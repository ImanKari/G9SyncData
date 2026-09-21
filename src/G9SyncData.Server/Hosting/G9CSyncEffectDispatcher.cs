using G9SyncData.Server.Diagnostics;
using G9SyncData.Server.Pipeline;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Server.Hosting;

/// <summary>
///     Delivers the effects a committed push recorded (ADR-0052). Delivery is at-least-once: an effect is completed only after its
///     handlers returned, so a crash in between re-delivers it, which is why handlers must be idempotent. An effect whose attempts run
///     out stops being retried and stays in the outbox marked failed - visible to operators - rather than being dropped or retried for
///     ever.
/// </summary>
internal sealed partial class G9CSyncEffectDispatcher(
    IG9SyncServerStore store,
    IServiceScopeFactory scopeFactory,
    ILogger<G9CSyncEffectDispatcher> logger)
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    /// <summary>Wakes the loop after a push recorded effects, so the common case does not wait for the next poll.</summary>
    public void Nudge()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already awake; one pending wake-up is enough.
        }
    }

    public async Task RunAsync(G9CSyncServerOptions options, CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Keep draining while a claim comes back full: a backlog should not be paced by the poll interval.
                while (await DispatchOnceAsync(options, stoppingToken).ConfigureAwait(false) == options.EffectBatchSize)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                LogDispatchLoopFailed(logger, exception, options.EffectPollInterval.TotalSeconds);
            }

            try
            {
                await _signal.WaitAsync(options.EffectPollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    /// <summary>Claims one batch and delivers it. Returns how many effects were claimed.</summary>
    public async Task<int> DispatchOnceAsync(G9CSyncServerOptions options, CancellationToken cancellationToken)
    {
        var claimed = await store.ClaimEffectsAsync(options.EffectBatchSize, options.EffectRetryDelay, cancellationToken).ConfigureAwait(false);
        foreach (var effect in claimed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await DeliverAsync(effect, options, cancellationToken).ConfigureAwait(false);
        }

        await RefreshQueueStatusAsync(cancellationToken).ConfigureAwait(false);
        return claimed.Count;
    }

    /// <summary>
    ///     Reads the queue's size for the gauges (ADR-0065), once per dispatch pass: a gauge callback must not open a database connection
    ///     of its own every time a meter is scraped. A failure here never disturbs delivery.
    /// </summary>
    private async Task RefreshQueueStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await store.GetEffectQueueStatusAsync(cancellationToken).ConfigureAwait(false);
            var failedBefore = G9CSyncServerTelemetry.EffectQueue.Failed;
            G9CSyncServerTelemetry.EffectQueue = status;

            // Said once when the count goes UP, not on every pass: a failed effect is work the push owes that nobody is doing.
            if (status.Failed > failedBefore) LogEffectsFailedForGood(logger, status.Failed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogQueueStatusFailed(logger, exception);
        }
    }

    private async Task DeliverAsync(G9CPendingEffect effect, G9CSyncServerOptions options, CancellationToken cancellationToken)
    {
        var table = store.FindTable(effect.TableId);
        if (table is null)
        {
            // The table left the model. Nothing can be owed to it any more, and keeping the row would fail for ever.
            LogEffectAbandoned(logger, effect.EffectId, effect.TableId);
            await store.CompleteEffectAsync(effect.EffectId, cancellationToken).ConfigureAwait(false);
            return;
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var handlers = scope.ServiceProvider.GetServices<IG9EffectHandler>().Where(h => h.AppliesTo(table)).ToArray();
        if (handlers.Length == 0)
        {
            LogEffectUnhandled(logger, effect.EffectId, table.LogicalName);
            await store.CompleteEffectAsync(effect.EffectId, cancellationToken).ConfigureAwait(false);
            return;
        }

        var context = new G9CEffectContext
        {
            EffectId = effect.EffectId,
            Table = table,
            ClientId = effect.ClientId,
            BatchId = effect.BatchId,
            UserId = effect.UserId,
            UpsertedKeys = effect.UpsertedKeys,
            DeletedKeys = effect.DeletedKeys,
            Attempt = effect.Attempt,
            Services = scope.ServiceProvider
        };

        try
        {
            foreach (var handler in handlers)
            {
                await handler.HandleAsync(context, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            var terminal = effect.Attempt >= options.EffectMaxAttempts;
            G9CSyncServerTelemetry.EffectDeliveries.Add(1, new KeyValuePair<string, object?>("outcome", terminal ? "failed_for_good" : "failed"));
            LogEffectFailed(logger, exception, effect.EffectId, table.LogicalName, effect.Attempt, options.EffectMaxAttempts, terminal);
            await store.FailEffectAsync(effect.EffectId, exception.Message, options.EffectMaxAttempts, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        // Only now: a completion recorded before the handlers ran would lose the work the outbox exists to protect.
        await store.CompleteEffectAsync(effect.EffectId, cancellationToken).ConfigureAwait(false);
        G9CSyncServerTelemetry.EffectDeliveries.Add(1, new KeyValuePair<string, object?>("outcome", "delivered"));
        LogEffectDelivered(logger, effect.EffectId, table.LogicalName, effect.Attempt);
    }

    [LoggerMessage(EventId = 21025, Level = LogLevel.Error,
        Message = "{Failed} effect(s) have exhausted their attempts and are no longer retried: work a committed push owes is NOT being done. " +
                  "Fix the handler, then requeue them (RequeueFailedEffectsAsync)")]
    private static partial void LogEffectsFailedForGood(ILogger logger, long failed);

    [LoggerMessage(EventId = 21026, Level = LogLevel.Debug, Message = "Could not read the effect queue status for the gauges")]
    private static partial void LogQueueStatusFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 21020, Level = LogLevel.Debug, Message = "Effect {EffectId} for {Table} delivered on attempt {Attempt}")]
    private static partial void LogEffectDelivered(ILogger logger, Guid effectId, string table, int attempt);

    [LoggerMessage(EventId = 21021, Level = LogLevel.Error,
        Message = "Effect {EffectId} for {Table} failed on attempt {Attempt} of {MaxAttempts} (terminal: {Terminal})")]
    private static partial void LogEffectFailed(ILogger logger, Exception exception, Guid effectId, string table, int attempt, int maxAttempts, bool terminal);

    [LoggerMessage(EventId = 21022, Level = LogLevel.Warning, Message = "Effect {EffectId} has no handler for {Table} any more; dropping it")]
    private static partial void LogEffectUnhandled(ILogger logger, Guid effectId, string table);

    [LoggerMessage(EventId = 21023, Level = LogLevel.Warning, Message = "Effect {EffectId} refers to table {TableId}, which the model no longer has; dropping it")]
    private static partial void LogEffectAbandoned(ILogger logger, Guid effectId, int tableId);

    [LoggerMessage(EventId = 21024, Level = LogLevel.Error, Message = "Effect dispatch failed; retrying in {Seconds} s")]
    private static partial void LogDispatchLoopFailed(ILogger logger, Exception exception, double seconds);
}
