using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>What happens to rows when permissions, scopes, subscriptions, the server model or the device's starting point change.</summary>
public sealed class SubscriptionLifecycleTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_permission_change_reconciles_and_removes_rows_the_user_may_no_longer_see()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.UseAuthContextProvider<MutableAuthProvider>());
        var auth = (MutableAuthProvider)harness.Services.GetRequiredService<IG9SyncAuthContextProvider>();
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 3);
        await harness.InsertPotsAsync(south, 3);

        auth.AllowedSites = [north, south];
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("allowed-pots");
        await client.SyncOkAsync();
        Assert.Equal(6, await client.CountAsync("Pot"));

        auth.AllowedSites = [north];
        await client.SyncOkAsync();
        Assert.True(client.InMemory.CallCount(G9ETransportOperation.Reconcile) >= 1, "An auth hash change must trigger reconciliation.");
        Assert.Equal(3, await client.CountAsync("Pot"));
        Assert.Equal(3L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE SiteId = $p0", Key(north)));
    }

    [PostgresFact]
    public async Task A_forbidden_subscription_is_reported_and_marked_failed_while_the_rest_sync()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.UseAuthContextProvider<MutableAuthProvider>());
        var auth = (MutableAuthProvider)harness.Services.GetRequiredService<IG9SyncAuthContextProvider>();
        auth.SubscriptionFilter = name => name != "site-pots";
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 2);

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        var forbidden = await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        var result = await client.SyncAsync();
        Assert.True(result.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(result));
        Assert.Contains(result.Problems, p => p.Key == forbidden.Key && p.Action == G9ESubscriptionAction.Forbidden);
        Assert.Equal(1, await client.CountAsync("Site"));
        Assert.Equal(0, await client.CountAsync("Pot"));

        var record = (await client.Engine.GetSubscriptionsAsync()).Single(s => s.Key == forbidden.Key);
        Assert.Equal(G9ESubscriptionState.Failed, record.State);
        Assert.Equal(G9SyncErrorCodes.SubscriptionForbidden, record.LastErrorCode);
    }

    [PostgresFact]
    public async Task A_row_moved_out_of_a_subscription_scope_is_removed_by_reconciliation()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 3);
        await using var client = await harness.CreateSubscribedClientAsync(north);
        var moved = (await harness.PotIdsAsync(north))[0];

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "SiteId" = $1 WHERE "Id" = $2""", south, moved);
        await client.SyncOkAsync();
        Assert.True(await client.ExistsAsync("Pot", moved), "A delta cannot see a row that left the scope (documented in 17-Known-Limitations).");

        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.False(await client.ExistsAsync("Pot", moved));
        Assert.Equal(2, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task An_expired_ephemeral_subscription_releases_only_rows_no_other_subscription_holds()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 3);
        await harness.InsertPotsAsync(south, 4);

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        var ephemeral = await client.Engine.SubscribeAsync("site-pots", SitePots(north),
            new G9CSubscriptionOptions { Ephemeral = true, Ttl = TimeSpan.FromMinutes(1) });
        await client.Engine.SubscribeAsync("site-pots", SitePots(south));
        await client.SyncOkAsync();
        Assert.Equal(7, await client.CountAsync("Pot"));

        Assert.Equal(0, await client.Store.CollectExpiredEphemeralAsync(DateTime.UtcNow, CancellationToken.None));
        Assert.Equal(1, await client.Store.CollectExpiredEphemeralAsync(DateTime.UtcNow.AddMinutes(5), CancellationToken.None));

        Assert.Equal(4, await client.CountAsync("Pot"));
        Assert.Equal(4L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE SiteId = $p0", Key(south)));
        Assert.DoesNotContain(await client.Engine.GetSubscriptionsAsync(), s => s.Key == ephemeral.Key);
    }

    [PostgresFact]
    public async Task A_column_the_server_starts_serving_is_backfilled_without_redownloading_rows()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(IncludePotNote: false)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 5);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'note-' || "Code" """);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        Assert.Equal(5L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note IS NULL"));

        // The next deployment serves Note.
        client.InMemory.Engine = await harness.CreateAdditionalServerAsync(m => ConfigureServer(m));
        var result = await client.SyncOkAsync();

        Assert.Equal(0L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note IS NULL"));
        Assert.Equal("note-P00001", await client.ScalarAsync("SELECT Note FROM Pot WHERE Code = 'P00001'"));
        Assert.Equal(0L, result.Changes.Upserts);
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task Rows_already_on_the_device_are_adopted_and_only_differences_move()
    {
        var site = Guid.NewGuid();
        var pots = Enumerable.Range(0, 20).Select(_ => Guid.NewGuid()).ToArray();
        await using var harness = await SyncHarness.CreateAsync(output, seedBeforeCapture: async dataSource =>
        {
            await using var command = dataSource.CreateCommand("""
                INSERT INTO farm."Site" ("Id", "Name") VALUES ($2, 'North');
                """);
            command.Parameters.Add(new NpgsqlParameter { Value = pots });
            command.Parameters.Add(new NpgsqlParameter { Value = site });
            await command.ExecuteNonQueryAsync();
            await using var insertPots = dataSource.CreateCommand("""
                INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status", "Note")
                SELECT u.id, $2, 'P' || lpad(u.ord::text, 5, '0'), 0, 'seeded' FROM unnest($1::uuid[]) WITH ORDINALITY AS u(id, ord)
                """);
            insertPots.Parameters.Add(new NpgsqlParameter { Value = pots });
            insertPots.Parameters.Add(new NpgsqlParameter { Value = site });
            await insertPots.ExecuteNonQueryAsync();
        });
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'changed-on-server' WHERE "Id" = $1""", pots[0]);

        await using var client = await harness.CreateClientAsync(prepareDatabase: async path =>
        {
            // The device already holds the data, written by an earlier sync system.
            await using var connection = new SqliteConnection("Data Source=" + path);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE Site (Id TEXT NOT NULL PRIMARY KEY, Name TEXT NOT NULL, IsActive INTEGER NOT NULL);
                CREATE TABLE Pot (Id TEXT NOT NULL PRIMARY KEY, SiteId TEXT NOT NULL, Code TEXT NOT NULL, Status INTEGER NOT NULL,
                                  Weight REAL NULL, PlantedAt TEXT NULL, Note TEXT NULL, IsDeleted INTEGER NOT NULL);
                """;
            await command.ExecuteNonQueryAsync();
            command.CommandText = "INSERT INTO Site VALUES ($site, 'North', 1)";
            command.Parameters.AddWithValue("$site", Key(site));
            await command.ExecuteNonQueryAsync();
            for (var i = 0; i < pots.Length; i++)
            {
                command.CommandText = "INSERT INTO Pot VALUES ($id, $site, $code, 0, NULL, NULL, $note, 0)";
                command.Parameters.Clear();
                command.Parameters.AddWithValue("$id", Key(pots[i]));
                command.Parameters.AddWithValue("$site", Key(site));
                command.Parameters.AddWithValue("$code", "P" + (i + 1).ToString("00000", System.Globalization.CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue("$note", i == 1 ? "edited-on-device" : "seeded");
                await command.ExecuteNonQueryAsync();
            }
        });

        Assert.Equal(1, await client.Engine.MarkDirtyAsync("Pot", [Key(pots[1])]));
        var adopt = new G9CSubscriptionOptions { AdoptExistingRows = true };
        await client.Engine.SubscribeAsync("sites", options: adopt);
        await client.Engine.SubscribeAsync("site-pots", SitePots(site), adopt);
        var result = await client.SyncOkAsync();

        Assert.Equal("edited-on-device", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[1]));
        Assert.Equal("changed-on-server", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal(20, await client.CountAsync("Pot"));
        Assert.Equal(0, result.Downloaded);
        Assert.True(result.Changes.MembershipAdded >= 19, SyncClient.Describe(result) + $"; membership added {result.Changes.MembershipAdded}");
        Assert.True(result.Changes.Upserts <= 2, $"Only the rows that differ may move; upserts {result.Changes.Upserts}.");
    }
}
