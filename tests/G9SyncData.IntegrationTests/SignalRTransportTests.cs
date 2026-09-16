using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests;

/// <summary>The same engines over real SignalR: Kestrel on loopback, the G9 hub and client, the Postgres listener and pokes.</summary>
public sealed class SignalRTransportTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_device_downloads_and_uploads_over_a_real_signalr_connection()
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 250);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        await client.SyncOkAsync();
        Assert.Equal(250, await client.CountAsync("Pot"));

        var created = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'SR-1', 1, 0)", Key(created), Key(site));
        await client.SyncOkAsync();
        Assert.Equal("SR-1", await harness.ScalarServerAsync<string>("""SELECT "Code" FROM farm."Pot" WHERE "Id" = $1""", created));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task A_server_refusal_reaches_the_client_with_its_stable_code()
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        await using var client = await harness.CreateClientAsync();
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

    [PostgresFact]
    public async Task A_committed_server_change_pokes_devices_holding_the_table()
    {
        await using var harness = await SyncHarness.CreateAsync(output, transport: SyncTransportKind.SignalR);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var client = await harness.CreateClientAsync();
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
}
