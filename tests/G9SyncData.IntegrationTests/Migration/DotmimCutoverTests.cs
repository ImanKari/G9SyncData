using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Tests.Shared;
using Microsoft.Data.Sqlite;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Migration;

/// <summary>
///     A device moving from Dotmim to G9SyncData (ADR-0038, AiGuides/16 step 4), end to end: the device database is what Dotmim left - its
///     tables, capture and scope tables, plus work the user did offline after the last Dotmim sync. After the cut-over that work reaches the
///     server exactly once, the device adopts its rows instead of downloading them, and ordinary sync carries on.
/// </summary>
public sealed class DotmimCutoverTests(ITestOutputHelper output)
{
    private const long LastSync = 20260101000000000;

    [PostgresFact]
    public async Task Offline_work_from_the_Dotmim_era_reaches_the_server_once_and_the_device_then_syncs_normally()
    {
        var site = Guid.NewGuid();
        var pots = Enumerable.Range(0, 6).Select(_ => Guid.NewGuid()).ToArray();
        var created = Guid.NewGuid();
        await using var harness = await SyncHarness.CreateAsync(output, seedBeforeCapture: async dataSource =>
        {
            await using var seed = dataSource.CreateCommand("""
                INSERT INTO farm."Site" ("Id", "Name") VALUES ($1, 'North');
                """);
            seed.Parameters.Add(new NpgsqlParameter { Value = site });
            await seed.ExecuteNonQueryAsync();
            await using var insertPots = dataSource.CreateCommand("""
                INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status", "Note")
                SELECT u.id, $2, 'P' || u.ord, 0, 'server' FROM unnest($1::uuid[]) WITH ORDINALITY AS u(id, ord)
                """);
            insertPots.Parameters.Add(new NpgsqlParameter { Value = pots });
            insertPots.Parameters.Add(new NpgsqlParameter { Value = site });
            await insertPots.ExecuteNonQueryAsync();
        });

        await using var client = await harness.CreateClientAsync(prepareDatabase: path => PrepareDotmimDeviceAsync(path, site, pots, created));

        var retirement = await client.Store.RetireDotmimCaptureAsync();
        Assert.Equal(6, retirement.DroppedTriggers.Count);                          // Site and Pot
        Assert.Equal(new[] { "scope_info", "scope_info_client" }, retirement.DroppedScopeTables);
        Assert.Empty(retirement.RemainingForeignTriggers);
        Assert.Equal(2, retirement.ImportedUpserts["Pot"]);
        Assert.Equal(1, retirement.ImportedDeletes["Pot"]);

        var adopt = new G9CSubscriptionOptions { AdoptExistingRows = true };
        await client.Engine.SubscribeAsync("sites", options: adopt);
        await client.Engine.SubscribeAsync("site-pots", SitePots(site), adopt);
        var first = await client.SyncOkAsync();

        Assert.Equal(3, first.Pushed);
        Assert.Equal(0, first.Rejected);
        Assert.Equal("edited offline", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", pots[1]));
        Assert.Equal("created offline", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal(await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "SiteId" = $1""", site), await client.CountAsync("Pot"));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
        Assert.True(first.Downloaded <= 3, $"Adopted rows must not be downloaded again; downloaded {first.Downloaded}.");

        // A second run pushes nothing again (exactly once), and the device follows server changes as usual.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'changed on server' WHERE "Id" = $1""", pots[2]);
        var second = await client.SyncOkAsync();
        Assert.Equal(0, second.Pushed);
        Assert.Equal("changed on server", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[2])));
        Assert.Equal(0L, await client.ScalarAsync("SELECT count(*) FROM sqlite_master WHERE name LIKE '%\\_tracking' ESCAPE '\\'"));

        // A local edit after the cut-over is G9SyncData's to upload - and only once.
        await client.ExecAsync("UPDATE Pot SET Note = 'after cut-over' WHERE Id = $p0", Key(pots[3]));
        var third = await client.SyncOkAsync();
        Assert.Equal(1, third.Pushed);
        Assert.Equal("after cut-over", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[3]));
    }

    [PostgresFact]
    public async Task Cutting_over_twice_and_syncing_in_between_uploads_nothing_twice()
    {
        var site = Guid.NewGuid();
        var pots = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        await using var harness = await SyncHarness.CreateAsync(output, seedBeforeCapture: async dataSource =>
        {
            await using var seed = dataSource.CreateCommand("""INSERT INTO farm."Site" ("Id", "Name") VALUES ($1, 'North')""");
            seed.Parameters.Add(new NpgsqlParameter { Value = site });
            await seed.ExecuteNonQueryAsync();
            await using var insertPots = dataSource.CreateCommand("""
                INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status", "Note")
                SELECT u.id, $2, 'P' || u.ord, 0, 'server' FROM unnest($1::uuid[]) WITH ORDINALITY AS u(id, ord)
                """);
            insertPots.Parameters.Add(new NpgsqlParameter { Value = pots });
            insertPots.Parameters.Add(new NpgsqlParameter { Value = site });
            await insertPots.ExecuteNonQueryAsync();
        });
        await using var client = await harness.CreateClientAsync(prepareDatabase: path => PrepareDotmimDeviceAsync(path, site, pots, created: null));

        await client.Store.RetireDotmimCaptureAsync();
        var adopt = new G9CSubscriptionOptions { AdoptExistingRows = true };
        await client.Engine.SubscribeAsync("sites", options: adopt);
        await client.Engine.SubscribeAsync("site-pots", SitePots(site), adopt);
        Assert.Equal(2, (await client.SyncOkAsync()).Pushed);                       // the offline edit and the offline delete

        // The app runs its cut-over step on every start: nothing is found, nothing is imported, nothing moves.
        var again = await client.Store.RetireDotmimCaptureAsync();
        Assert.False(again.ChangedAnything);
        Assert.Empty(again.ImportedUpserts);
        Assert.Equal(0, (await client.SyncOkAsync()).Pushed);
    }

    /// <summary>
    ///     An offline edit from the Dotmim era meets an office edit of ANOTHER column of the same row (LES-0080). Dotmim tracked rows,
    ///     not columns, so the import sends every column: the server applies the device's edit and keeps the office's column, and
    ///     the column the office changed comes back to the device as a kept conflict — the device cannot tell its stale value from a
    ///     real edit, so the operator decides (AgriPad's sheet asks before «send again» would overwrite the office's value).
    /// </summary>
    [PostgresFact]
    public async Task A_Dotmim_era_edit_merges_with_an_office_edit_and_the_office_column_is_kept_as_a_conflict()
    {
        var site = Guid.NewGuid();
        var pots = Enumerable.Range(0, 3).Select(_ => Guid.NewGuid()).ToArray();
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge)),
            seedBeforeCapture: async dataSource =>
        {
            await using var seed = dataSource.CreateCommand("""INSERT INTO farm."Site" ("Id", "Name") VALUES ($1, 'North')""");
            seed.Parameters.Add(new NpgsqlParameter { Value = site });
            await seed.ExecuteNonQueryAsync();
            await using var insertPots = dataSource.CreateCommand("""
                INSERT INTO farm."Pot" ("Id", "SiteId", "Code", "Status", "Note")
                SELECT u.id, $2, 'P' || u.ord, 0, 'server' FROM unnest($1::uuid[]) WITH ORDINALITY AS u(id, ord)
                """);
            insertPots.Parameters.Add(new NpgsqlParameter { Value = pots });
            insertPots.Parameters.Add(new NpgsqlParameter { Value = site });
            await insertPots.ExecuteNonQueryAsync();
        });
        await using var client = await harness.CreateClientAsync(prepareDatabase: path => PrepareDotmimDeviceAsync(path, site, pots, created: null),
            store: s => s.KeepLostConflicts = true);

        // The office edits ANOTHER column of the row the device edited offline, after the device's last Dotmim sync.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Weight" = 7 WHERE "Id" = $1""", pots[0]);

        await client.Store.RetireDotmimCaptureAsync();
        var adopt = new G9CSubscriptionOptions { AdoptExistingRows = true };
        await client.Engine.SubscribeAsync("sites", options: adopt);
        await client.Engine.SubscribeAsync("site-pots", SitePots(site), adopt);
        var first = await client.SyncAsync();

        Assert.True(first.Outcome == G9ESyncOutcome.CompletedWithRejections, SyncClient.Describe(first));
        // The server: the device's edit applied, the office's column kept.
        Assert.Equal("edited offline", await harness.ScalarServerAsync<string>("""SELECT "Note" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        Assert.Equal(7m, await harness.ScalarServerAsync<decimal>("""SELECT "Weight" FROM farm."Pot" WHERE "Id" = $1""", pots[0]));
        // The device: its (stale) value of the office's column is held as a conflict naming exactly that column, never sent silently.
        var conflict = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal("G9S-CON-COLUMNS", conflict.Code);
        Assert.Equal("Weight", conflict.Column);
        Assert.Contains("7", conflict.Details, StringComparison.Ordinal);
    }

    /// <summary>
    ///     What Dotmim leaves on a device: Site and Pot as Dotmim created them, its capture and scope tables, the server rows marked as
    ///     downloaded before the last sync - then offline work: an edit of pots[0], a delete of pots[1] and, optionally, a new pot.
    /// </summary>
    private static async Task PrepareDotmimDeviceAsync(string path, Guid site, Guid[] pots, Guid? created)
    {
        await using var connection = new SqliteConnection("Data Source=" + path);
        await connection.OpenAsync();

        async Task ExecAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync();
        }

        await ExecAsync(DotmimDeviceSql.ScopeTables);
        await ExecAsync(DotmimDeviceSql.ScopeClient("INITIAL_V2", LastSync));
        await ExecAsync(DotmimDeviceSql.ScopeClient("FARM_V4", LastSync + 1000));
        await ExecAsync("""
            CREATE TABLE [Site] ([Id] text NOT NULL COLLATE NOCASE, [Name] text NOT NULL COLLATE NOCASE, [IsActive] integer NOT NULL, PRIMARY KEY ([Id]));
            CREATE TABLE [Pot] ([Id] text NOT NULL COLLATE NOCASE, [SiteId] text NOT NULL COLLATE NOCASE, [Code] text NOT NULL COLLATE NOCASE,
                [Status] integer NOT NULL, [Weight] real NULL, [PlantedAt] text NULL, [Note] text NULL COLLATE NOCASE, [IsDeleted] integer NOT NULL,
                PRIMARY KEY ([Id]), FOREIGN KEY ([SiteId]) REFERENCES [Site]([Id]));
            """);
        await ExecAsync(DotmimDeviceSql.Capture("Site"));
        await ExecAsync(DotmimDeviceSql.Capture("Pot"));

        await ExecAsync($"INSERT INTO Site VALUES ('{Key(site)}', 'North', 1)");
        for (var i = 0; i < pots.Length; i++) await ExecAsync($"INSERT INTO Pot VALUES ('{Key(pots[i])}', '{Key(site)}', 'P{i + 1}', 0, NULL, NULL, 'server', 0)");
        await ExecAsync(DotmimDeviceSql.MarkSynced("Site", LastSync - 5000));
        await ExecAsync(DotmimDeviceSql.MarkSynced("Pot", LastSync - 5000));

        await ExecAsync($"UPDATE Pot SET Note = 'edited offline' WHERE Id = '{Key(pots[0])}'");
        await ExecAsync($"DELETE FROM Pot WHERE Id = '{Key(pots[1])}'");
        if (created is { } id) await ExecAsync($"INSERT INTO Pot VALUES ('{Key(id)}', '{Key(site)}', 'P-new', 0, NULL, NULL, 'created offline', 0)");
    }
}
