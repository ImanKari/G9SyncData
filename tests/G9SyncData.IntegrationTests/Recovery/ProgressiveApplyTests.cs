using G9SyncData.Client.Progress;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Recovery;

/// <summary>
///     A staged pull can land in committed slices instead of one transaction, so rows become readable while the rest are still
///     arriving (ADR-0053). What that must not cost: the same final state as the atomic apply, and a run interrupted between slices
///     that converges rather than leaving the device believing it is further ahead than it is.
/// </summary>
public sealed class ProgressiveApplyTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Rows_become_readable_slice_by_slice_instead_of_all_at_the_end()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 500);
        await using var client = await harness.CreateClientAsync(store: s => s.ApplySliceRows = 100);

        var visible = new List<long>();
        client.Engine.RowsApplied += (_, e) =>
        {
            // Read from the database inside the handler: a slice that has been announced must already be committed.
            if (e.Slice is not null) visible.Add(client.CountAsync("Pot").GetAwaiter().GetResult());
        };

        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        Assert.True(visible.Count > 1, $"The pull should have landed in several slices, got {visible.Count}.");
        Assert.Equal(visible.OrderBy(v => v), visible);           // each slice only ever adds rows
        Assert.True(visible[0] < 500, "The first slice must be visible before the pull is finished.");
        Assert.Equal(500, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task A_sliced_apply_lands_exactly_what_the_atomic_apply_lands()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 300);

        await using var atomic = await SubscribedAsync(harness, site, sliceRows: 0);
        await using var sliced = await SubscribedAsync(harness, site, sliceRows: 40);

        Assert.Equal(await Fingerprint(atomic), await Fingerprint(sliced));
        Assert.Equal(300, await sliced.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task A_run_interrupted_between_slices_keeps_what_landed_and_finishes_next_time()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 400);
        await using var client = await harness.CreateClientAsync(store: s => s.ApplySliceRows = 50);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        using var kill = new CancellationTokenSource();
        var slices = 0;
        client.Engine.RowsApplied += (_, e) =>
        {
            if (e.Slice is not null && ++slices == 2) kill.Cancel();
        };

        var interrupted = await client.Engine.SyncAsync(new G9CSyncRequest { CancellationToken = kill.Token });
        Assert.Equal(G9ESyncOutcome.Canceled, interrupted.Outcome);

        var landed = await client.CountAsync("Pot");
        Assert.True(landed is > 0 and < 400, $"Some rows should have landed, got {landed}.");

        // The cursor never moved, so the next run pulls the same range again and upserts over what is there.
        await client.SyncUntilSettledAsync();
        Assert.Equal(400, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task A_local_edit_still_survives_a_sliced_apply()
    {
        // The protected set is read once, before the first slice. If a slice re-read it, rows it had just applied would look
        // "changed after the stage" to the next slice and later frames for them would be dropped (ADR-0046 + ADR-0053).
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m => ConfigureServer(m, new ServerModelOptions(PotConflicts: G9EConflictPolicy.ClientWins)));
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 200);
        await using var client = await SubscribedAsync(harness, site, sliceRows: 25);
        var pots = await harness.PotIdsAsync(site);

        await client.ExecAsync("UPDATE Pot SET Note = 'mine' WHERE Id = $p0", Key(pots[0]));
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 3""");
        await harness.WaitForCaptureAsync();
        await client.SyncUntilSettledAsync();

        Assert.Equal("mine", (string?)await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[0])));
        Assert.Equal(200L, await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Status = 3"));
    }

    [PostgresFact]
    public async Task Every_changed_key_is_announced_across_the_slices()
    {
        // A projection that redraws only the keys it is told about must be told about all of them.
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 250);
        await using var client = await harness.CreateClientAsync(store: s => s.ApplySliceRows = 30);

        var announced = new List<string>();
        client.Engine.RowsApplied += (_, e) =>
        {
            if (e.Slice is not null && e.Report.ChangedKeys.TryGetValue("Pot", out var keys)) announced.AddRange(keys);
        };

        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        Assert.Equal(250, announced.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    private static async Task<SyncClient> SubscribedAsync(SyncHarness harness, Guid site, int sliceRows)
    {
        var client = await harness.CreateClientAsync(store: s => s.ApplySliceRows = sliceRows);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        return client;
    }

    /// <summary>Every pot row as one ordered string, so two devices can be compared exactly.</summary>
    private static async Task<string> Fingerprint(SyncClient client) =>
        (string?)await client.ScalarAsync("SELECT group_concat(Id || ':' || Status || ':' || coalesce(Note, '')) FROM (SELECT * FROM Pot ORDER BY Id)") ?? string.Empty;
}
