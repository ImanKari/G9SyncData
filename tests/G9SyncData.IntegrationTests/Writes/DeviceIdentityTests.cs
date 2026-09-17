using System.Security.Claims;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     A client id is a value the caller supplies, and the server keeps a push receipt on it so an interrupted push can be replayed
///     safely. That makes the device record an authorisation boundary: it is claimed by the first authenticated caller and never
///     answers to another one (ADR-0047).
/// </summary>
public sealed class DeviceIdentityTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Another_user_quoting_a_device_id_and_batch_does_not_receive_its_push_receipt()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var (client, pot) = await ConflictedClientAsync(harness);
        var clientId = await client.Store.GetClientIdAsync(CancellationToken.None);
        var batchId = await harness.ScalarServerAsync<long>("SELECT last_push_batch FROM g9sync.clients WHERE client_id = $1", clientId);
        Assert.True(batchId > 0, "The conflict must have produced a stored receipt.");

        // Alice's own replay is the guarantee the receipt exists for: the same batch returns the same answer, canonical rows included.
        var replay = await harness.Engine.PushAsync(Caller("alice"), Request(clientId, batchId), Nothing());
        Assert.True(replay.AlreadyApplied);
        Assert.NotEmpty(replay.CanonicalFrames);

        var bob = await Assert.ThrowsAsync<G9CSyncException>(() => harness.Engine.PushAsync(Caller("bob"), Request(clientId, batchId), Nothing()));
        Assert.Equal(G9SyncErrorCodes.Unauthorized, bob.Code);
        Assert.Equal(7, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
    }

    [PostgresFact]
    public async Task Another_user_cannot_take_over_a_device_record_by_saying_hello_with_its_id()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var (client, _) = await ConflictedClientAsync(harness);
        var clientId = await client.Store.GetClientIdAsync(CancellationToken.None);

        var hello = await Assert.ThrowsAsync<G9CSyncException>(() => harness.Engine.HelloAsync(Caller("bob"), new G9DtHelloRequest { ClientId = clientId }));
        Assert.Equal(G9SyncErrorCodes.Unauthorized, hello.Code);

        // The takeover must not have happened even in part: the device still belongs to Alice and still syncs.
        Assert.Equal("alice", await harness.ScalarServerAsync<string>("SELECT user_id FROM g9sync.clients WHERE client_id = $1", clientId));
        await client.SyncOkAsync();
    }

    [PostgresFact]
    public async Task A_device_that_no_authenticated_caller_has_claimed_still_works()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotAsync(Guid.NewGuid(), site, "P-1");

        // No claims: deployments without authentication have no identity to bind to, and must keep working.
        await using var client = await harness.CreateClientAsync(caller: G9CSyncCaller.Anonymous);
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", TestSchema.SitePots(site));
        await client.SyncOkAsync();

        Assert.Equal(1, await client.CountAsync("Pot"));
    }

    /// <summary>Alice's device with a conflict behind it, so the stored receipt carries canonical rows worth stealing.</summary>
    private static async Task<(SyncClient Client, Guid Pot)> ConflictedClientAsync(SyncHarness harness)
    {
        var site = Guid.NewGuid();
        var pot = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotAsync(pot, site, "P-1");

        var client = await harness.CreateClientAsync(caller: Caller("alice"));
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", TestSchema.SitePots(site));
        await client.SyncOkAsync();

        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 7 WHERE "Id" = $1""", pot);
        await harness.WaitForCaptureAsync();
        await client.ExecAsync("UPDATE Pot SET Status = 9 WHERE Id = $p0", TestSchema.Key(pot));

        var result = await client.SyncOkAsync();
        Assert.True(result.Conflicts > 0, "The test needs a conflict to produce canonical rows.");
        return (client, pot);
    }

    private static G9CSyncCaller Caller(string userId) =>
        new() { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId)], "test")) };

    private static G9DtPushRequest Request(Guid clientId, long batchId) => new() { ClientId = clientId, BatchId = batchId };

    private static async IAsyncEnumerable<G9DtSyncChunk> Nothing()
    {
        await Task.CompletedTask;
        yield break;
    }
}
