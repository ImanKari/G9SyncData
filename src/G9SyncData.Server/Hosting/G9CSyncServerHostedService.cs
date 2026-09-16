using G9SyncData.Server.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace G9SyncData.Server.Hosting;

/// <summary>
///     Initialises the store at startup (a failure stops the host: a bad model must fail the server, never a device),
///     listens for committed changes, and purges old tombstones on a timer.
/// </summary>
internal sealed partial class G9CSyncServerHostedService(
    G9CSyncServerEngine engine,
    IG9SyncServerStore store,
    G9CSyncChangeNotifier notifier,
    IOptions<G9CSyncServerOptions> options,
    ILogger<G9CSyncServerHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (settings.InitializeOnStartup)
        {
            await engine.EnsureInitializedAsync(stoppingToken).ConfigureAwait(false);
        }

        var listen = settings.EnableChangeNotifications ? ListenLoopAsync(stoppingToken) : Task.CompletedTask;
        var cleanup = CleanupLoopAsync(settings, stoppingToken);
        await Task.WhenAll(listen, cleanup).ConfigureAwait(false);
    }

    private async Task ListenLoopAsync(CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await engine.EnsureInitializedAsync(stoppingToken).ConfigureAwait(false);
                await foreach (var tableIds in store.ListenForChangesAsync(stoppingToken).ConfigureAwait(false))
                {
                    delay = TimeSpan.FromSeconds(1);
                    var names = tableIds.Select(store.FindTable).OfType<Model.G9CSyncServerTable>().Select(t => t.LogicalName)
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    if (names.Length > 0) notifier.Publish(names);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LogListenFailed(logger, ex, delay.TotalSeconds);
            }

            try
            {
                await Task.Delay(delay, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            delay = TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2));
        }
    }

    private async Task CleanupLoopAsync(G9CSyncServerOptions settings, CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(settings.CleanupInterval);
        try
        {
            do
            {
                try
                {
                    await engine.EnsureInitializedAsync(stoppingToken).ConfigureAwait(false);
                    var removed = await store.CleanupTombstonesAsync(settings.TombstoneRetention, stoppingToken).ConfigureAwait(false);
                    if (removed > 0) LogCleaned(logger, removed);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogCleanupFailed(logger, ex);
                }
            } while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            // host stopping
        }
    }

    [LoggerMessage(EventId = 21010, Level = LogLevel.Warning, Message = "Change listener failed; retrying in {DelaySeconds} s")]
    private static partial void LogListenFailed(ILogger logger, Exception exception, double delaySeconds);

    [LoggerMessage(EventId = 21011, Level = LogLevel.Information, Message = "Purged {Count} tombstones past the retention horizon")]
    private static partial void LogCleaned(ILogger logger, int count);

    [LoggerMessage(EventId = 21012, Level = LogLevel.Warning, Message = "Tombstone cleanup failed")]
    private static partial void LogCleanupFailed(ILogger logger, Exception exception);
}
