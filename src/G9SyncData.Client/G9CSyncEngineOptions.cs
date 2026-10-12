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

    /// <summary>
    ///     Rows a reconciliation fetches before it applies them and lets them go (ADR-0066); rounded up to whole fetch requests. The
    ///     subscription's cursor still only moves when the whole reconciliation is done, so an interrupted one repeats and finds less to
    ///     do. Default 20,000.
    /// </summary>
    public int ReconcileApplyRows { get; set; } = 20_000;

    /// <summary>
    ///     A reconciliation that would have to fetch at least this many rows of ONE table stops fetching: it applies the memberships and
    ///     move-outs it found and clears the cursor, so the pull of the same run downloads the subscription as a staged, resumable
    ///     snapshot instead (ADR-0066). 0 = never. Default 100,000.
    /// </summary>
    public int ReconcileSnapshotThreshold { get; set; } = 100_000;

    /// <summary>
    ///     The largest decoded body the engine accepts from one server frame (ADR-0051, applied on the device by ADR-0066). A frame
    ///     declares how large it becomes before a byte is decompressed; a page of 2,000 rows is well under a megabyte. Default 64 MiB.
    /// </summary>
    public int MaxDecodedFrameBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>
    ///     The newest G9RowBatch body layout to ask the server for (ADR-0040). Default <see cref="G9SyncData.Protocol.G9SyncProtocol.MaxFrameVersion" />
    ///     (2, column-grouped: ~15% fewer bytes); 1 asks for row-wise frames. The device reads both either way.
    /// </summary>
    public int MaxFrameVersion { get; set; } = G9SyncData.Protocol.G9SyncProtocol.MaxFrameVersion;

    /// <summary>
    ///     Offer the server to send large first downloads as SQLite snapshot files (ADR-0072), when the store can take them
    ///     (<c>IG9SyncClientStore.DescribeSnapshotFiles</c>). Default true. A file the server cannot produce or the device rejects is
    ///     replaced by the same pull as rows, in the same run.
    /// </summary>
    public bool AcceptSnapshotFiles { get; set; } = true;

    /// <summary>
    ///     Fail a run when the server reports a client column whose declared type differs from the server's (ADR-0061). Default false:
    ///     a released build keeps syncing everything else and reports the columns in <c>G9CSyncRunResult.ModelProblems</c>. Turn it on in
    ///     debug and QA builds - the column syncs in neither direction, and the only place that is cheap to notice is before release.
    /// </summary>
    public bool FailOnTypeMismatch { get; set; }

    /// <summary>A staged pull older than this is discarded instead of resumed (its token would have expired).</summary>
    public TimeSpan StagedPullMaxAge { get; set; } = TimeSpan.FromMinutes(90);

    /// <summary>
    ///     Most payload bytes kept in stages OTHER than the one a run is about to resume (ADR-0060). A store keeps one stage per set of
    ///     subscriptions, so an interrupted download survives a sync of something else; this bounds what those waiting stages may occupy,
    ///     oldest going first. The stage being resumed is never counted and never removed for its size. Default 64 MiB.
    /// </summary>
    public long StagedPullsMaxBytes { get; set; } = 64L * 1024 * 1024;

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

public sealed class G9CRowsAppliedEventArgs(G9CApplyReport report, G9ESyncPhase source, G9CApplySlice? slice = null) : EventArgs
{
    public G9CApplyReport Report { get; } = report;
    public G9ESyncPhase Source { get; } = source;

    /// <summary>
    ///     Set when this came from a staged pull applied in slices (ADR-0053): the rows named in <see cref="Report" /> are committed
    ///     and readable now, and <see cref="G9CApplySlice.IsFinal" /> says whether the pull is finished. Null for a reconcile or a
    ///     push acknowledgement, which are single transactions.
    /// </summary>
    public G9CApplySlice? Slice { get; } = slice;
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
        // A store's own exception says which kind of failure it was; without this every SQLite error was G9S-UNKNOWN (ADR-0068).
        Storage.IG9SyncLocalStoreFailure local => new G9CSyncException(local.Failure switch
        {
            Storage.G9ELocalStoreFailure.Corrupt => G9SyncErrorCodes.LocalStoreCorrupt,
            Storage.G9ELocalStoreFailure.DiskFull => G9SyncErrorCodes.LocalStoreDiskFull,
            Storage.G9ELocalStoreFailure.Busy => G9SyncErrorCodes.LocalStoreBusy,
            _ => G9SyncErrorCodes.LocalStoreError
        }, exception.Message, exception),
        DbException => new G9CSyncException(G9SyncErrorCodes.LocalStoreError, exception.Message, exception),
        _ => new G9CSyncException(G9SyncErrorCodes.Unknown, exception.Message, exception)
    };

    public static bool IsTransient(Exception exception) =>
        Normalize(exception).Code is G9SyncErrorCodes.NetworkFailure or G9SyncErrorCodes.ServerError or G9SyncErrorCodes.ServerBusy;
}

internal sealed class G9CInlineProgress<T>(Action<T> handler) : IProgress<T>
{
    public void Report(T value) => handler(value);
}
