using G9SyncData.Client.Transport;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Model;
using G9SyncData.Testing;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Resync;

/// <summary>
///     A pull plan does not plan a delta part of a table that recorded no change since the part's cursor
///     (<c>IG9SyncServerStore.HasChangesAsync</c>, AgriPad TR-0069, 2026-11-13): estimating and reading an empty delta cost a query each, per
///     table and subscription, on every sync - a no-change sync of 63 subscriptions took 1.4 s and takes 0.05 s without them. What must not
///     change: every change still arrives (updates, inserts, deletes, in any subscription), and every cursor still advances.
/// </summary>
public sealed class QuietDeltaTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Only_the_tables_that_changed_are_planned_and_every_change_still_arrives()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var model = harness.Services.GetRequiredService<G9CSyncServerModel>();
        var potTable = harness.Store.GetTableId(model.FindTable("farm.Pot")!);
        var siteTable = harness.Store.GetTableId(model.FindTable("farm.Site")!);
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotsAsync(north, 6);
        await harness.InsertPotsAsync(south, 4);

        PlanRecorder? recorder = null;
        await using var client = await harness.CreateClientAsync(transportFactory: () => recorder = new PlanRecorder(new G9CInMemorySyncTransport(harness.Engine, harness.Notifier)));
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(north));
        await client.Engine.SubscribeAsync("site-pots", SitePots(south));
        await client.SyncOkAsync();
        Assert.Equal(10, await client.CountAsync("Pot"));

        // Nothing changed: no part at all, every cursor advanced.
        var quiet = await client.SyncOkAsync();
        Assert.Empty(recorder!.Last.Parts);
        Assert.Equal(3, recorder.Last.Cursors.Count);
        Assert.Equal(0, quiet.Downloaded);

        // A pot of the north changed: only Pot parts are planned (the probe is per table, so both site-pots parts), and it arrives.
        var northPots = await harness.PotIdsAsync(north);
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'changed' WHERE "Id" = $1""", northPots[0]);
        await client.SyncOkAsync();
        Assert.All(recorder.Last.Parts, p => Assert.Equal(potTable, p.TableId));
        Assert.Equal(2, recorder.Last.Parts.Count);
        Assert.Equal("changed", await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(northPots[0])));

        // A site renamed, a pot deleted in the south and one inserted in the north: all planned, all arrive.
        var southPots = await harness.PotIdsAsync(south);
        await harness.ExecServerAsync("""UPDATE farm."Site" SET "Name" = 'South (renamed)' WHERE "Id" = $1""", south);
        await harness.ExecServerAsync("""DELETE FROM farm."Pot" WHERE "Id" = $1""", southPots[0]);
        await harness.InsertPotsAsync(north, 1, prefix: "NEW");
        await client.SyncOkAsync();
        Assert.Contains(recorder.Last.Parts, p => p.TableId == siteTable);
        Assert.Equal("South (renamed)", await client.ScalarAsync("SELECT Name FROM Site WHERE Id = $p0", Key(south)));
        Assert.False(await client.ExistsAsync("Pot", southPots[0]));
        Assert.Equal(10, await client.CountAsync("Pot"));

        // Settled again: nothing planned, nothing downloaded.
        var settled = await client.SyncOkAsync();
        Assert.Empty(recorder.Last.Parts);
        Assert.Equal(0, settled.Downloaded);
    }

    [PostgresFact]
    public async Task A_change_made_while_a_subscription_was_quiet_arrives_with_its_next_sync_never_skipped()
    {
        // The cursor of a quiet part still advances: a change made AFTER that plan must be inside the next delta, and one made between
        // two quiet runs must not fall between their snapshots.
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await harness.CreateSubscribedClientAsync(site);
        var pots = await harness.PotIdsAsync(site);

        for (var round = 0; round < 5; round++)
        {
            await client.SyncOkAsync();   // quiet
            await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = $1 WHERE "Id" = $2""", "round " + round, pots[round % pots.Count]);
            await client.SyncOkAsync();
            Assert.Equal("round " + round, await client.ScalarAsync("SELECT Note FROM Pot WHERE Id = $p0", Key(pots[round % pots.Count])));
        }
    }

    /// <summary>Passes everything through and keeps the last pull plan.</summary>
    private sealed class PlanRecorder(G9CInMemorySyncTransport inner) : IG9SyncTransport
    {
        public G9DtPullPlan Last { get; private set; } = new();

        public bool IsConnected => inner.IsConnected;

        public event Action<G9DtChangesAvailable>? ChangesAvailable
        {
            add => inner.ChangesAvailable += value;
            remove => inner.ChangesAvailable -= value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);

        public Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken) => inner.HelloAsync(request, cancellationToken);

        public Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken) =>
            inner.PushAsync(request, chunks, cancellationToken);

        public async Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken) =>
            Last = await inner.PlanPullAsync(request, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken) =>
            inner.PullAsync(token, continuation, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
            inner.ReconcileAsync(request, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
            inner.FetchRowsAsync(request, cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
