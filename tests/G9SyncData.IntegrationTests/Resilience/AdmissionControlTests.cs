using G9SyncData.Client.Runs;
using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Resilience;

/// <summary>
///     The server's concurrency limits (ADR-0062). A request past the limit is refused with the retryable <c>G9S-SRV-BUSY</c> instead of
///     taking another connection and another buffer; the device backs off and gets through once there is room. Nothing is lost while it
///     waits: a refused push leaves its rows pending.
/// </summary>
public sealed class AdmissionControlTests(ITestOutputHelper output)
{
    private const string InsertPot = "INSERT INTO Pot (Id, SiteId, Code, Status, IsDeleted) VALUES ($p0, $p1, $p2, $p3, 0)";

    [PostgresFact]
    public async Task A_push_past_the_limit_is_refused_as_busy_keeps_its_rows_and_goes_through_once_there_is_room()
    {
        var gate = new PushGate();
        await using var harness = await SyncHarness.CreateAsync(output,
            configure: o =>
            {
                o.MaxConcurrentPushes = 1;
                o.AdmissionWait = TimeSpan.FromMilliseconds(100);
            },
            server: s =>
            {
                s.Services.AddSingleton(gate);
                s.AddValidator<GatedValidator>();
            });
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await using var first = await harness.CreateSubscribedClientAsync(site);
        await using var second = await harness.CreateSubscribedClientAsync(site, o =>
        {
            o.MaxRetries = 1;
            o.RetryBaseDelay = TimeSpan.FromMilliseconds(10);
        });

        await first.ExecAsync(InsertPot, Key(Guid.NewGuid()), Key(site), "FIRST", 1);
        var held = Guid.NewGuid();
        await second.ExecAsync(InsertPot, Key(held), Key(site), "SECOND", 1);

        // The first push takes the only slot and stays inside its validator.
        var running = first.SyncAsync();
        await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var refused = await second.SyncAsync();
        Assert.Equal(G9ESyncOutcome.Failed, refused.Outcome);
        Assert.Equal(G9SyncErrorCodes.ServerBusy, refused.Error?.Code);
        Assert.True(refused.Error!.Retryable);
        Assert.Equal(1, (await second.Engine.GetPendingSummaryAsync()).TotalRows);

        gate.Release.SetResult();
        Assert.True((await running).IsSuccess);

        var retried = await second.SyncAsync();
        Assert.True(retried.IsSuccess, SyncClient.Describe(retried));
        Assert.Equal(1L, await harness.ScalarServerAsync<long>("""SELECT count(*) FROM farm."Pot" WHERE "Id" = $1""", held));
        Assert.Equal(0, (await second.Engine.GetPendingSummaryAsync()).TotalRows);
    }

    internal sealed class PushGate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Holds the first push that reaches it until the test lets go; later pushes pass straight through.</summary>
    internal sealed class GatedValidator(PushGate gate) : IG9UploadValidator
    {
        public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

        public async ValueTask ValidateAsync(G9CUploadStepContext context, CancellationToken cancellationToken)
        {
            if (!gate.Entered.TrySetResult()) return;
            await gate.Release.Task.WaitAsync(cancellationToken);
        }
    }
}
