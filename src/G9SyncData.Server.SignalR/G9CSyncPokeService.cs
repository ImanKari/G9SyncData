using G9SyncData.Protocol;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace G9SyncData.Server.SignalR;

/// <summary>
///     Turns committed-change notifications into <c>OnChangesAvailable</c> pokes. Changes are coalesced per table for
///     <see cref="G9CSyncSignalROptions.PokeCoalescence" /> and sent only to the group of connections that negotiated the
///     table, so a busy table costs one small message per window, not one per statement. A poke is a hint: clients
///     still pull with their cursors, so a lost poke only delays a sync.
/// </summary>
internal sealed partial class G9CSyncPokeService(
    G9CSyncChangeNotifier notifier,
    IHubContext<G9SyncHub> hubContext,
    IOptions<G9CSyncSignalROptions> options,
    ILogger<G9CSyncPokeService> logger) : IHostedService, IDisposable
{
    private readonly Lock _lock = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private bool _scheduled;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.EnablePokes) return Task.CompletedTask;
        _timer = new Timer(static state => _ = ((G9CSyncPokeService)state!).FlushAsync(), this, Timeout.Infinite, Timeout.Infinite);
        notifier.TablesChanged += OnTablesChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        notifier.TablesChanged -= OnTablesChanged;
        _timer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    public void Dispose() => _timer?.Dispose();

    private void OnTablesChanged(IReadOnlyList<string> tables)
    {
        lock (_lock)
        {
            foreach (var table in tables) _pending.Add(table);
            if (_scheduled) return;
            _scheduled = true;
        }

        _timer?.Change(options.Value.PokeCoalescence, Timeout.InfiniteTimeSpan);
    }

    private async Task FlushAsync()
    {
        string[] tables;
        lock (_lock)
        {
            tables = [.. _pending];
            _pending.Clear();
            _scheduled = false;
        }

        foreach (var table in tables)
        {
            try
            {
                await hubContext.Clients.Group(G9SyncProtocol.TableGroup(table))
                    .SendAsync(G9SyncProtocol.Listeners.OnChangesAvailable, new G9DtChangesAvailable { Tables = [table] }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                LogPokeFailed(logger, ex, table);
            }
        }
    }

    [LoggerMessage(EventId = 21110, Level = LogLevel.Warning, Message = "Poke for {Table} could not be sent")]
    private static partial void LogPokeFailed(ILogger logger, Exception exception, string table);
}
