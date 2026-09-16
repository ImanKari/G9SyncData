using System.Threading.Channels;
using G9SyncData.Client.Progress;
using G9SyncData.Client.Storage;
using G9SyncData.Protocol;

namespace G9SyncData.Client.Runs;

/// <summary>Scheduling priority. A higher-priority request pre-empts a running background run.</summary>
public enum G9ESyncPriority : byte
{
    Background = 0,
    Foreground = 1,
    UserInteractive = 2
}

public enum G9ESyncOutcome : byte
{
    Succeeded = 0,

    /// <summary>Data moved, but some rows were rejected or some subscriptions reported problems.</summary>
    CompletedWithRejections = 1,

    Failed = 2,
    Canceled = 3
}

/// <summary>Context handed to an app-owned post step (e.g. a geodatabase reconcile after the site data applied).</summary>
public sealed class G9CSyncPostStepContext
{
    public required G9CSyncRun Run { get; init; }

    /// <summary>Everything this run changed so far.</summary>
    public required G9CApplyReport Changes { get; init; }

    /// <summary>Report 0..1 of the step; mapped into the step's slice of the bar.</summary>
    public required IProgress<double> Progress { get; init; }
}

/// <summary>An app-owned step that runs after the data applied and owns a slice of the progress bar.</summary>
/// <param name="name">Stable name (progress phase label and statistics key).</param>
/// <param name="weightHint">Approximate share of the whole run (0..0.9) until the device has measured history.</param>
/// <param name="action">The work.</param>
public sealed class G9CSyncPostStep(string name, double weightHint, Func<G9CSyncPostStepContext, CancellationToken, Task> action)
{
    public string Name { get; } = string.IsNullOrWhiteSpace(name) ? throw new ArgumentException("A post step needs a name.", nameof(name)) : name;
    public double WeightHint { get; } = Math.Clamp(weightHint, 0, 0.9);
    public Func<G9CSyncPostStepContext, CancellationToken, Task> Action { get; } = action ?? throw new ArgumentNullException(nameof(action));
}

public sealed class G9CSyncRequest
{
    /// <summary>Subscription keys or names to pull. Null means every local subscription (background runs skip heavy ones). Dependencies are added automatically.</summary>
    public IReadOnlyList<string>? Subscriptions { get; init; }

    public bool Push { get; init; } = true;
    public bool Pull { get; init; } = true;

    /// <summary>Reconcile the selected subscriptions even when nothing asks for it.</summary>
    public bool ForceReconcile { get; init; }

    public G9ESyncPriority Priority { get; init; } = G9ESyncPriority.Foreground;
    public IReadOnlyList<G9CSyncPostStep> PostSteps { get; init; } = [];
    public IProgress<G9DtSyncProgress>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }

    internal bool CanCoalesce => PostSteps.Count == 0 && Progress is null && !CancellationToken.CanBeCanceled;

    internal string Signature =>
        $"{Push}|{Pull}|{ForceReconcile}|{(Subscriptions is null ? "*" : string.Join(',', Subscriptions.Order(StringComparer.Ordinal)))}";
}

public sealed class G9CSyncRunResult
{
    public required Guid RunId { get; init; }
    public required G9ESyncOutcome Outcome { get; init; }
    public G9CSyncException? Error { get; init; }
    public int Pushed { get; init; }
    public int Rejected { get; init; }
    public int Conflicts { get; init; }
    public long Downloaded { get; init; }
    public long Applied { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<string> Subscriptions { get; init; } = [];

    /// <summary>Subscriptions that could not sync, with the server's reason.</summary>
    public IReadOnlyList<G9DtSubscriptionState> Problems { get; init; } = [];

    public G9CApplyReport Changes { get; init; } = new();

    public bool IsSuccess => Outcome is G9ESyncOutcome.Succeeded or G9ESyncOutcome.CompletedWithRejections;
}

/// <summary>A queued or running sync. Await <see cref="Completion" />; read <see cref="ReadProgressAsync" /> for progress.</summary>
public sealed class G9CSyncRun
{
    private readonly TaskCompletionSource<G9CSyncRunResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly Channel<G9DtSyncProgress> _progress = Channel.CreateBounded<G9DtSyncProgress>(
        new BoundedChannelOptions(64) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = false, SingleWriter = true });

    internal G9CSyncRun(G9CSyncRequest request)
    {
        Request = request;
        Cancellation = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken);
    }

    public Guid RunId { get; } = Guid.NewGuid();
    public G9CSyncRequest Request { get; }
    public G9DtSyncProgress? LatestProgress { get; private set; }
    public Task<G9CSyncRunResult> Completion => _completion.Task;
    public bool IsStarted { get; internal set; }

    internal CancellationTokenSource Cancellation { get; }
    internal bool Preempted { get; set; }

    public IAsyncEnumerable<G9DtSyncProgress> ReadProgressAsync(CancellationToken cancellationToken = default) =>
        _progress.Reader.ReadAllAsync(cancellationToken);

    /// <summary>Requests cancellation. Network phases stop at once; an apply in progress finishes atomically first.</summary>
    public void Cancel()
    {
        try
        {
            Cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // already completed
        }
    }

    internal void Publish(G9DtSyncProgress progress)
    {
        LatestProgress = progress;
        _progress.Writer.TryWrite(progress);
        try
        {
            Request.Progress?.Report(progress);
        }
        catch (Exception)
        {
            // A progress consumer must never break the sync.
        }
    }

    internal void Complete(G9CSyncRunResult result)
    {
        _progress.Writer.TryComplete();
        _completion.TrySetResult(result);
        Cancellation.Dispose();
    }
}

public sealed class G9CSubscriptionOptions
{
    /// <summary>Overrides the server definition's ephemeral flag.</summary>
    public bool? Ephemeral { get; init; }

    public TimeSpan? Ttl { get; init; }

    /// <summary>
    ///     The rows already exist locally (e.g. written by a previous sync system): reconcile instead of downloading a snapshot,
    ///     so only differences transfer. Call <c>AdoptExistingRowsAsync</c> first.
    /// </summary>
    public bool AdoptExistingRows { get; init; }
}
