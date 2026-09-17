using G9SyncData.IntegrationTests.Infrastructure;
using G9SyncData.Server;
using G9SyncData.Server.Hosting;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using static G9SyncData.IntegrationTests.Infrastructure.TestSchema;

namespace G9SyncData.IntegrationTests.Writes;

/// <summary>
///     Work a committed push owes is recorded in the push's own transaction and delivered until it succeeds (ADR-0052). A handler that
///     throws, a process that dies before delivery, and a batch that is replayed all end the same way: delivered. What is not owed is
///     never recorded, and what can never succeed stops and stays visible.
/// </summary>
public sealed class EffectOutboxTests(ITestOutputHelper output)
{
    [PostgresFact]
    public async Task An_effect_whose_first_delivery_throws_is_delivered_by_a_later_attempt()
    {
        var recorder = new EffectRecorder { FailAttempts = 1 };
        await using var harness = await CreateHarnessAsync(recorder);
        var pot = await PushedPotAsync(harness);

        Assert.Equal(1, await DispatchAsync(harness));
        Assert.Empty(recorder.Delivered);
        Assert.Equal(1, recorder.Attempts);

        // The failure left the effect owed, not lost.
        Assert.Equal(1, await DispatchAsync(harness, dueNow: true));
        Assert.Equal(pot.ToString(), Assert.Single(recorder.Delivered));
    }

    [PostgresFact]
    public async Task An_effect_recorded_before_a_crash_is_delivered_after_it()
    {
        var recorder = new EffectRecorder();
        await using var harness = await CreateHarnessAsync(recorder);
        var pot = await PushedPotAsync(harness);

        // Nothing has dispatched yet: this is the state a process killed right after the commit leaves behind.
        Assert.Empty(recorder.Delivered);
        Assert.Equal(1, await PendingEffectsAsync(harness));

        Assert.Equal(1, await DispatchAsync(harness));
        Assert.Equal(pot.ToString(), Assert.Single(recorder.Delivered));
        Assert.Equal(0, await PendingEffectsAsync(harness));
    }

    [PostgresFact]
    public async Task A_replayed_push_still_owes_its_effect()
    {
        var recorder = new EffectRecorder();
        await using var harness = await CreateHarnessAsync(recorder);
        await PushedPotAsync(harness);

        // The old in-process callback ran only on the original call, so a replay lost the work entirely. The record does not care
        // which call delivered the rows.
        Assert.Equal(1, await PendingEffectsAsync(harness));
        await DispatchAsync(harness);
        Assert.Single(recorder.Delivered);
    }

    [PostgresFact]
    public async Task An_effect_that_never_succeeds_stops_and_stays_visible()
    {
        var recorder = new EffectRecorder { FailAttempts = int.MaxValue };
        await using var harness = await CreateHarnessAsync(recorder, o => o.EffectMaxAttempts = 3);
        await PushedPotAsync(harness);

        for (var i = 0; i < 5; i++) await DispatchAsync(harness, dueNow: true);

        Assert.Equal(3, recorder.Attempts);
        var failed = Assert.Single(await harness.Services.GetRequiredService<IG9SyncServerStore>().GetFailedEffectsAsync(10, CancellationToken.None));
        Assert.Equal(3, failed.Attempt);
        Assert.Contains("on purpose", failed.LastError, StringComparison.Ordinal);
    }

    [PostgresFact]
    public async Task A_push_with_no_effect_handler_records_nothing()
    {
        await using var harness = await SyncHarness.CreateAsync(output);
        await PushedPotAsync(harness);

        Assert.Equal(0, await PendingEffectsAsync(harness));
    }

    [PostgresFact]
    public async Task A_rejected_row_owes_no_effect()
    {
        var recorder = new EffectRecorder();
        await using var harness = await CreateHarnessAsync(recorder, server: s => s.AddValidator<NegativeStatusValidator>());
        var site = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotAsync(Guid.NewGuid(), site, "P-1");
        await using var client = await SubscribedAsync(harness, site);
        await client.ExecAsync("UPDATE Pot SET Status = -1");
        await client.SyncAsync();

        Assert.Equal(0, await PendingEffectsAsync(harness));
    }

    // ================================================================ fixture

    private Task<SyncHarness> CreateHarnessAsync(EffectRecorder recorder, Action<G9CSyncServerOptions>? options = null,
        Action<G9CSyncServerBuilder>? server = null) =>
        SyncHarness.CreateAsync(output, server: s =>
        {
            s.Services.AddSingleton(recorder);
            s.AddEffectHandler<RecordingEffectHandler>();
            server?.Invoke(s);
            if (options is not null) s.Services.Configure(options);
        });

    private static async Task<Guid> PushedPotAsync(SyncHarness harness)
    {
        var site = Guid.NewGuid();
        var pot = Guid.NewGuid();
        await harness.InsertSiteAsync(site, "North");
        await harness.InsertPotAsync(pot, site, "P-1");
        await using var client = await SubscribedAsync(harness, site);
        await client.ExecAsync("UPDATE Pot SET Status = 4 WHERE Id = $p0", Key(pot));
        await client.SyncOkAsync();
        return pot;
    }

    private static async Task<SyncClient> SubscribedAsync(SyncHarness harness, Guid site)
    {
        var client = await harness.CreateClientAsync();
        await client.Engine.SubscribeAsync("sites");
        await client.Engine.SubscribeAsync("site-pots", SitePots(site));
        await client.SyncOkAsync();
        return client;
    }

    /// <summary>Runs one dispatch pass; <paramref name="dueNow" /> brings a backed-off effect forward instead of waiting for it.</summary>
    private static async Task<int> DispatchAsync(SyncHarness harness, bool dueNow = false)
    {
        if (dueNow) await harness.ExecServerAsync("UPDATE g9sync.effects SET next_attempt_at = now() - interval '1 hour'");
        var options = harness.Services.GetRequiredService<IOptions<G9CSyncServerOptions>>().Value;
        return await harness.Services.GetRequiredService<G9CSyncEffectDispatcher>().DispatchOnceAsync(options, CancellationToken.None);
    }

    private static Task<long> PendingEffectsAsync(SyncHarness harness) =>
        harness.ScalarServerAsync<long>("SELECT count(*) FROM g9sync.effects");
}

/// <summary>What the effect handler saw, shared with the test.</summary>
internal sealed class EffectRecorder
{
    private int _attempts;

    public int FailAttempts { get; init; }
    public int Attempts => _attempts;
    public List<string> Delivered { get; } = [];

    public int NextAttempt() => Interlocked.Increment(ref _attempts);
}

internal sealed class RecordingEffectHandler(EffectRecorder recorder) : IG9EffectHandler
{
    public bool AppliesTo(G9CSyncServerTable table) => table.LogicalName == "farm.Pot";

    public ValueTask HandleAsync(G9CEffectContext context, CancellationToken cancellationToken)
    {
        if (recorder.NextAttempt() <= recorder.FailAttempts) throw new InvalidOperationException("The handler failed on purpose.");
        lock (recorder.Delivered) recorder.Delivered.AddRange(context.UpsertedKeys);
        return ValueTask.CompletedTask;
    }
}
