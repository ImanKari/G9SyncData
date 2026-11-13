using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace G9SyncData.IntegrationTests.Resync;

/// <summary>
///     A Hello and a pull plan check every cursor the device holds in ONE store call (<see cref="IG9SyncServerStore.CheckCursorsAsync" />,
///     AgriPad TR-0069, 2026-11-13): two round trips per subscription made a no-change sync of a device holding 63 subscriptions take
///     1.4 s. The batch must answer exactly what the two single checks answer, cursor by cursor - the end-to-end tests of a cursor ahead
///     of the database and of one beyond the tombstone horizon run through it too.
/// </summary>
public sealed class CursorBatchTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task One_call_answers_every_cursor_exactly_as_the_two_single_checks_do_in_the_order_asked()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 5);
        var pot = harness.Services.GetRequiredService<G9CSyncServerModel>().FindTable("farm.Pot")!;
        var site_ = harness.Services.GetRequiredService<G9CSyncServerModel>().FindTable("farm.Site")!;

        // Taken before a delete whose tombstone is then purged: beyond the horizon for Pot, still valid for a table with no purge.
        var old = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        var pots = await harness.PotIdsAsync(site);
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", pots[0]);
        await Task.Delay(50);
        Assert.True(await harness.Store.CleanupTombstonesAsync(TimeSpan.Zero, CancellationToken.None) >= 1);
        var current = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);
        var parts = current.Split(':');
        var ahead = $"{long.Parse(parts[0]) + 10_000_000}:{long.Parse(parts[1]) + 10_000_000}:";

        G9CStoreCursorCheck[] checks =
        [
            new(current, [pot]),
            new(ahead, [pot]),
            new(old, [pot]),
            new(old, [site_]),
            new(old, [site_, pot]),
            new(current, [])
        ];
        var batched = await harness.Store.CheckCursorsAsync(checks, CancellationToken.None);

        var single = new List<G9EStoreCursorVerdict>();
        foreach (var check in checks)
        {
            single.Add(await harness.Store.IsSnapshotAheadAsync(check.Snapshot, CancellationToken.None) ? G9EStoreCursorVerdict.Ahead
                : await harness.Store.IsBeyondTombstoneHorizonAsync(check.Snapshot, check.Tables, CancellationToken.None) ? G9EStoreCursorVerdict.BeyondHorizon
                : G9EStoreCursorVerdict.Valid);
        }

        Assert.Equal(single, batched);
        Assert.Equal(
            [G9EStoreCursorVerdict.Valid, G9EStoreCursorVerdict.Ahead, G9EStoreCursorVerdict.BeyondHorizon, G9EStoreCursorVerdict.Valid,
             G9EStoreCursorVerdict.BeyondHorizon, G9EStoreCursorVerdict.Valid],
            batched);
        Assert.Empty(await harness.Store.CheckCursorsAsync([], CancellationToken.None));
    }

    [PostgresFact]
    public async Task An_unreadable_snapshot_is_ahead_alone_and_the_others_keep_their_own_answer()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var pot = harness.Services.GetRequiredService<G9CSyncServerModel>().FindTable("farm.Pot")!;
        var current = await harness.Store.GetCurrentSnapshotAsync(CancellationToken.None);

        var verdicts = await harness.Store.CheckCursorsAsync([new(current, [pot]), new("not a snapshot", [pot]), new(current, [pot])], CancellationToken.None);

        Assert.Equal([G9EStoreCursorVerdict.Valid, G9EStoreCursorVerdict.Ahead, G9EStoreCursorVerdict.Valid], verdicts);
    }
}
