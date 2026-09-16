using Microsoft.Extensions.Logging;

namespace G9SyncData.Server;

/// <summary>
///     In-process fan-out of "these tables have committed changes". The hosted service feeds it from the store's
///     listener; transports subscribe to poke connected clients. Each server instance listens to the database itself,
///     so no cross-instance backplane is needed.
/// </summary>
public sealed partial class G9CSyncChangeNotifier(ILogger<G9CSyncChangeNotifier> logger)
{
    /// <summary>Raised with the logical names of tables that changed. Handlers must be fast and non-blocking.</summary>
    public event Action<IReadOnlyList<string>>? TablesChanged;

    public void Publish(IReadOnlyList<string> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);
        var handlers = TablesChanged;
        if (handlers is null || tables.Count == 0) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<IReadOnlyList<string>>>())
        {
            try
            {
                handler(tables);
            }
            catch (Exception ex)
            {
                LogHandlerFailed(logger, ex);
            }
        }
    }

    [LoggerMessage(EventId = 21020, Level = LogLevel.Warning, Message = "A TablesChanged handler threw")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception);
}
