using System.Data.Common;
using System.Net.Sockets;
using System.Net.WebSockets;
using G9SyncData.Client.Progress;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;

namespace G9SyncData.Client;

public sealed class G9CSyncEngineOptions
{
    /// <summary>Sent at Hello; the server's connect gates may refuse old versions.</summary>
    public string? AppVersion { get; set; }

    public int PushBatchRows { get; set; } = 2000;

    /// <summary>Transient failures retried inside one run (push and pull resume from where they stopped).</summary>
    public int MaxRetries { get; set; } = 5;

    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Queue a background pull when the server pokes about changed tables.</summary>
    public bool PullOnChangesAvailable { get; set; } = true;

    public TimeSpan ChangesDebounce { get; set; } = TimeSpan.FromSeconds(2);

    public double ProgressMaxHz { get; set; } = 10;

    public int FetchKeysPerRequest { get; set; } = 2000;

    /// <summary>A staged pull older than this is discarded instead of resumed (its token would have expired).</summary>
    public TimeSpan StagedPullMaxAge { get; set; } = TimeSpan.FromMinutes(90);

    public int KeepDeletedFeedEntries { get; set; } = 50_000;

    public int KeepRunReports { get; set; } = 50;
}

/// <summary>Platform integration points (foreground service / keep-screen-on / connectivity).</summary>
public interface IG9SyncHostHooks
{
    /// <summary>Called when a run starts executing; the returned scope is disposed when it ends.</summary>
    ValueTask<IAsyncDisposable?> BeginRunAsync(G9CSyncRun run, CancellationToken cancellationToken);

    /// <summary>False skips automatic background runs.</summary>
    bool IsNetworkAvailable { get; }
}

public sealed class G9CSyncHealth
{
    public required Guid ClientId { get; init; }
    public required bool IsConnected { get; init; }
    public required bool IsRunning { get; init; }
    public required int QueuedRuns { get; init; }
    public Guid? ServerEpoch { get; init; }
    public DateTime? LastHelloUtc { get; init; }
    public required G9CPendingSummary Pending { get; init; }
    public required IReadOnlyList<G9CSyncSubscriptionRecord> Subscriptions { get; init; }
    public required IReadOnlyList<G9CSyncRunReport> RecentRuns { get; init; }
}

public sealed class G9CRowsAppliedEventArgs(G9CApplyReport report, G9ESyncPhase source) : EventArgs
{
    public G9CApplyReport Report { get; } = report;
    public G9ESyncPhase Source { get; } = source;
}

public sealed class G9CSyncRunCompletedEventArgs(G9CSyncRunResult result) : EventArgs
{
    public G9CSyncRunResult Result { get; } = result;
}

internal static class G9CSyncErrors
{
    public static G9CSyncException Normalize(Exception exception) => exception switch
    {
        G9CSyncException sync => sync,
        OperationCanceledException => new G9CSyncException(G9SyncErrorCodes.Canceled, "The sync was canceled.", exception),
        TimeoutException or IOException or HttpRequestException or WebSocketException or SocketException =>
            new G9CSyncException(G9SyncErrorCodes.NetworkFailure, exception.Message, exception),
        DbException => new G9CSyncException(G9SyncErrorCodes.LocalStoreError, exception.Message, exception),
        _ => new G9CSyncException(G9SyncErrorCodes.Unknown, exception.Message, exception)
    };

    public static bool IsTransient(Exception exception) => Normalize(exception).Code is G9SyncErrorCodes.NetworkFailure or G9SyncErrorCodes.ServerError;
}

internal sealed class G9CInlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
