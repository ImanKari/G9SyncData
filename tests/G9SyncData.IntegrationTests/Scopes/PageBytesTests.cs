using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Protocol.RowBatch;
using G9SyncData.Server;
using G9SyncData.Server.Storage;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Scopes;

/// <summary>
///     A page is bounded by bytes as well as rows (ADR-0073). A page is one chunk and one transport message, which cannot be split; bounded
///     by rows alone, a table with an image per row made 17.8 MB messages, and over LongPolling on a slow link one of them took longer than
///     the server's disconnect timeout. Every page path closes early once full, never loses or repeats a row, and resumes after its last row.
/// </summary>
public sealed class PageBytesTests(ITestOutputHelper output)
{
    private const int NoteChars = 2_000;
    private const int MaxPageBytes = 8 * 1024;

    /// <summary>A full page holds what fits under the bound plus the one row that crossed it.</summary>
    private const int MaxRowsPerFullPage = MaxPageBytes / NoteChars + 2;

    [PostgresFact]
    public async Task A_direct_snapshot_part_is_split_by_bytes_and_every_row_arrives_once_in_key_order()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 120);
        await harness.ExecServerAsync($"""UPDATE farm."Pot" SET "Note" = repeat('n', {NoteChars})""");
        var snapshot = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);

        var pages = await ReadAllAsync(harness.Store.ReadPartAsync(SitePotsPart(harness, site, G9EPullPartKind.Snapshot, snapshot), CancellationToken.None));

        AssertSplit(pages, expectedRows: 120);
        Assert.Equal(await AllPotKeysAsync(harness), pages.SelectMany(Keys).ToList());
    }

    [PostgresFact]
    public async Task A_delta_part_is_split_by_bytes_and_carries_its_tombstones()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 90);
        var before = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        await harness.ExecServerAsync($"""UPDATE farm."Pot" SET "Note" = repeat('d', {NoteChars})""");
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Code" IN ('P00007', 'P00050')""");
        await harness.WaitForCaptureAsync();
        var after = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);

        var pages = await ReadAllAsync(harness.Store.ReadPartAsync(SitePotsPart(harness, site, G9EPullPartKind.Delta, after, before), CancellationToken.None));

        Assert.True(pages.Count > 90 / MaxRowsPerFullPage, $"expected the delta to be split by bytes, got {pages.Count} page(s)");
        // A page closed by bytes is never marked last; the final page may be unmarked too when it filled its LIMIT exactly (the part
        // then ends on an empty read, as it always did at a multiple of PageRows).
        Assert.All(pages[..^1], p => Assert.False(p.IsLast));
        var upserts = pages.SelectMany(Keys).ToList();
        var tombstones = pages.SelectMany(p => Keys(p, G9ERowBatchOp.Tombstone)).ToList();
        Assert.Equal(88, upserts.Count);
        Assert.Equal(88, upserts.Distinct().Count());
        Assert.Equal(2, tombstones.Count);
    }

    [PostgresFact]
    public async Task A_key_set_part_closed_by_bytes_is_not_mistaken_for_its_last_page()
    {
        await using var harness = await EfScopeFixture.CreateHarnessAsync(output);
        var north = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertPotsAsync(north, 130);
        await harness.ExecServerAsync($"""UPDATE farm."Pot" SET "Note" = repeat('k', {NoteChars})""");
        var snapshot = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        var request = EfScopeFixture.AllowedPotsPart(harness, [north], G9EPullPartKind.Snapshot, snapshot, maxPageBytes: MaxPageBytes);

        var pages = await ReadAllAsync(harness.Store.ReadPartAsync(request, CancellationToken.None));

        // Before the bound a short key page meant "the set ran out"; a page closed by bytes is short too and must not end the part.
        AssertSplit(pages, expectedRows: 130);
        Assert.Equal(130, pages.SelectMany(Keys).Distinct().Count());
    }

    [PostgresFact]
    public async Task Fetched_rows_leave_in_several_frames_when_one_would_exceed_the_bound()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 40);
        await harness.ExecServerAsync($"""UPDATE farm."Pot" SET "Note" = repeat('f', {NoteChars})""");
        var keys = await AllPotKeysAsync(harness);
        var part = SitePotsPart(harness, site, G9EPullPartKind.Snapshot, await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None));

        var pages = await ReadAllAsync(harness.Store.FetchRowsAsync(new G9CStoreFetchRequest
        {
            Table = part.Table, Include = part.Include, Columns = part.Columns, Subscription = part.Subscription, Keys = keys,
            PageRows = 1_000, MaxPageBytes = MaxPageBytes
        }, CancellationToken.None));

        Assert.True(pages.Count > 1, "one key chunk of 40 wide rows must leave in several frames");
        Assert.All(pages, p => Assert.InRange(p.RowCount, 1, MaxRowsPerFullPage));
        Assert.Equal(keys, pages.SelectMany(Keys).ToList());
    }

    [PostgresFact]
    public async Task A_device_syncs_wide_rows_through_byte_bounded_pages_and_then_their_changes()
    {
        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.MaxPageBytes = MaxPageBytes);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 150);
        await harness.ExecServerAsync($"""UPDATE farm."Pot" SET "Note" = repeat('a', {NoteChars})""");
        await harness.WaitForCaptureAsync();

        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        Assert.Equal(150, await client.CountAsync("Pot"));
        Assert.Equal(150L, await client.ScalarAsync($"SELECT count(*) FROM Pot WHERE Note = '{new string('a', NoteChars)}'"));

        await harness.ExecServerAsync($"""UPDATE farm."Pot" SET "Note" = repeat('b', {NoteChars})""");
        await harness.WaitForCaptureAsync();
        await client.SyncOkAsync();
        Assert.Equal(150L, await client.ScalarAsync($"SELECT count(*) FROM Pot WHERE Note = '{new string('b', NoteChars)}'"));
    }

    [PostgresFact]
    public async Task Without_a_byte_bound_a_page_is_bounded_by_rows_as_before()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 120);
        await harness.ExecServerAsync($"""UPDATE farm."Pot" SET "Note" = repeat('n', {NoteChars})""");
        var snapshot = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);

        var pages = await ReadAllAsync(harness.Store.ReadPartAsync(SitePotsPart(harness, site, G9EPullPartKind.Snapshot, snapshot, maxPageBytes: 0),
            CancellationToken.None));

        Assert.Equal([50, 50, 20], pages.Select(p => p.RowCount).ToArray());
    }

    private static void AssertSplit(List<G9CStorePage> pages, int expectedRows)
    {
        Assert.True(pages.Count > expectedRows / MaxRowsPerFullPage, $"expected the part to be split by bytes, got {pages.Count} page(s)");
        Assert.All(pages, p => Assert.InRange(p.RowCount, 1, MaxRowsPerFullPage));
        Assert.All(pages[..^1], p => Assert.False(p.IsLast));
        Assert.Equal(expectedRows, pages.Sum(p => p.RowCount));
    }

    private static async Task<List<G9CStorePage>> ReadAllAsync(IAsyncEnumerable<G9CStorePage> source)
    {
        var pages = new List<G9CStorePage>();
        await foreach (var page in source) pages.Add(page);
        return pages;
    }

    private static async Task<List<string>> AllPotKeysAsync(SyncHarness harness)
    {
        var keys = new List<string>();
        await using var command = harness.DataSource.CreateCommand("""SELECT "Id"::text FROM farm."Pot" ORDER BY "Id" """);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) keys.Add(reader.GetString(0));
        return keys;
    }

    private static List<string> Keys(G9CStorePage page) => Keys(page, G9ERowBatchOp.Upsert);

    private static List<string> Keys(G9CStorePage page, G9ERowBatchOp op)
    {
        var keys = new List<string>();
        using var reader = G9CRowBatchReader.Open(page.Frame);
        while (reader.NextSection())
        {
            if (reader.Op != op) continue;
            while (reader.ReadRow()) keys.Add(reader.Key);
        }

        return keys;
    }

    private static G9CStorePartRequest SitePotsPart(SyncHarness harness, Guid site, G9EPullPartKind kind, string snapshot, string? previous = null,
        int maxPageBytes = MaxPageBytes)
    {
        var definition = harness.Engine.Model.FindSubscription("site-pots")!;
        var include = definition.Tables.Single();
        var parameters = SitePots(site);
        return new G9CStorePartRequest
        {
            Table = include.Table, Include = include, Kind = kind, Columns = include.Table.Columns, PageRows = 50, MaxPageBytes = maxPageBytes,
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
}
