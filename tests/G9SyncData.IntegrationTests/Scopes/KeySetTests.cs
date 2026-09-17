using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server.PostgreSql;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Scopes;

/// <summary>
///     Materialised visibility key sets (ADR-0030): one build per part, shared by identical parts, rebuilt when lost, never
///     the reason a row is skipped, and swept when idle.
/// </summary>
public sealed class KeySetTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_provider_snapshot_is_paged_through_one_key_set_that_is_released_when_the_part_completes()
    {
        await using var harness = await EfScopeFixture.CreateHarnessAsync(output);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 180);
        await harness.InsertPotsAsync(south, 40);
        EfScopeFixture.Auth(harness).AllowedSites = [north];

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("ef-allowed-pots");
        await client.SyncOkAsync();

        Assert.Equal(180, await client.CountAsync("Pot"));
        Assert.Equal(180L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE SiteId = $p0", Key(north)));
        Assert.Equal(0L, await KeySetCountAsync(harness));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>($"SELECT count(*) FROM g9sync.{KeyTable(harness)}"));
    }

    [PostgresFact]
    public async Task Identical_parts_requested_concurrently_share_one_build()
    {
        await using var harness = await EfScopeFixture.CreateHarnessAsync(output);
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 120);
        var snapshot = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);

        var estimates = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            harness.Store.EstimatePartAsync(EfScopeFixture.AllowedPotsPart(harness, [north], G9EPullPartKind.Snapshot, snapshot), CancellationToken.None)));

        Assert.All(estimates, e => Assert.Equal(120, e.Rows));
        Assert.Equal(1L, await KeySetCountAsync(harness));

        // A different caller (other allowed sites) at the same snapshot is different content: its own set.
        await harness.Store.EstimatePartAsync(EfScopeFixture.AllowedPotsPart(harness, [north, Guid.NewGuid()], G9EPullPartKind.Snapshot, snapshot),
            CancellationToken.None);
        Assert.Equal(2L, await KeySetCountAsync(harness));
    }

    [PostgresFact]
    public async Task A_key_set_lost_mid_part_is_rebuilt_and_no_row_is_skipped_or_repeated()
    {
        await using var harness = await EfScopeFixture.CreateHarnessAsync(output);
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 175);
        var snapshot = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        var request = EfScopeFixture.AllowedPotsPart(harness, [north], G9EPullPartKind.Snapshot, snapshot);

        var keys = new List<string>();
        var pages = 0;
        await foreach (var page in harness.Store.ReadPartAsync(request, CancellationToken.None))
        {
            keys.AddRange(Keys(page));
            if (++pages == 1)
            {
                // Simulates the sweep, another reader releasing a shared set, or a crash truncating the UNLOGGED tables.
                await harness.ExecServerAsync($"DELETE FROM g9sync.key_sets; DELETE FROM g9sync.{KeyTable(harness)}");
            }
        }

        Assert.Equal(175, keys.Count);
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Equal(0L, await KeySetCountAsync(harness));
    }

    [PostgresFact]
    public async Task A_row_changed_after_the_build_is_left_to_the_next_delta_which_applies_the_scope_live()
    {
        await using var harness = await EfScopeFixture.CreateHarnessAsync(output);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 60);
        var pots = await harness.PotIdsAsync(north);
        var snapshot = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        var request = EfScopeFixture.AllowedPotsPart(harness, [north], G9EPullPartKind.Snapshot, snapshot);
        await harness.Store.EstimatePartAsync(request, CancellationToken.None);   // builds the set, as a plan does

        // After the build: one pot edited in scope, one moved out of scope.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'late edit' WHERE "Id" = $1""", pots[0]);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "SiteId" = $1, "Code" = 'MOVED' WHERE "Id" = $2""", south, pots[1]);

        var snapshotKeys = new List<string>();
        await foreach (var page in harness.Store.ReadPartAsync(request, CancellationToken.None)) snapshotKeys.AddRange(Keys(page));
        Assert.Equal(58, snapshotKeys.Count);
        Assert.DoesNotContain(Key(pots[0]), snapshotKeys);
        Assert.DoesNotContain(Key(pots[1]), snapshotKeys);   // never sent with its out-of-scope values

        var next = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        var deltaKeys = new List<string>();
        await foreach (var page in harness.Store.ReadPartAsync(
                           EfScopeFixture.AllowedPotsPart(harness, [north], G9EPullPartKind.Delta, next, previousSnapshot: snapshot), CancellationToken.None))
        {
            deltaKeys.AddRange(Keys(page));
        }

        Assert.Equal([Key(pots[0])], deltaKeys);
    }

    [PostgresFact]
    public async Task An_idle_key_set_is_swept_with_its_keys_and_a_later_read_rebuilds_it()
    {
        await using var harness = await EfScopeFixture.CreateHarnessAsync(output);
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 30);
        var snapshot = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        var request = EfScopeFixture.AllowedPotsPart(harness, [north], G9EPullPartKind.Snapshot, snapshot);
        await harness.Store.EstimatePartAsync(request, CancellationToken.None);
        var store = harness.Services.GetRequiredService<G9CPostgresSyncStore>();

        Assert.Equal(0, await store.CleanupKeySetsAsync(CancellationToken.None));
        await harness.ExecServerAsync("UPDATE g9sync.key_sets SET last_used_at = now() - interval '1 day'");
        Assert.Equal(1, await store.CleanupKeySetsAsync(CancellationToken.None));
        Assert.Equal(0L, await harness.ScalarServerAsync<long>($"SELECT count(*) FROM g9sync.{KeyTable(harness)}"));

        var rows = 0;
        await foreach (var page in harness.Store.ReadPartAsync(request, CancellationToken.None)) rows += page.RowCount;
        Assert.Equal(30, rows);
    }

    [PostgresFact]
    public async Task Reconciliation_pages_a_provider_scope_through_a_transaction_local_key_table()
    {
        await using var harness = await EfScopeFixture.CreateHarnessAsync(output);
        var auth = EfScopeFixture.Auth(harness);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 140);
        await harness.InsertPotsAsync(south, 160);
        auth.AllowedSites = [north, south];

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("ef-allowed-pots");
        await client.SyncOkAsync();
        Assert.Equal(300, await client.CountAsync("Pot"));

        // Revoke the larger site: reconciliation must list 140 surviving keys over several pages (PageRows 50) and remove 160.
        auth.AllowedSites = [north];
        var result = await client.SyncOkAsync();
        Assert.Equal(140, await client.CountAsync("Pot"));
        Assert.Equal(140L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE SiteId = $p0", Key(north)));
        Assert.Equal(0L, result.Changes.Upserts);
    }

    [PostgresTheory]
    [InlineData(G9EKeySetScopes.All, 1L)]
    [InlineData(G9EKeySetScopes.ProviderScopes, 0L)]
    public async Task A_SQL_template_scope_uses_a_key_set_unless_configured_otherwise_and_delivers_the_same_rows(G9EKeySetScopes mode, long expectedSets)
    {
        await using var harness = await EfScopeFixture.CreateHarnessAsync(output, postgres: o => o.KeySets.Scopes = mode);
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 130);
        await harness.InsertPotAsync(Guid.NewGuid(), north, "GONE", isDeleted: true);

        var definition = harness.Engine.Model.FindSubscription("site-pots")!;
        var include = definition.Tables.Single();
        var parameters = SitePots(north);
        var request = new G9CStorePartRequest
        {
            Table = include.Table, Include = include, Kind = G9EPullPartKind.Snapshot, Columns = include.Table.Columns, PageRows = 50,
            NewSnapshot = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None),
            Subscription = new G9CResolvedSubscription
            {
                Definition = definition,
                Key = G9SyncData.G9CSyncParameters.BuildSubscriptionKey("site-pots", parameters),
                Parameters = parameters,
                Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["SiteId"] = north },
                AuthHash = string.Empty
            }
        };

        await harness.Store.EstimatePartAsync(request, CancellationToken.None);
        Assert.Equal(expectedSets, await KeySetCountAsync(harness));

        var keys = new List<string>();
        await foreach (var page in harness.Store.ReadPartAsync(request, CancellationToken.None)) keys.AddRange(Keys(page));
        Assert.Equal(130, keys.Count);   // the soft-deleted pot fails the row-state filter on both paths
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
        Assert.Equal(0L, await KeySetCountAsync(harness));

        await using var client = await harness.CreateSubscribedClientAsync(north);
        Assert.Equal(130, await client.CountAsync("Pot"));
    }

    // ================================================================ helpers

    private static Task<long> KeySetCountAsync(SyncHarness harness) => harness.ScalarServerAsync<long>("SELECT count(*) FROM g9sync.key_sets");

    private static string KeyTable(SyncHarness harness) =>
        "\"" + G9SyncData.Server.PostgreSql.Internal.PgNames.TrackingBare("farm", "Pot") + "_keys\"";

    private static List<string> Keys(G9CStorePage page)
    {
        var keys = new List<string>();
        using var reader = G9CRowBatchReader.Open(page.Frame);
        while (reader.NextSection())
        {
            while (reader.ReadRow()) keys.Add(reader.Key);
        }

        return keys;
    }
}
