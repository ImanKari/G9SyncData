using System.Diagnostics;
using G9SyncData.Server.Capture;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace G9SyncData.Server.PostgreSql.Capture;

/// <summary>
///     The WAL consumer (ADR-0028): records changes made outside G9SyncData in the tracking tables, answers capture sync
///     points, and repairs a capture gap (a lost or missing slot) with a rescan.
///     <para>
///         Ordering contract, and the reason it cannot be loosened: a batch's WAL position is confirmed only after its
///         tracking rows are committed (LES-0015), and sync points are answered in stream order, so answering one proves
///         every earlier transaction is already recorded. Pushes do NOT depend on this service: they record the state of the
///         rows they touch themselves, so a stopped consumer delays deliveries to devices but never blocks or corrupts a write.
///     </para>
///     <para>
///         Exactly one consumer per slot runs at a time: PostgreSQL refuses a second connection to an active slot, so extra
///         server instances simply retry in the background and take over if the active one stops.
///     </para>
/// </summary>
internal sealed partial class G9CWalCaptureService(
    G9CSyncServerEngine engine,
    G9CPostgresSyncStore store,
    ILoggerFactory loggerFactory) : BackgroundService
{
    private readonly ILogger<G9CWalCaptureService> _logger = loggerFactory.CreateLogger<G9CWalCaptureService>();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var wal = store.Options.Wal;
        var delay = wal.RetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await engine.EnsureInitializedAsync(stoppingToken).ConfigureAwait(false);
                await ConsumeAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                G9CPostgresWalTelemetry.ConsumerRestarts.Add(1);
                LogConsumerFailed(_logger, exception, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromTicks(Math.Min(wal.MaxRetryDelay.Ticks, delay.Ticks * 2));
        }
    }

    private async Task ConsumeAsync(CancellationToken stoppingToken)
    {
        var wal = store.Options.Wal;

        // Before attaching: a missing or invalidated slot is re-created, and the gap it implies is scheduled for a rescan.
        await store.EnsureSlotForConsumerAsync(stoppingToken).ConfigureAwait(false);

        await using var source = new G9CLogicalReplicationChangeSource(
            store.Model,
            new G9CLogicalReplicationOptions
            {
                ConnectionString = store.ReplicationConnectionString,
                PublicationName = wal.PublicationName,
                SlotName = wal.SlotName,
                CreateSlotIfMissing = false   // only the store creates slots, because only it can tell a first start from a gap
            },
            loggerFactory.CreateLogger<G9CLogicalReplicationChangeSource>());

        LogConsumerStarted(_logger, wal.SlotName, wal.PublicationName);

        // A pending rescan runs beside the stream: both record current state with the same idempotent statement, so their
        // order does not matter, and the slot already covers every change made while the rescan runs.
        using var rescanCancellation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var rescan = RunRescanAsync(rescanCancellation.Token);

        var sincePurge = Stopwatch.StartNew();
        try
        {

        await foreach (var batch in source.ReadAsync(G9CChangePosition.Start, stoppingToken).ConfigureAwait(false))
        {
            if (batch.IsSyncPoint)
            {
                await store.RecordSyncPointAsync(batch.SyncPoint!.Value, stoppingToken).ConfigureAwait(false);
            }
            else
            {
                if (batch.OriginClientId.HasValue)
                {
                    // Tracked synchronously by the push's own guarded triggers - recording it again would bump every
                    // version and make the pushing device download its own rows back.
                    G9CPostgresWalTelemetry.PushTransactionsSkipped.Add(1);
                }
                else
                {
                    var rows = await store.ApplyWalBatchAsync(batch, stoppingToken).ConfigureAwait(false);
                    G9CPostgresWalTelemetry.TransactionsApplied.Add(1);
                    G9CPostgresWalTelemetry.RowsApplied.Add(rows);
                }

                // Only now, with the tracking rows committed, may the position advance (LES-0015).
                await source.ConfirmAsync(batch.Position, stoppingToken).ConfigureAwait(false);
            }

            if (sincePurge.Elapsed >= wal.SyncPointRetention)
            {
                await store.PurgeSyncPointsAsync(wal.SyncPointRetention, stoppingToken).ConfigureAwait(false);
                sincePurge.Restart();
            }
        }
        }
        finally
        {
            await rescanCancellation.CancelAsync().ConfigureAwait(false);
            await rescan.ConfigureAwait(false);
        }
    }

    private async Task RunRescanAsync(CancellationToken cancellationToken)
    {
        try
        {
            await store.RescanPendingAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // the consumer is stopping; the flag stays set and the next attach resumes the rescan
        }
        catch (Exception exception)
        {
            LogRescanFailed(_logger, exception);
        }
    }

    [LoggerMessage(EventId = 7320, Level = LogLevel.Information, Message = "G9SyncData WAL consumer streaming from slot '{SlotName}', publication '{Publication}'")]
    private static partial void LogConsumerStarted(ILogger logger, string slotName, string publication);

    [LoggerMessage(EventId = 7321, Level = LogLevel.Warning, Message = "G9SyncData WAL consumer failed; reconnecting in {DelaySeconds} s")]
    private static partial void LogConsumerFailed(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(EventId = 7322, Level = LogLevel.Error, Message = "G9SyncData rescan failed; it stays pending and resumes on the next attach")]
    private static partial void LogRescanFailed(ILogger logger, Exception exception);
}
