using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.IntegrationTests.Scopes;
using G9SyncData.Server.Caching;
using G9SyncData.Server.Model;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Caching;

/// <summary>
///     The digest cache (ADR-0024) as the one thing it must never be: wrong. Every test changes the data in a way a naive cache
///     would miss, reconciles through the cache, and compares the device with the server.
/// </summary>
public sealed class DigestCacheIntegrationTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Changes_made_since_an_entry_was_valid_are_applied_and_a_second_reconciliation_is_served_from_the_cache()
    {
        await using var harness = await CreateHarnessAsync();
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 120);
        await using var client = await harness.CreateSubscribedClientAsync(north);
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });   // derives and caches both views
        Assert.Equal(2, Statistics(harness).Entries);                              // sites, and this site's pots

        var pots = await harness.PotIdsAsync(north);
        var added = Guid.NewGuid();
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'edited' WHERE "Id" = $1""", pots[0]);
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[1]);
        await harness.InsertPotAsync(added, north, "ADDED");
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "SiteId" = $1 WHERE "Id" = $2""", south, pots[2]);   // leaves the scope
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "IsDeleted" = true WHERE "Id" = $1""", pots[3]);      // fails When

        // The delta cannot see the pot that left the scope (17-Known-Limitations); reconciliation must, through the cache.
        await client.SyncOkAsync();
        Assert.True(await client.ExistsAsync("Pot", pots[2]));
        var hitsBefore = Statistics(harness).Hits;
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });

        Assert.True(Statistics(harness).Hits > hitsBefore, "The second reconciliation must use the cached entry.");
        await AssertDeviceMatchesServerAsync(harness, client, north);
        Assert.False(await client.ExistsAsync("Pot", pots[2]));
        Assert.True(await client.ExistsAsync("Pot", added));

        // Nothing changed: served from the cache again, and nothing moves.
        var result = await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(0L, result.Changes.Upserts + result.Changes.Deletes + result.Changes.MoveOuts);
    }

    [PostgresFact]
    public async Task A_change_in_another_table_the_scope_reads_forces_derivation_instead_of_a_stale_answer()
    {
        await using var harness = await CreateHarnessAsync();
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 40);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("active-site-pots");
        await client.SyncOkAsync();                                               // a first sync pulls; it does not reconcile
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });   // caches both views
        Assert.Equal(40, await client.CountAsync("Pot"));
        Assert.Equal(2, Statistics(harness).Entries);

        // No pot changes; the SITE does. A cache that only watched farm.Pot would report "nothing to do".
        await harness.ExecServerAsync("""UPDATE farm."Site" SET "IsActive" = false WHERE "Id" = $1""", north);
        var hitsBefore = Statistics(harness).Hits;
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });

        Assert.True(Statistics(harness).Hits > hitsBefore, "The cached entry must have been found - and then refused.");
        Assert.Equal(0, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task A_delete_whose_tombstone_was_purged_after_the_entry_is_still_removed()
    {
        await using var harness = await CreateHarnessAsync();
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 30);
        await using var client = await harness.CreateSubscribedClientAsync(north);
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });

        var pots = await harness.PotIdsAsync(north);
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[5]);
        await harness.ExecServerAsync("""UPDATE g9sync."farm__Pot" SET changed_at = now() - interval '1 day' WHERE deleted""");
        Assert.True(await harness.Store.CleanupTombstonesAsync(TimeSpan.FromHours(1), CancellationToken.None) >= 1);

        await client.SyncUntilSettledAsync(new G9CSyncRequest { ForceReconcile = true });

        Assert.False(await client.ExistsAsync("Pot", pots[5]));
        await AssertDeviceMatchesServerAsync(harness, client, north);
    }

    [PostgresFact]
    public async Task More_changes_than_the_incremental_limit_are_derived_and_still_exact()
    {
        await using var harness = await CreateHarnessAsync(o => o.MaxIncrementalKeys = 3);
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 50);
        await using var client = await harness.CreateSubscribedClientAsync(north);
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = "Status" + 1 WHERE "Code" <= 'P00020'""");
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Code" > 'P00045'""");
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });

        await AssertDeviceMatchesServerAsync(harness, client, north);
    }

    [PostgresFact]
    public async Task A_scope_reading_a_table_that_is_not_synced_is_never_cached_and_stays_correct()
    {
        await using var harness = await CreateHarnessAsync();
        var auth = EfScopeFixture.Auth(harness);
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 20);
        await harness.ExecServerAsync("""INSERT INTO farm."SiteMember" ("SiteId", "UserId") VALUES ($1, 'tester')""", north);

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("ef-member-pots");
        await client.SyncOkAsync();                                               // a first sync pulls; it does not reconcile
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(20, await client.CountAsync("Pot"));
        Assert.Equal(1, Statistics(harness).Entries);                              // "sites" only: the pots' scope reads SiteMember

        await harness.ExecServerAsync("""DELETE FROM farm."SiteMember" WHERE "SiteId" = $1""", north);
        auth.PermissionStamp = "v2";
        await client.SyncOkAsync();
        Assert.Equal(0, await client.CountAsync("Pot"));
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync(Action<G9CDigestCacheOptions>? cache = null) =>
        EfScopeFixture.CreateHarnessAsync(output,
            model: model =>
            {
                EfScopeFixture.Model(model);
                model.Subscription("active-site-pots", s => s
                    .Include("farm.Pot", """EXISTS (SELECT 1 FROM farm."Site" AS s WHERE s."Id" = {row}."SiteId" AND s."IsActive")""",
                        "NOT {row}.\"IsDeleted\"")
                    .DependsOn("sites"));
            },
            server: builder => builder.UseDigestCache(cache));

    private static G9CDigestCacheStatistics Statistics(SyncHarness harness) =>
        harness.Services.GetRequiredService<IG9DigestCache>().GetStatistics();

    /// <summary>The device holds exactly the server's visible pots of the site, at the server's versions.</summary>
    private static async Task AssertDeviceMatchesServerAsync(SyncHarness harness, SyncClient client, Guid site)
    {
        var expected = new List<string>();
        foreach (var id in await harness.PotIdsAsync(site))
        {
            if (await harness.ScalarServerAsync<bool>("""SELECT NOT "IsDeleted" FROM farm."Pot" WHERE "Id" = $1""", id)) expected.Add(Key(id));
        }

        foreach (var extra in await ServerKeysAsync(harness, site)) if (!expected.Contains(extra)) expected.Add(extra);
        var actual = await EfCoreScopeTests.PotIdsAsync(client);
        Assert.Equal(expected.Order(StringComparer.Ordinal), actual.Order(StringComparer.Ordinal));
    }

    private static async Task<List<string>> ServerKeysAsync(SyncHarness harness, Guid site)
    {
        var keys = new List<string>();
        await using var command = harness.DataSource.CreateCommand("""SELECT "Id" FROM farm."Pot" WHERE "SiteId" = $1 AND NOT "IsDeleted" """);
        command.Parameters.Add(new Npgsql.NpgsqlParameter { Value = site });
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) keys.Add(Key(reader.GetGuid(0)));
        return keys;
    }
}
