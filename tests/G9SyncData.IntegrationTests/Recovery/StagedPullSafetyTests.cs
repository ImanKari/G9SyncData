using G9SyncData.Client;
using G9SyncData.Client.Progress;
using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Auth;
using G9SyncData.Testing;
using Microsoft.Extensions.DependencyInjection;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Recovery;

/// <summary>
///     A staged pull that was downloaded before the device's state moved on must never put that older state back (ADR-0046). The
///     stage survives a process death by design, so the run that resumes it has to prove the stage still describes the world it was
///     planned for.
/// </summary>
public sealed class StagedPullSafetyTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task A_stage_downloaded_before_a_local_edit_does_not_overwrite_the_row_the_server_accepted()
    {
        await using var harness = await SyncHarness.CreateAsync(output,
            model: m => ConfigureServer(m, new ServerModelOptions(PotConflicts: G9EConflictPolicy.ClientWins)));
        var site = Guid.NewGuid();
        var pot = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotAsync(pot, site, "P-1");
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        // The server moves to 1 and the device downloads it completely, but the process dies before the apply.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 1 WHERE "Id" = $1""", pot);
        await harness.WaitForCaptureAsync();
        await StageFullyAsync(client);

        // The user then edits the same row to 2. The next run pushes it (ClientWins) and must not resurrect the staged 1.
        await client.ExecAsync("UPDATE Pot SET Status = 2 WHERE Id = $p0", Key(pot));
        await client.SyncOkAsync();

        Assert.Equal(2, await harness.ScalarServerAsync<int>("""SELECT "Status" FROM farm."Pot" WHERE "Id" = $1""", pot));
        Assert.Equal(2L, await client.ScalarAsync("SELECT Status FROM Pot WHERE Id = $p0", Key(pot)));
        Assert.Equal(0, (await client.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    [PostgresFact]
    public async Task A_stage_downloaded_before_access_was_revoked_does_not_put_the_revoked_row_back()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.UseAuthContextProvider<MutableAuthProvider>());
        var auth = (MutableAuthProvider)harness.Services.GetRequiredService<IG9SyncAuthContextProvider>();
        var site = Guid.NewGuid();
        var pot = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotAsync(pot, site, "P-1");

        auth.AllowedSites = [site];
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("allowed-pots");
        await client.SyncOkAsync();
        Assert.Equal(1, await client.CountAsync("Pot"));

        // A delta is staged completely, then the caller loses the site.
        await harness.ExecServerAsync("""UPDATE farm."Pot" SET "Status" = 5 WHERE "Id" = $1""", pot);
        await harness.WaitForCaptureAsync();
        await StageFullyAsync(client);

        auth.AllowedSites = [];
        await client.SyncOkAsync();

        Assert.Equal(0, await client.CountAsync("Pot"));
    }

    /// <summary>
    ///     A staged pull carries the cursors and hashes of the permissions it was planned under. Applying it after those permissions
    ///     changed would stamp the old ones back and leave the device believing it is up to date under rules that no longer hold - so
    ///     the run must replan instead of resuming, no matter what the rows themselves say.
    /// </summary>
    [PostgresFact]
    public async Task A_stage_planned_under_old_permissions_does_not_stamp_them_back_over_the_subscription()
    {
        await using var harness = await SyncHarness.CreateAsync(output, server: s => s.UseAuthContextProvider<MutableAuthProvider>());
        var auth = (MutableAuthProvider)harness.Services.GetRequiredService<IG9SyncAuthContextProvider>();
        var north = Guid.NewGuid();
        var south = Guid.NewGuid();
        await harness.InsertSiteAsync(north, "North");
        await harness.InsertSiteAsync(south, "South");
        await harness.InsertPotAsync(Guid.NewGuid(), north, "N-1");
        await harness.InsertPotAsync(Guid.NewGuid(), south, "S-1");

        auth.AllowedSites = [north, south];
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("allowed-pots");
        await client.SyncOkAsync();
        var stale = await AuthHashAsync(client);
        Assert.NotNull(stale);

        await harness.InsertPotAsync(Guid.NewGuid(), north, "N-2");
        await harness.WaitForCaptureAsync();
        await StageFullyAsync(client);

        auth.AllowedSites = [north];
        await client.SyncOkAsync();

        Assert.NotEqual(stale, await AuthHashAsync(client));
        Assert.Equal(2, await client.CountAsync("Pot"));
    }

    [PostgresFact]
    public async Task A_stage_that_still_describes_the_world_is_resumed_instead_of_downloaded_again()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 20);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        await harness.InsertPotsAsync(site, 5, "R");
        await harness.WaitForCaptureAsync();
        await StageFullyAsync(client);

        // Nothing changed since the stage was planned: the run applies it without asking the server for the rows again.
        var plans = client.InMemory.CallCount(G9ETransportOperation.PlanPull);
        var pulls = client.InMemory.CallCount(G9ETransportOperation.Pull);
        var result = await client.SyncOkAsync();

        Assert.Equal(25, await client.CountAsync("Pot"));
        Assert.Equal(plans, client.InMemory.CallCount(G9ETransportOperation.PlanPull));
        Assert.Equal(pulls, client.InMemory.CallCount(G9ETransportOperation.Pull));
        Assert.Equal(5, result.Applied);
    }

    [PostgresFact]
    public async Task An_interrupted_download_survives_a_sync_of_a_different_subscription_set_and_is_resumed()
    {
        // ADR-0060. An application that syncs one scope per run used to lose a half-downloaded site to the very next sync of anything
        // else: that run saw "a stage of another generation" and deleted it.
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 20);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();

        await harness.InsertPotsAsync(site, 5, "R");
        await harness.WaitForCaptureAsync();
        await StageFullyAsync(client, ["site-pots"]);

        // Something else syncs in between - and changes, so its cursor really moves.
        await harness.InsertSiteAsync(Guid.NewGuid(), "South");
        await harness.WaitForCaptureAsync();
        var between = await client.Engine.SyncAsync(new G9CSyncRequest { Subscriptions = ["sites"] });
        Assert.True(between.IsSuccess, between.Error?.Message);
        Assert.Equal(20, await client.CountAsync("Pot"));
        Assert.Equal(2, await client.CountAsync("Site"));
        var sitesCursor = await CursorAsync(client, "sites");

        var plans = client.InMemory.CallCount(G9ETransportOperation.PlanPull);
        var pulls = client.InMemory.CallCount(G9ETransportOperation.Pull);
        var resumed = await client.Engine.SyncAsync(new G9CSyncRequest { Subscriptions = ["site-pots"] });

        Assert.True(resumed.IsSuccess, resumed.Error?.Message);
        Assert.Equal(25, await client.CountAsync("Pot"));
        Assert.Equal(plans, client.InMemory.CallCount(G9ETransportOperation.PlanPull));
        Assert.Equal(pulls, client.InMemory.CallCount(G9ETransportOperation.Pull));

        // A cursor another run moved on in the meantime is not set back by the older plan.
        Assert.Equal(sitesCursor, await CursorAsync(client, "sites"));
        Assert.Equal(2, await client.CountAsync("Site"));
    }

    [PostgresFact]
    public async Task Discarding_staged_pulls_removes_every_stage_not_only_the_newest()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 3);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));

        await StageFullyAsync(client, ["sites"]);
        await StageFullyAsync(client, ["site-pots"]);

        Assert.True(await client.Engine.DiscardStagedPullAsync());
        Assert.Null(await client.Store.GetStagedPullAsync(CancellationToken.None));
        Assert.False(await client.Engine.DiscardStagedPullAsync());
    }

    private static async Task<string?> CursorAsync(SyncClient client, string name) =>
        (await client.Store.GetSubscriptionsAsync(CancellationToken.None)).Single(s => s.Name == name).Cursor;

    private static async Task<string?> AuthHashAsync(SyncClient client) =>
        (await client.Store.GetSubscriptionsAsync(CancellationToken.None)).Single(s => s.Name == "allowed-pots").AuthHash;

    /// <summary>
    ///     Runs a real sync that is killed the instant the download finishes and the apply is about to start - the process death the
    ///     staging design exists for. What is left behind is a complete, unapplied stage planned by the engine itself.
    /// </summary>
    private static async Task StageFullyAsync(SyncClient client, IReadOnlyList<string>? subscriptions = null)
    {
        using var kill = new CancellationTokenSource();
        var request = new G9CSyncRequest
        {
            Subscriptions = subscriptions,
            CancellationToken = kill.Token,
            Progress = new G9CInlineProgress<G9DtSyncProgress>(p =>
            {
                if (p.Phase == G9ESyncPhase.Applying) kill.Cancel();
            })
        };

        Assert.Equal(G9ESyncOutcome.Canceled, (await client.Engine.SyncAsync(request)).Outcome);
        var staged = await client.Store.GetStagedPullAsync(CancellationToken.None);
        Assert.NotNull(staged);
        Assert.True((await client.Store.GetResumePointAsync(staged.RunId, CancellationToken.None)).Complete, "The stage must be complete for the test to mean anything.");
    }
}
