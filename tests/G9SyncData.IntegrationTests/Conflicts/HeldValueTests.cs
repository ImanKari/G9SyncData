using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Conflicts;

/// <summary>
///     LES-0086: a value the server already holds is no change. The server drops such columns before its conflict policy runs, so a row
///     that arrives a second time with its own values - a restored backup on a second tablet, the original tablet sending again - is
///     accepted without a conflict and without being written, on ANY client version; and the client counts as lost only what its push
///     carried, comparing timestamps as instants.
/// </summary>
public sealed class HeldValueTests(ITestOutputHelper output)
{
    /// <summary>The AgriPad case (Production, 2026-10-28): the device inserted rows the server already has, from another copy of itself.</summary>
    [PostgresTheory]
    [InlineData(G9EConflictPolicy.ColumnMerge, true)]
    [InlineData(G9EConflictPolicy.ColumnMerge, false)]
    [InlineData(G9EConflictPolicy.ServerWins, false)]
    public async Task A_row_inserted_again_with_the_values_the_server_holds_is_accepted_without_a_conflict_and_not_rewritten(
        G9EConflictPolicy policy, bool keepLostConflicts)
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(policy)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        var first = await harness.CreateClientAsync();
        await using var __ = first;
        await first.Engine.SubscribeAsync("sites");
        await first.Engine.SubscribeAsync("site-pots", SitePots(site));
        await first.SyncOkAsync();
        var pot = Guid.NewGuid();
        const string values = "$p1, 'P-1', 3, 1.5, '2026-10-29 10:00:00.1234567', 'note', 0";
        await first.ExecAsync($"INSERT INTO Pot (Id, SiteId, Code, Status, Weight, PlantedAt, Note, IsDeleted) VALUES ($p0, {values})", Key(pot), Key(site));
        await first.SyncOkAsync();
        var written = await harness.ScalarServerAsync<string>("""SELECT xmin::text FROM farm."Pot" WHERE "Id" = $1""", pot);

        // A second device holding the same row as its own insert (server_ver 0): a copy of the first device's file, restored elsewhere.
        var second = await harness.CreateClientAsync(store: s => s.KeepLostConflicts = keepLostConflicts);
        await using var ___ = second;
        await second.Engine.SubscribeAsync("sites");
        await second.Engine.SubscribeAsync("site-pots", SitePots(site));
        await second.ExecAsync($"INSERT INTO Pot (Id, SiteId, Code, Status, Weight, PlantedAt, Note, IsDeleted) VALUES ($p0, {values})", Key(pot), Key(site));
        var result = await second.SyncAsync();

        Assert.True(result.Outcome == G9ESyncOutcome.Succeeded, SyncClient.Describe(result));
        Assert.Equal(0, result.Conflicts);
        Assert.Equal(0, result.Rejected);
        Assert.Empty(await second.Engine.GetRejectionsAsync());
        Assert.Equal(0, (await second.Engine.GetPendingSummaryAsync()).TotalRows);
        Assert.Equal(written, await harness.ScalarServerAsync<string>("""SELECT xmin::text FROM farm."Pot" WHERE "Id" = $1""", pot));

        // ...and it is an ordinary row afterwards: the second device's next edit is no conflict.
        await second.ExecAsync("UPDATE Pot SET Status = 9 WHERE Id = $p0", Key(pot));
        await second.SyncOkAsync();
        Assert.Equal(9, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    /// <summary>Column merge: of two overlapping columns the one with the same value is dropped from the conflict the SERVER reports.</summary>
    [PostgresFact]
    public async Task A_column_merge_reports_only_the_overlapping_column_whose_value_differs_even_to_a_client_that_compares_nothing()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge)));
        var (client, pot) = await OnePotAsync(harness, keep: true);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 2, "Note" = 'office' WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Status = 2, Note = 'field' WHERE Id = $p0", Key(pot));
        var result = await client.SyncAsync();

        Assert.Equal(1, result.Conflicts);
        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal("Note", rejection.Column);
    }

    /// <summary>A column the device changed but never sends (read-only on the server) is not part of a conflict and is not kept as lost.</summary>
    [PostgresFact]
    public async Task A_read_only_column_changed_on_the_device_is_never_kept_as_lost()
    {
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ColumnMerge, PotNoteReadOnly: true)));
        var (client, pot) = await OnePotAsync(harness, keep: true);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 2 WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Status = 4, Note = 'local only' WHERE Id = $p0", Key(pot));
        await client.SyncAsync();

        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal("Status", rejection.Column);   // until 1.5.14: "Status,Note" - Note was never sent
    }

    /// <summary>
    ///     A timestamp that differs only below the microsecond is the same instant: the device keeps 100 ns, PostgreSQL microseconds.
    ///     ServerWins keeps the whole change, so the client's own comparison decides what it lists.
    /// </summary>
    [PostgresFact]
    public async Task A_timestamp_equal_to_the_microsecond_is_not_listed_as_lost()
    {
        await using var harness = await SyncHarness.CreateAsync(output, model: m => ConfigureServer(m, new ServerModelOptions(G9EConflictPolicy.ServerWins)));
        var (client, pot) = await OnePotAsync(harness, keep: true);
        await using var _ = client;

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'office', "PlantedAt" = '2026-10-29 10:00:00.123457' WHERE "Id" = $1""", pot);
        await client.ExecAsync("UPDATE Pot SET Note = 'field', PlantedAt = '2026-10-29 10:00:00.1234567' WHERE Id = $p0", Key(pot));
        await client.SyncAsync();

        var rejection = Assert.Single(await client.Engine.GetRejectionsAsync());
        Assert.Equal("Note", rejection.Column);
    }

    private static async Task<(SyncClient Client, Guid Pot)> OnePotAsync(SyncHarness harness, bool keep)
    {
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1);
        var client = await harness.CreateClientAsync(store: s => s.KeepLostConflicts = keep);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        return (client, (await harness.PotIdsAsync(site))[0]);
    }
}
