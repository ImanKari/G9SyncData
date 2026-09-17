using System.Diagnostics;
using G9SyncData.Client.Progress;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Testing;

namespace G9SyncData.IntegrationTests;

/// <summary>The client engine's run queue, post steps and progress contract.</summary>
public sealed class EngineBehaviourTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task Post_steps_run_after_the_data_and_progress_never_goes_backwards()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 300);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", TestSchema.SitePots(site));

        var progress = new RecordingProgress();
        var stepRuns = 0;
        G9CApplyReport? changesSeenByStep = null;
        var result = await client.SyncOkAsync(new G9CSyncRequest
        {
            Progress = progress,
            PostSteps =
            [
                new G9CSyncPostStep("geo", 0.2, async (context, ct) =>
                {
                    stepRuns++;
                    changesSeenByStep = context.Changes;
                    context.Progress.Report(0.5);
                    await Task.Delay(50, ct);
                    context.Progress.Report(1);
                })
            ]
        });

        Assert.Equal(1, stepRuns);
        Assert.True(changesSeenByStep!.Upserts >= 301, $"The step must see the applied rows; saw {changesSeenByStep.Upserts}.");
        Assert.True(result.Applied >= 301);

        var items = progress.Items;
        Assert.Contains(items, p => p.Phase == G9ESyncPhase.PostStep && p.PostStepName == "geo");
        Assert.Contains(items, p => p.Phase == G9ESyncPhase.Downloading && p.RowsTotal >= 301);
        for (var i = 1; i < items.Count; i++)
        {
            Assert.True(items[i].Overall + 1e-9 >= items[i - 1].Overall, $"Progress went back from {items[i - 1].Overall} ({items[i - 1].Phase}) to {items[i].Overall} ({items[i].Phase}).");
        }

        Assert.Equal(G9ESyncPhase.Completed, items[^1].Phase);
        Assert.Equal(1.0, items[^1].Overall, 3);
    }

    [PostgresFact]
    public async Task A_user_run_preempts_a_background_run_which_is_queued_again()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1500);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", TestSchema.SitePots(site));

        client.InMemory.Faults.Latency = TimeSpan.FromMilliseconds(80);
        var background = client.Engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.Background });
        await Wait.UntilAsync(() => background.LatestProgress?.Phase == G9ESyncPhase.Downloading, TimeSpan.FromSeconds(30), "the background run downloads");

        var user = client.Engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive, Subscriptions = ["sites"] });
        client.InMemory.Faults.Latency = TimeSpan.Zero;

        var backgroundResult = await background.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(G9ESyncOutcome.Canceled, backgroundResult.Outcome);
        Assert.Equal(G9SyncErrorCodes.Canceled, backgroundResult.Error?.Code);

        var userResult = await user.Completion.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(userResult.IsSuccess, SyncClient.Describe(userResult));

        await Wait.UntilAsync(async () => await client.CountAsync("Pot") == 1500 && !client.Engine.IsRunning && client.Engine.QueuedRuns == 0,
            TimeSpan.FromSeconds(60), "the re-queued background run finished the download");
    }

    [PostgresFact]
    public async Task Preempting_a_run_cancels_it_outside_the_queue_lock()
    {
        // LES-0036: preemption cancelled the background run while holding the queue lock. Cancellation runs the token's callbacks on
        // the cancelling thread, and one of them (the progress tracker) waited for a lock the run thread held while it asked the
        // engine for its queue length: a lock-order deadlock that hung the suite. Any callback that reaches the engine must succeed.
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 1500);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", TestSchema.SitePots(site));

        var fired = 0;
        var reachedEngine = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.InMemory.CallStarting += (operation, token) =>
        {
            if (operation != G9ETransportOperation.Pull) return;
            token.Register(() =>
            {
                if (Interlocked.Exchange(ref fired, 1) != 0) return;
                var probe = Task.Run(() =>
                {
                    _ = client.Engine.QueuedRuns;
                    client.Engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.Background, Subscriptions = ["sites"] });
                });
                reachedEngine.TrySetResult(probe.Wait(TimeSpan.FromSeconds(5)));
            });
        };

        client.InMemory.Faults.Latency = TimeSpan.FromMilliseconds(80);
        var background = client.Engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.Background });
        await Wait.UntilAsync(() => background.LatestProgress?.Phase == G9ESyncPhase.Downloading, TimeSpan.FromSeconds(30), "the background run downloads");

        var user = client.Engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.UserInteractive, Subscriptions = ["sites"] });
        client.InMemory.Faults.Latency = TimeSpan.Zero;

        Assert.True(await reachedEngine.Task.WaitAsync(TimeSpan.FromSeconds(30)),
            "A cancellation callback could not reach the engine within 5 s: the run was cancelled under the queue lock.");
        Assert.Equal(G9ESyncOutcome.Canceled, (await background.Completion.WaitAsync(TimeSpan.FromSeconds(30))).Outcome);
        Assert.True((await user.Completion.WaitAsync(TimeSpan.FromSeconds(30))).IsSuccess);
        await Wait.UntilAsync(async () => await client.CountAsync("Pot") == 1500 && !client.Engine.IsRunning && client.Engine.QueuedRuns == 0,
            TimeSpan.FromSeconds(60), "every queued run finished");
    }

    [PostgresFact]
    public async Task Status_reads_answer_during_a_run_and_identical_queued_requests_coalesce()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotsAsync(site, 600);
        await using var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", TestSchema.SitePots(site));

        client.InMemory.Faults.Latency = TimeSpan.FromMilliseconds(60);
        var running = client.Engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.Foreground, Progress = new RecordingProgress() });
        await Wait.UntilAsync(() => running.LatestProgress?.Phase == G9ESyncPhase.Downloading, TimeSpan.FromSeconds(30), "the run downloads");

        var stopwatch = Stopwatch.StartNew();
        await client.Engine.GetPendingSummaryAsync();
        await client.Engine.GetRejectionsAsync();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2), $"Status reads must not wait for the sync; took {stopwatch.ElapsedMilliseconds} ms.");

        var first = client.Engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.Background });
        var second = client.Engine.Run(new G9CSyncRequest { Priority = G9ESyncPriority.Background });
        Assert.Same(first, second);

        client.InMemory.Faults.Latency = TimeSpan.Zero;
        Assert.True((await running.Completion.WaitAsync(TimeSpan.FromSeconds(60))).IsSuccess);
        Assert.True((await first.Completion.WaitAsync(TimeSpan.FromSeconds(60))).IsSuccess);
        Assert.Equal(600, await client.CountAsync("Pot"));
    }
}
