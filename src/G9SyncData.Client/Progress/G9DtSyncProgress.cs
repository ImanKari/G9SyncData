namespace G9SyncData.Client.Progress;

/// <summary>Phases of a sync run, in execution order.</summary>
public enum G9ESyncPhase : byte
{
    Queued = 0,
    Connecting = 1,
    Pushing = 2,
    Reconciling = 3,
    Planning = 4,
    Downloading = 5,
    Applying = 6,
    PostStep = 7,
    Completed = 8,
    Canceled = 9,
    Failed = 10
}

/// <summary>
///     One progress sample. <see cref="Overall" /> is guaranteed monotonic within a run and reaches 1.0 only when the run
///     completed. The library emits numbers and phases; localising them is the app's job.
/// </summary>
/// <param name="RunId">The run.</param>
/// <param name="Phase">Current phase.</param>
/// <param name="Overall">0..1, monotonic.</param>
/// <param name="PhaseFraction">0..1 inside the phase.</param>
/// <param name="RowsDone">Rows pushed / downloaded / applied so far in the phase.</param>
/// <param name="RowsTotal">Rows expected in the phase (known before the data moves).</param>
/// <param name="BytesDone">Wire bytes so far (download).</param>
/// <param name="BytesTotal">Estimated wire bytes.</param>
/// <param name="SubscriptionKey">Subscription being worked on, when meaningful.</param>
/// <param name="Table">Table being worked on, when meaningful.</param>
/// <param name="RowsPerSecond">Current phase throughput.</param>
/// <param name="Eta">Estimated time to completion of the run.</param>
/// <param name="QueuedBehind">Runs waiting behind this one (the "queued" badge).</param>
/// <param name="PostStepName">Name of the running post-step.</param>
/// <param name="CancelPending">Cancel requested; the run is finishing a non-interruptible step.</param>
public sealed record G9DtSyncProgress(
    Guid RunId,
    G9ESyncPhase Phase,
    double Overall,
    double PhaseFraction,
    long RowsDone,
    long RowsTotal,
    long BytesDone,
    long BytesTotal,
    string? SubscriptionKey,
    string? Table,
    double RowsPerSecond,
    TimeSpan? Eta,
    int QueuedBehind,
    string? PostStepName,
    bool CancelPending);
