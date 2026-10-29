using G9SyncData.Client.SignalR;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>
///     The same engines over real SignalR: Kestrel on loopback, the G9 hub and client, the Postgres listener and pokes - in both hub
///     protocols the server accepts (JSON and MessagePack).
/// </summary>
public sealed class SignalRTransportTests(ITestOutputHelper output)
{
    [PostgresTheory]
    [InlineData(G9ESyncHubProtocol.Json)]
    [InlineData(G9ESyncHubProtocol.MessagePack)]
    public async Task A_device_downloads_and_uploads_over_a_real_signalr_connection(G9ESyncHubProtocol protocol)
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 250);
        await using var client = await harness.CreateClientAsync(protocol: protocol);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        await client.SyncOkAsync();
        Assert.Equal(250, await client.CountAsync("Pot"));

        var created = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'SR-1', 1, 0)", Key(created), Key(site));
        await client.SyncOkAsync();
        Assert.Equal("SR-1", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        // Reconciliation streams key listings and fetches rows over the same protocol.
        var reconciled = await client.SyncOkAsync(new G9SyncData.Client.Runs.G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(0, reconciled.Changes.Upserts);
    }

    [PostgresTheory]
    [InlineData(G9ESyncHubProtocol.Json)]
    [InlineData(G9ESyncHubProtocol.MessagePack)]
    public async Task With_stateful_reconnect_on_at_both_ends_a_device_still_downloads_uploads_and_reconciles(G9ESyncHubProtocol protocol)
    {
        // ADR-0069. The feature changes what travels (sequence and ack messages, a buffer on each side), so the whole run is proven
        // over it in both protocols - including the MessagePack one, which is this family's own implementation.
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR, signalR: o => o.AllowStatefulReconnects = true);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 250);
        await using var client = await harness.CreateClientAsync(transportFactory: () => new G9CSignalRSyncTransport(
            new G9CSignalRSyncTransportOptions { Url = harness.HubUrl!, Protocol = protocol, StatefulReconnect = true }));
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        await client.SyncOkAsync();
        Assert.Equal(250, await client.CountAsync("Pot"));

        var created = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'SR-2', 1, 0)", Key(created), Key(site));
        await client.SyncOkAsync();
        Assert.Equal("SR-2", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", created));

        var reconciled = await client.SyncOkAsync(new G9SyncData.Client.Runs.G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(0, reconciled.Changes.Upserts);
    }

    [PostgresTheory]
    [InlineData(G9ESyncHubProtocol.Json)]
    [InlineData(G9ESyncHubProtocol.MessagePack)]
    public async Task A_server_refusal_reaches_the_client_with_its_stable_code(G9ESyncHubProtocol protocol)
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        await using var client = await harness.CreateClientAsync(protocol: protocol);
        await client.Transport.ConnectAsync(CancellationToken.None);

        var error = await Assert.ThrowsAsync<G9CSyncException>(async () =>
        {
            await foreach (var _ in client.Transport.PullAsync("not-a-token", null, CancellationToken.None))
            {
            }
        });

        Assert.StartsWith("G9S-TOKEN-", error.Code, StringComparison.Ordinal);
        Assert.False(error.Retryable);
    }

    [PostgresTheory]
    [InlineData(G9ESyncHubProtocol.Json)]
    [InlineData(G9ESyncHubProtocol.MessagePack)]
    public async Task A_committed_server_change_pokes_devices_holding_the_table(G9ESyncHubProtocol protocol)
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateClientAsync(protocol: protocol);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        var poked = new TaskCompletionSource<G9DtChangesAvailable>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Transport.ChangesAvailable += changes =>
        {
            if (changes.Tables.Contains("farm.Pot", StringComparer.OrdinalIgnoreCase)) poked.TrySetResult(changes);
        };

        await harness.InsertPotAsync(Guid.NewGuid(), site, "POKE");
        await poked.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    /// <summary>
    ///     S-2 (LES-0087): a connection that reconnects BY ITSELF is a new connection id in no table group - the server joins groups in
    ///     Hello only - so until 1.5.14 every poke after a dropped link (or an hourly token-expiry close) went to nobody until the next
    ///     run. The transport now sends the last Hello again on Reconnected.
    /// </summary>
    [PostgresTheory]
    [InlineData(G9ESyncHubProtocol.Json)]
    [InlineData(G9ESyncHubProtocol.MessagePack)]
    public async Task After_the_connection_reconnects_by_itself_a_committed_server_change_still_pokes_the_device(G9ESyncHubProtocol protocol)
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateClientAsync(protocol: protocol);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        var signalR = (G9CSignalRSyncTransport)client.Transport;
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        signalR.Connection.Reconnected += _ =>
        {
            reconnected.TrySetResult();
            return Task.CompletedTask;
        };
        Assert.True(harness.Wire!.AbortAll() > 0);
        await reconnected.Task.WaitAsync(TimeSpan.FromSeconds(60));

        var poked = new TaskCompletionSource<G9DtChangesAvailable>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Transport.ChangesAvailable += changes =>
        {
            if (changes.Tables.Contains("farm.Pot", StringComparer.OrdinalIgnoreCase)) poked.TrySetResult(changes);
        };

        // The rejoin is asynchronous: keep committing until one poke arrives, as an office keeps editing (bounded).
        for (var attempt = 0; attempt < 15 && !poked.Task.IsCompleted; attempt++)
        {
            await harness.InsertPotAsync(Guid.NewGuid(), site, "AFTER-" + attempt);
            await Task.WhenAny(poked.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        }

        Assert.True(poked.Task.IsCompleted, "no poke after the automatic reconnect: the new connection is in no table group");
    }

    [PostgresFact]
    public async Task Json_and_MessagePack_clients_of_one_server_hold_the_same_rows_and_the_binary_one_uses_fewer_bytes()
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 20_000);

        var received = new Dictionary<G9ESyncHubProtocol, long>();
        var contents = new Dictionary<G9ESyncHubProtocol, string>();
        foreach (var protocol in new[] { G9ESyncHubProtocol.Json, G9ESyncHubProtocol.MessagePack })
        {
            await using var client = await harness.CreateClientAsync(protocol: protocol);
            await client.Engine.SubscribeAsync("sites");
            await client.Engine.SubscribeAsync("site-pots", SitePots(site));
            harness.Wire!.Reset();
            await client.SyncOkAsync();
            received[protocol] = harness.Wire.Sent;
            contents[protocol] = (string)(await client.ScalarAsync(
                "SELECT count(*) || ':' || sum(length(Code)) || ':' || sum(Status) || ':' || group_concat(Id, ',') FROM (SELECT * FROM Pot ORDER BY Id)"))!;
        }

        output.WriteLine($"server sent: JSON {received[G9ESyncHubProtocol.Json]:N0} bytes, MessagePack {received[G9ESyncHubProtocol.MessagePack]:N0} bytes");
        Assert.Equal(contents[G9ESyncHubProtocol.Json], contents[G9ESyncHubProtocol.MessagePack]);
        Assert.True(received[G9ESyncHubProtocol.MessagePack] < received[G9ESyncHubProtocol.Json] * 0.85,
            $"MessagePack sent {received[G9ESyncHubProtocol.MessagePack]:N0} bytes, JSON {received[G9ESyncHubProtocol.Json]:N0}.");
    }
}
