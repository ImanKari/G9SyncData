using System.Text.Json;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.PostgreSql;
using G9SyncData.Server.PostgreSql.Internal;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Scopes;

/// <summary>
///     A page must cost O(page) wherever it falls in its part (LES-0037). Two plans made a 500 k-row snapshot quadratic - 14 s on the server
///     for what should take a few: a merge join that scanned the joined table from its first key on every page, and a fresh key table
///     without statistics read by a bitmap scan of every remaining key plus a sort. Results were always right, so only the plans can show
///     it: these run EXPLAIN ANALYZE on the real statements, deep inside a large part, and count the rows each relation delivered.
/// </summary>
public sealed class PagingCostTests(ITestOutputHelper output)
{
    private const int Pots = 20_000;
    private const int PageRows = 500;

    [PostgresFact]
    public async Task A_snapshot_page_deep_in_a_large_part_reads_only_about_its_own_rows_of_the_tracking_table()
    {
        await using var harness = await SyncHarness.CreateAsync(output, postgres: o => o.KeySets.Scopes = G9EKeySetScopes.ProviderScopes);
        var site = await SeedAsync(harness);
        var request = SitePotsPart(harness, site, G9EPullPartKind.Snapshot, await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None));
        var after = await KeyAtAsync(harness, 15_000);

        var read = await RowsReadPerRelationAsync(harness, request, after);

        output.WriteLine(string.Join(", ", read.Select(r => $"{r.Key}={r.Value}")));
        Assert.True(read.GetValueOrDefault(Tracking) <= 3 * PageRows, $"The page read {read.GetValueOrDefault(Tracking)} tracking rows for {PageRows}.");
        Assert.True(read.GetValueOrDefault("Pot") <= 3 * PageRows, $"The page read {read.GetValueOrDefault("Pot")} pots for {PageRows}.");
    }

    [PostgresFact]
    public async Task A_delta_page_deep_in_a_large_part_reads_only_about_its_own_rows_of_the_base_table()
    {
        await using var harness = await SyncHarness.CreateAsync(output, postgres: o => o.KeySets.Scopes = G9EKeySetScopes.ProviderScopes);
        var site = await SeedAsync(harness);
        var previous = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'delta'""");
        await harness.WaitForCaptureAsync();
        await AnalyzeAsync(harness);
        var request = SitePotsPart(harness, site, G9EPullPartKind.Delta, await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None), previous);
        var after = await KeyAtAsync(harness, 15_000);

        var read = await RowsReadPerRelationAsync(harness, request, after);

        output.WriteLine(string.Join(", ", read.Select(r => $"{r.Key}={r.Value}")));
        Assert.True(read.GetValueOrDefault("Pot") <= 3 * PageRows, $"The page read {read.GetValueOrDefault("Pot")} pots for {PageRows}.");
        Assert.True(read.GetValueOrDefault(Tracking) <= 3 * PageRows, $"The page read {read.GetValueOrDefault(Tracking)} tracking rows for {PageRows}.");
    }

    [PostgresFact]
    public async Task A_large_key_set_refreshes_its_tables_statistics_so_a_deep_page_is_read_in_key_order_without_a_sort()
    {
        await using var harness = await SyncHarness.CreateAsync(output);   // SQL template scopes use key sets by default
        var site = await SeedAsync(harness, analyze: false);
        var request = SitePotsPart(harness, site, G9EPullPartKind.Snapshot, await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None));

        await harness.Store.EstimatePartAsync(request, CancellationToken.None);   // builds the set, as a plan does

        var keyTable = "g9sync.\"" + PgNames.TrackingBare("farm", "Pot") + "_keys\"";
        Assert.True(await harness.ScalarServerAsync<double>($"SELECT reltuples::float8 FROM pg_class WHERE oid = '{keyTable}'::regclass") >= Pots,
            "The key table was not analysed after a set larger than its statistics was built.");

        var setId = await harness.ScalarServerAsync<long>("SELECT set_id FROM g9sync.key_sets");
        var plan = await ExplainAsync(harness,
            $"SELECT key FROM {keyTable} WHERE set_id = $1 AND key > $2::uuid ORDER BY key LIMIT {PageRows}",
            [new NpgsqlParameter { Value = setId }, new NpgsqlParameter { Value = await KeyAtAsync(harness, 15_000) }]);
        var nodes = Nodes(plan).ToList();
        Assert.DoesNotContain(nodes, n => n.GetProperty("Node Type").GetString() is "Sort" or "Bitmap Heap Scan");
        Assert.True(RowsRead(nodes).GetValueOrDefault(PgNames.TrackingBare("farm", "Pot") + "_keys") <= 3 * PageRows);

        // And the part still delivers every row exactly once, in key order.
        var keys = new List<string>();
        await foreach (var page in harness.Store.ReadPartAsync(request, CancellationToken.None))
        {
            using var reader = G9SyncData.Protocol.RowBatch.G9CRowBatchReader.Open(page.Frame);
            while (reader.NextSection())
            {
                while (reader.ReadRow()) keys.Add(reader.Key);
            }
        }

        Assert.Equal(Pots, keys.Count);
        Assert.Equal(keys.Order(StringComparer.Ordinal), keys);
    }

    // ================================================================ helpers

    private static string Tracking => PgNames.TrackingBare("farm", "Pot");

    private static async Task<Guid> SeedAsync(SyncHarness harness, bool analyze = true)
    {
        var site = Guid.NewGuid();
        var other = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertSiteAsync(other, "South");
        await harness.InsertPotsAsync(site, Pots);
        await harness.InsertPotsAsync(other, 5_000, "S");
        await harness.WaitForCaptureAsync();
        if (analyze) await AnalyzeAsync(harness);
        return site;
    }

    /// <summary>Production tables have statistics; the plans measured here must be the plans a real server chooses.</summary>
    private static Task AnalyzeAsync(SyncHarness harness) =>
        harness.ExecServerAsync($"ANALYZE farm.\"Pot\"; ANALYZE g9sync.\"{Tracking}\"");

    /// <summary>The key a page resumes after when <paramref name="index" /> of the site's pots have been sent.</summary>
    private static async Task<string> KeyAtAsync(SyncHarness harness, int index) =>
        (await harness.ScalarServerAsync<string>($"""SELECT "Id"::text FROM farm."Pot" ORDER BY "Id" OFFSET {index} LIMIT 1"""))!;

    private static G9CStorePartRequest SitePotsPart(SyncHarness harness, Guid site, G9EPullPartKind kind, string snapshot, string? previous = null)
    {
        var definition = harness.Engine.Model.FindSubscription("site-pots")!;
        var include = definition.Tables.Single();
        var parameters = SitePots(site);
        return new G9CStorePartRequest
        {
            Table = include.Table, Include = include, Kind = kind, Columns = include.Table.Columns, PageRows = PageRows,
            NewSnapshot = snapshot, PreviousSnapshot = previous,
            Subscription = new G9CResolvedSubscription
            {
                Definition = definition,
                Key = G9CSyncParameters.BuildSubscriptionKey("site-pots", parameters),
                Parameters = parameters,
                Values = new Dictionary<string, object?>(StringComparer.Ordinal) { ["SiteId"] = site },
                AuthHash = string.Empty
            }
        };
    }

    private static async Task<Dictionary<string, long>> RowsReadPerRelationAsync(SyncHarness harness, G9CStorePartRequest request, string after)
    {
        var store = harness.Services.GetRequiredService<G9CPostgresSyncStore>();
        var (sql, parameters) = await store.DescribePartPageAsync(request, after, CancellationToken.None);
        return RowsRead(Nodes(await ExplainAsync(harness, sql, parameters)));
    }

    private static async Task<JsonElement> ExplainAsync(SyncHarness harness, string sql, IReadOnlyList<NpgsqlParameter> parameters)
    {
        await using var command = harness.DataSource.CreateCommand("EXPLAIN (ANALYZE, FORMAT JSON) " + sql);
        foreach (var parameter in parameters) command.Parameters.Add(parameter);
        var json = (string)(await command.ExecuteScalarAsync())!;
        using var document = JsonDocument.Parse(json);
        return document.RootElement[0].GetProperty("Plan").Clone();
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        yield return node;
        if (!node.TryGetProperty("Plans", out var children)) yield break;
        foreach (var child in children.EnumerateArray())
        {
            foreach (var descendant in Nodes(child)) yield return descendant;
        }
    }

    /// <summary>Rows each relation's scans delivered, over all their loops.</summary>
    private static Dictionary<string, long> RowsRead(IEnumerable<JsonElement> nodes)
    {
        var read = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var node in nodes)
        {
            if (!node.TryGetProperty("Relation Name", out var relation)) continue;
            var loops = node.GetProperty("Actual Loops").GetDouble();
            var rows = (long)(node.GetProperty("Actual Rows").GetDouble() * loops);
            if (node.TryGetProperty("Rows Removed by Filter", out var removed)) rows += (long)(removed.GetDouble() * loops);
            read[relation.GetString()!] = read.GetValueOrDefault(relation.GetString()!) + rows;
        }

        return read;
    }
}
