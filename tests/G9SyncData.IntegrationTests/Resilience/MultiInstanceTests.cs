using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Testing;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Resilience;

/// <summary>
///     Several server instances behind a load balancer without sticky sessions (G9CSyncServerOptions.TokenSigningKey): everything a
///     device carries between calls — pull tokens, continuations, push batch ids, command ids — must be honoured by ANY instance
///     that shares the key, and a mis-deployed instance without it must fail safely and recover, never apply the wrong thing.
/// </summary>
public sealed class MultiInstanceTests(ITestOutputHelper output)
{
    private static readonly byte[] SharedKey = Enumerable.Range(1, 32).Select(i => (byte)(i * 7)).ToArray();

    [PostgresFact]
    public async Task Every_call_may_land_on_a_different_instance_that_shares_the_signing_key()
    {
        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.TokenSigningKey = SharedKey);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 420);   // 50 rows a page: the pull spans many continuations
        var second = await harness.StartServerAsync();
        var routing = new RoutingTransport(new G9CInMemorySyncTransport(harness.Engine, harness.Notifier), new G9CInMemorySyncTransport(second.Engine));
        await using var client = await harness.CreateClientAsync(transportFactory: () => routing);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        // The pull stream breaks mid-way on the first instance; the continuation is served by the other one.
        routing.Instances[0].Faults.BreakStreamAfter(G9ETransportOperation.Pull, chunks: 3);
        routing.Instances[1].Faults.BreakStreamAfter(G9ETransportOperation.Pull, chunks: 3);
        await client.SyncOkAsync();
        Assert.Equal(420, await client.CountAsync("Pot"));
        Assert.Contains(routing.Route, r => r.StartsWith("PullResume", StringComparison.Ordinal));
        Assert.Contains(routing.Route, r => r.EndsWith("@0", StringComparison.Ordinal));
        Assert.Contains(routing.Route, r => r.EndsWith("@1", StringComparison.Ordinal));

        // A push whose result is lost on one instance is retried on the other, which recognises the batch: applied once.
        var created = Guid.NewGuid();
        await client.ExecAsync("INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, 'MI-1', 0, 0)", Key(created), Key(site));
        await client.ExecAsync("UPDATE Pot SET Status = Status + 1 WHERE SiteId = $p0 AND Code LIKE 'P0000%'", Key(site));
        foreach (var instance in routing.Instances) instance.Faults.FailAfter(G9ETransportOperation.Push);
        await client.SyncOkAsync();
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Code" = 'MI-1'"""));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);

        // Deltas and reconciliation across instances agree with the database.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Note" = 'delta' WHERE "SiteId" = $1 AND "Code" LIKE 'P001%'""", site);
        await client.SyncOkAsync();
        await client.SyncOkAsync(new G9CSyncRequest { ForceReconcile = true });
        Assert.Equal(await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "SiteId" = $1 AND "Note" = 'delta'""", site),
            await client.ScalarAsync("SELECT count(*) FROM Pot WHERE Note = 'delta'"));
        Assert.Equal(421, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task An_instance_without_the_shared_key_refuses_foreign_tokens_and_the_device_recovers_on_the_next_run()
    {
        await using var harness = await SyncHarness.CreateAsync(output, configure: o => o.TokenSigningKey = SharedKey);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 120);
        var misdeployed = await harness.StartServerAsync(configure: o => o.TokenSigningKey = Enumerable.Repeat((byte)9, 32).ToArray());
        var routing = new RoutingTransport(new G9CInMemorySyncTransport(harness.Engine, harness.Notifier), new G9CInMemorySyncTransport(misdeployed.Engine));
        await using var client = await harness.CreateClientAsync(transportFactory: () => routing, configure: o => o.MaxRetries = 0);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        // Hello on 0, plan on 1, pull on 0: the token signed by one key is presented to the other.
        var failed = await client.SyncAsync();
        Assert.Equal(G9ESyncOutcome.Failed, failed.Outcome);
        Assert.Equal(G9SyncErrorCodes.TokenInvalid, failed.Error?.Code);
        Assert.Equal(0, await client.CountAsync("Pot"));   // nothing half-applied

        routing.Pin = 0;
        await client.SyncOkAsync();
        Assert.Equal(120, await client.CountAsync("Pot"));
    }
}
