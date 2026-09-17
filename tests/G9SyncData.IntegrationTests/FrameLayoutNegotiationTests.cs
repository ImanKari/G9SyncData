using System.Runtime.CompilerServices;
using G9SyncData.Client.Runs;
using G9SyncData.Client.SignalR;
using G9SyncData.Client.Transport;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>
///     The frame layout each client asks for (ADR-0040): row-wise (1) or column-grouped (2), negotiated per request, over real SignalR.
/// </summary>
public sealed class FrameLayoutNegotiationTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Clients_asking_for_either_frame_layout_get_it_for_every_frame_and_end_with_the_servers_rows_through_pull_push_reconcile_and_fetch()
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 20_000);

        var pulled = new Dictionary<int, long>();
        foreach (var layout in new[] { 1, 2 })
        {
            var recorder = new FrameVersionRecorder(new G9CSignalRSyncTransport(new G9CSignalRSyncTransportOptions { Url = harness.HubUrl! }));
            await using var client = await harness.CreateClientAsync(o => o.MaxFrameVersion = layout, transportFactory: () => recorder);
            await client.Engine.SubscribeAsync("sites");
            await client.Engine.SubscribeAsync("site-pots", SitePots(site));

            harness.Wire!.Reset();
            await client.SyncOkAsync();
            pulled[layout] = harness.Wire.Sent;
            await AssertSameRowsAsync(harness, client, site, $"layout {layout} after the pull");

            // push: an edit uploads and the device ends with the server's row
            var edited = (string)(await client.ScalarAsync($"SELECT Id FROM Pot ORDER BY Id LIMIT 1 OFFSET {layout * 100}"))!;
            await client.ExecAsync("UPDATE Pot SET Code = $p0 WHERE Id = $p1", $"LAYOUT-{layout}", edited);
            await client.SyncOkAsync();
            Assert.Equal($"LAYOUT-{layout}", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", Guid.Parse(edited)));

            // reconcile + fetch: 50 rows damaged on the device behind the capture triggers, with versions the server never issued
            await client.ExecAsync("""
                UPDATE g9_ctx SET applying = 1 WHERE id = 1;
                UPDATE Pot SET Code = 'DAMAGED' WHERE Id IN (SELECT Id FROM Pot ORDER BY Id DESC LIMIT 50);
                UPDATE g9_rows SET server_ver = 1 WHERE pk IN (SELECT Id FROM Pot WHERE Code = 'DAMAGED');
                UPDATE g9_ctx SET applying = 0 WHERE id = 1;
                """);
            await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
            Assert.Equal(0L, (long)(await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Code = 'DAMAGED'"))!);
            await AssertSameRowsAsync(harness, client, site, $"layout {layout} after reconciliation");

            // Every frame the server produced for this client is in the layout the client asked for, on every operation.
            foreach (var operation in new[] { "pull", "reconcile", "fetch" })
            {
                Assert.True(recorder.Versions.ContainsKey(operation), $"layout {layout}: no {operation} frame was seen");
                Assert.All(recorder.Versions[operation], version => Assert.Equal(layout, version));
            }
        }

        // Bytes are reported, not asserted: this table's bytes are mostly random 16-byte keys, which no layout compresses (±2% either
        // way). On AgriPad-shaped rows layout 2 is ~15% smaller (19-Performance).
        output.WriteLine($"initial pull of 20,000 pots: layout 1 {pulled[1]:N0} bytes, layout 2 {pulled[2]:N0} bytes");
    }

    [Fact]
    public void A_request_that_does_not_state_a_layout_gets_row_wise_frames_and_no_request_gets_a_layout_the_server_does_not_know()
    {
        Assert.Equal(1, G9SyncProtocol.NegotiateFrameVersion(0));
        Assert.Equal(1, G9SyncProtocol.NegotiateFrameVersion(1));
        Assert.Equal(2, G9SyncProtocol.NegotiateFrameVersion(2));
        Assert.Equal(G9SyncProtocol.MaxFrameVersion, G9SyncProtocol.NegotiateFrameVersion(int.MaxValue));
        Assert.Equal(1, G9SyncProtocol.NegotiateFrameVersion(-5));
    }

    /// <summary>Passes every call through and records the G9RowBatch version byte of each frame the server sends back.</summary>
    private sealed class FrameVersionRecorder(IG9SyncTransport inner) : IG9SyncTransport
    {
        public Dictionary<string, List<int>> Versions { get; } = [];

        public bool IsConnected => inner.IsConnected;

        public event Action<G9DtChangesAvailable>? ChangesAvailable
        {
            add => inner.ChangesAvailable += value;
            remove => inner.ChangesAvailable -= value;
        }

        public Task ConnectAsync(CancellationToken cancellationToken) => inner.ConnectAsync(cancellationToken);

        public Task<G9DtHelloResult> HelloAsync(G9DtHelloRequest request, CancellationToken cancellationToken) => inner.HelloAsync(request, cancellationToken);

        public async Task<G9DtPushResult> PushAsync(G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks, CancellationToken cancellationToken)
        {
            var result = await inner.PushAsync(request, chunks, cancellationToken);
            foreach (var frame in result.CanonicalFrames) Record("push", frame);
            return result;
        }

        public Task<G9DtPullPlan> PlanPullAsync(G9DtPullRequest request, CancellationToken cancellationToken) => inner.PlanPullAsync(request, cancellationToken);

        public IAsyncEnumerable<G9DtSyncChunk> PullAsync(string token, G9DtPullContinuation? continuation, CancellationToken cancellationToken) =>
            Recorded("pull", inner.PullAsync(token, continuation, cancellationToken));

        public IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9DtReconcileRequest request, CancellationToken cancellationToken) =>
            Recorded("reconcile", inner.ReconcileAsync(request, cancellationToken));

        public IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9DtFetchRowsRequest request, CancellationToken cancellationToken) =>
            Recorded("fetch", inner.FetchRowsAsync(request, cancellationToken));

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private async IAsyncEnumerable<G9DtSyncChunk> Recorded(string operation, IAsyncEnumerable<G9DtSyncChunk> chunks,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var chunk in chunks.WithCancellation(cancellationToken))
            {
                if (chunk.Payload is { Length: > 4 } payload) Record(operation, payload);
                yield return chunk;
            }
        }

        private void Record(string operation, byte[] frame)
        {
            lock (Versions)
            {
                if (!Versions.TryGetValue(operation, out var list)) Versions[operation] = list = [];
                list.Add(frame[4]);
            }
        }
    }

    private static async Task AssertSameRowsAsync(SyncHarness harness, SyncClient client, Guid site, string when)
    {
        var server = await harness.ScalarServerAsync<string>("""
            SELECT count(*) || ':' || string_agg(lower("Id"::text) || '=' || "Code" || '/' || "Status", ',' ORDER BY lower("Id"::text) COLLATE "C")
            FROM farm."Pot" WHERE "SiteId" = $1 AND NOT "IsDeleted"
            """, site);
        var device = (string)(await client.ScalarAsync(
            "SELECT count(*) || ':' || group_concat(Id || '=' || Code || '/' || Status, ',') FROM (SELECT Id, Code, Status FROM Pot WHERE IsDeleted = 0 ORDER BY Id)"))!;
        Assert.True(server == device, $"{when}: the device's pots differ from the server's");
    }
}
