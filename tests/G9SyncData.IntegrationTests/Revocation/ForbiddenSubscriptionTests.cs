using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Revocation;

/// <summary>
///     When the server refuses a subscription the caller used to hold, that is a statement about the rows, not a failure to refresh
///     them (ADR-0048): they leave by the same move-out and revocation policies as any other loss of access, the reason says so, and
///     rows another subscription still holds stay put.
/// </summary>
public sealed class ForbiddenSubscriptionTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_forbidden_subscription_releases_the_rows_it_was_holding()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedAsync(harness, site);
        Assert.Equal(3, await client.CountAsync("Pot"));

        Auth(harness).SubscriptionFilter = name => name != "site-pots";
        var result = await client.SyncAsync();
        Assert.Equal(G9ESyncOutcome.CompletedWithRejections, result.Outcome);

        Assert.Contains(result.Problems, p => p.Key.StartsWith("site-pots", StringComparison.Ordinal) && p.Action == G9ESubscriptionAction.Forbidden);
        Assert.Equal(0, await client.CountAsync("Pot"));
        Assert.Equal(3L, result.Changes.RevokedDeleted);
    }

    [PostgresFact]
    public async Task Rows_another_subscription_still_holds_are_not_released()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);

        Auth(harness).AllowedSites = [site];
        await using var client = await SubscribedAsync(harness, site);
        await client.Engine.SubscribeAsync("allowed-pots");
        await client.SyncOkAsync();
        Assert.Equal(3, await client.CountAsync("Pot"));

        // Only site-pots is refused; allowed-pots covers the same rows and is still this caller's.
        Auth(harness).SubscriptionFilter = name => name != "site-pots";
        await client.SyncAsync();

        Assert.Equal(3, await client.CountAsync("Pot"));
        Assert.Empty(await client.Engine.GetQuarantinedAsync());
    }

    [PostgresFact]
    public async Task Unsent_local_work_on_a_revoked_row_is_kept_and_says_why()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedAsync(harness, site);
        var pots = await harness.PotIdsAsync(site);
        await client.ExecAsync("UPDATE Pot SET Note = 'written offline' WHERE Id = $p0", Key(pots[0]));

        Auth(harness).SubscriptionFilter = name => name != "site-pots";
        await client.SyncAsync();

        Assert.Equal(0, await client.CountAsync("Pot"));
        var held = Assert.Single(await client.Engine.GetQuarantinedAsync());
        Assert.Equal(G9ERevocationReason.AccessRevoked, held.Reason);
        Assert.Equal("written offline", held.Values["Note"]);
    }

    [PostgresFact]
    public async Task A_subscription_declared_keep_leaves_the_rows_on_the_device()
    {
        // What an app does when revoked data must stay readable offline and be hidden by the app instead of deleted.
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m => ConfigureServer(m, new ServerModelOptions(PotMoveOut: G9EMoveOutPolicy.Keep)),
            server: s => s.UseAuthContextProvider<MutableAuthProvider>());
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedAsync(harness, site);

        Auth(harness).SubscriptionFilter = name => name != "site-pots";
        await client.SyncAsync();

        Assert.Equal(3, await client.CountAsync("Pot"));
        Assert.Empty(await client.Engine.GetQuarantinedAsync());
    }

    [PostgresFact]
    public async Task Regaining_access_refills_the_subscription()
    {
        await using var harness = await CreateHarnessAsync();
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await SubscribedAsync(harness, site);

        Auth(harness).SubscriptionFilter = name => name != "site-pots";
        await client.SyncAsync();
        Assert.Equal(0, await client.CountAsync("Pot"));

        Auth(harness).SubscriptionFilter = null;
        await client.SyncUntilSettledAsync();

        Assert.Equal(3, await client.CountAsync("Pot"));
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync() => SyncHarness.CreateAsync(output, server: s => s.UseAuthContextProvider<MutableAuthProvider>());

    private static MutableAuthProvider Auth(SyncHarness harness) => (MutableAuthProvider)harness.Services.GetRequiredService<IG9SyncAuthContextProvider>();

    private static async Task<SyncClient> SubscribedAsync(SyncHarness harness, Guid site)
    {
        var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        return client;
    }
}
