using G9SyncData.Client.Diagnostics;
using G9SyncData.Client.Model;
using G9SyncData.Client.Progress;
using G9SyncData.Client.Runs;
using G9SyncData.Client.Storage;
using G9SyncData.Client.Transport;
using G9SyncData.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace G9SyncData.Client;

/// <summary>
///     The client sync engine: one per local database. Owns a prioritised run queue, so status queries never wait for a
///     running sync (P-20) and background runs yield to user runs.
/// </summary>
public sealed partial class G9CSyncEngine : IAsyncDisposable
{
    private readonly IG9SyncClientStore _store;
    private readonly IG9SyncTransport _transport;
    private readonly G9CSyncClientModel _model;
    private readonly G9CSyncEngineOptions _options;
    private readonly ILogger _logger;
    private readonly IG9SyncHostHooks? _hooks;

    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly object _queueLock = new();
    private readonly PriorityQueue<G9CSyncRun, (int Priority, long Order)> _queue = new();
    private readonly SemaphoreSlim _signal = new(0);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _pokeLock = new();
    private readonly HashSet<string> _pokedTables = new(StringComparer.OrdinalIgnoreCase);

    private volatile bool _initialized;
    private Guid _clientId;
    private long _order;
    private int _queuedCount;
    private Task? _worker;
    private G9CSyncRun? _current;
    private int _suppressions;
    private CancellationTokenSource? _autoCancellation;
    private Timer? _pokeTimer;
    private int _disposed;

    /// <summary>The model problems last written to the log, so the warning is said once per distinct set and not once per run (ADR-0061).</summary>
    private string? _reportedModelProblems;

    public G9CSyncEngine(IG9SyncClientStore store, IG9SyncTransport transport, G9CSyncClientModel model, G9CSyncEngineOptions? options = null,
        ILogger<G9CSyncEngine>? logger = null, IG9SyncHostHooks? hooks = null)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _model = model ?? throw new ArgumentNullException(nameof(model));
        _options = options ?? new G9CSyncEngineOptions();
        _logger = (ILogger?)logger ?? NullLogger.Instance;
        _hooks = hooks;
        _transport.ChangesAvailable += OnChangesAvailable;
    }

    public G9CSyncClientModel Model => _model;

    public Guid ClientId => _initialized ? _clientId : throw new InvalidOperationException("Call InitializeAsync first.");

    public bool IsRunning => Volatile.Read(ref _current) is not null;

    /// <remarks>
    ///     Read without the queue lock: the progress tracker samples it while holding its own lock, and the queue lock must never be
    ///     taken after the tracker lock (preemption cancels a run, which reaches the tracker, so the order is queue → tracker).
    /// </remarks>
    public int QueuedRuns => Volatile.Read(ref _queuedCount);

    public bool IsBackgroundSuppressed => Volatile.Read(ref _suppressions) > 0;

    /// <summary>Raised after rows were written locally (download, reconciliation or canonical push results).</summary>
    public event EventHandler<G9CRowsAppliedEventArgs>? RowsApplied;

    public event EventHandler<G9CSyncRunCompletedEventArgs>? RunCompleted;

    /// <summary>Provisions local tables and capture, and starts the run worker. Idempotent.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            await _store.InitializeAsync(_model, cancellationToken).ConfigureAwait(false);
            _clientId = await _store.GetClientIdAsync(cancellationToken).ConfigureAwait(false);
            _worker = Task.Run(WorkerAsync);
            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ================================================================ subscriptions

    /// <summary>Adds (or returns) a subscription. Its rows arrive on the next run that includes it.</summary>
    public async Task<G9CSyncSubscriptionRecord> SubscribeAsync(string name, G9CSyncParameters? parameters = null, G9CSubscriptionOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var state = await _store.GetServerStateAsync(cancellationToken).ConfigureAwait(false);
        var definition = state.Definitions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal));
        var ephemeral = options?.Ephemeral ?? definition?.Ephemeral ?? false;
        var ttl = options?.Ttl ?? (definition is { Ephemeral: true, TtlSeconds: > 0 } ? TimeSpan.FromSeconds(definition.TtlSeconds) : null);
        var record = await _store.EnsureSubscriptionAsync(name, parameters ?? new G9CSyncParameters(), ephemeral, ttl, options?.AdoptExistingRows ?? false,
            cancellationToken).ConfigureAwait(false);
        await _store.UpdateSubscriptionAsync(record.Key, new G9CSubscriptionUpdate { Accessed = true }, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>Removes a subscription; rows no other subscription holds follow the server's move-out policy.</summary>
    public async Task UnsubscribeAsync(string name, G9CSyncParameters? parameters = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var state = await _store.GetServerStateAsync(cancellationToken).ConfigureAwait(false);
        var policy = state.Definitions.FirstOrDefault(d => string.Equals(d.Name, name, StringComparison.Ordinal))?.MoveOutPolicy ?? G9EMoveOutPolicy.Delete;
        await _store.RemoveSubscriptionAsync(G9CSyncParameters.BuildSubscriptionKey(name, parameters), policy, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<G9CSyncSubscriptionRecord>> GetSubscriptionsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
    }

    // ================================================================ runs

    /// <summary>Queues a run. Identical plain requests already waiting are coalesced into one run.</summary>
    public G9CSyncRun Run(G9CSyncRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!_initialized) throw new InvalidOperationException("Call InitializeAsync first.");
        G9CSyncRun run;
        G9CSyncRun? preempted = null;
        lock (_queueLock)
        {
            if (request.CanCoalesce)
            {
                foreach (var (queued, _) in _queue.UnorderedItems)
                {
                    if (!queued.IsStarted && queued.Request.CanCoalesce && queued.Request.Priority == request.Priority &&
                        string.Equals(queued.Request.Signature, request.Signature, StringComparison.Ordinal))
                    {
                        return queued;
                    }
                }
            }

            run = new G9CSyncRun(request);
            _queue.Enqueue(run, (-(int)request.Priority, _order++));
            Volatile.Write(ref _queuedCount, _queue.Count);
            var current = _current;
            if (current is not null && current.Request.Priority == G9ESyncPriority.Background && request.Priority > G9ESyncPriority.Background)
            {
                current.Preempted = true;
                preempted = current;
            }

            _signal.Release();
        }

        // Cancelling runs the token's callbacks synchronously (progress tracker, transport, store), so it happens outside the queue lock:
        // a callback that waits for a lock the run thread holds while it asks the engine for state would otherwise deadlock (LES-0036).
        if (preempted is not null)
        {
            preempted.Cancel();
            LogPreempted(_logger, preempted.RunId, run.RunId);
        }

        return run;
    }

    /// <summary>Queues a run and awaits its result.</summary>
    public Task<G9CSyncRunResult> SyncAsync(G9CSyncRequest request) => Run(request).Completion;

    /// <summary>While the returned scope is alive, background runs are skipped (e.g. on a latency-sensitive screen).</summary>
    public IDisposable SuppressBackground()
    {
        Interlocked.Increment(ref _suppressions);
        return new SuppressionScope(this);
    }

    /// <summary>Queues a background push+pull of every non-heavy subscription on a fixed interval.</summary>
    public void StartAutomaticSync(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(interval));
        StopAutomaticSync();
        var cancellation = new CancellationTokenSource();
        _autoCancellation = cancellation;
        _ = AutomaticLoopAsync(interval, cancellation.Token);
    }

    public void StopAutomaticSync()
    {
        var cancellation = Interlocked.Exchange(ref _autoCancellation, null);
        if (cancellation is null) return;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    // ================================================================ queries

    /// <summary>
    ///     Records a command (ADR-0025): the server executes the handler registered for <paramref name="name" /> on the next push,
    ///     once, in device order. <paramref name="applyLocally" /> writes the command's effects on this device immediately, in the same
    ///     transaction; those rows are not uploaded as row changes, are protected from pulls until the server's result arrives, and
    ///     are then replaced by the server's canonical state (also when the command is rejected).
    /// </summary>
    public async Task<G9CLocalCommand> ExecuteCommandAsync(string name, string payloadJson, Func<IG9LocalCommandWriter, CancellationToken, Task>? applyLocally = null,
        int version = 1, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(payloadJson);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.EnqueueCommandAsync(new G9CCommandRequest { Name = name, Version = version, Payload = payloadJson }, applyLocally, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>A typed <see cref="ExecuteCommandAsync(string, string, Func{IG9LocalCommandWriter, CancellationToken, Task}?, int, CancellationToken)" />.</summary>
    public Task<G9CLocalCommand> ExecuteCommandAsync<TCommand>(string name, TCommand payload, System.Text.Json.Serialization.Metadata.JsonTypeInfo<TCommand> typeInfo,
        Func<IG9LocalCommandWriter, CancellationToken, Task>? applyLocally = null, int version = 1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return ExecuteCommandAsync(name, System.Text.Json.JsonSerializer.Serialize(payload, typeInfo), applyLocally, version, cancellationToken);
    }

    /// <summary>
    ///     Frees space taken by an UPLOAD-ONLY table: removes rows that have been uploaded (all, or the given keys) without uploading a
    ///     delete. Rows with a pending change are kept. Throws for a table that is not upload-only — there a local delete is a change
    ///     the server must hear about.
    /// </summary>
    public async Task<int> ForgetUploadedRowsAsync(string localTable, IReadOnlyCollection<string>? keys = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(localTable);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ForgetUploadedRowsAsync(localTable, keys, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Recorded commands, newest first.</summary>
    public async Task<IReadOnlyList<G9CLocalCommand>> GetCommandsAsync(G9ELocalCommandState? state = null, int limit = 200, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetCommandsAsync(state, limit, cancellationToken).ConfigureAwait(false);
    }

    public async Task<G9CPendingSummary> GetPendingSummaryAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetPendingSummaryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<G9CSyncRejection>> GetRejectionsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetRejectionsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Makes rejected rows eligible for the next push (after the user fixed them, or to retry).</summary>
    /// <summary>Rows moved to the holding area when they stopped being visible (ADR-0026).</summary>
    public async Task<IReadOnlyList<G9CQuarantinedRow>> GetQuarantinedAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetQuarantinedAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Puts a quarantined row back as a pending local change (it uploads on the next sync; the server decides).</summary>
    public async Task<int> RestoreQuarantinedAsync(string localTable, string key, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.RestoreQuarantinedAsync(localTable, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Throws away a staged pull that has not been applied, so the next run plans afresh instead of resuming it. Returns whether
    ///     there was one.
    /// </summary>
    /// <remarks>
    ///     For an operator repairing a device that appears stuck. It cannot lose local work: a stage holds DOWNLOADED rows on their
    ///     way in, never anything the device wrote - pending changes live in the tables themselves and are untouched. The cost of
    ///     discarding is re-downloading, nothing more.
    /// </remarks>
    public async Task<bool> DiscardStagedPullAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);

        // Every stage, not the newest: the store keeps one per subscription set (ADR-0060), and an operator unsticking a device means all of them.
        return await _store.DiscardAllStagedPullsAsync(cancellationToken).ConfigureAwait(false) > 0;
    }

    public async Task<int> DiscardQuarantinedAsync(string? localTable = null, string? key = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.DiscardQuarantinedAsync(localTable, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rows kept in their tables after revocation, with the rule that kept them: "why is this row still here".</summary>
    public async Task<IReadOnlyList<G9CRetainedRow>> GetRetainedAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetRetainedAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> RetryRejectedAsync(string? localTable = null, string? key = null, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.RetryRejectedAsync(localTable, key, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Local change feed for one table (downloaded and local changes), ordered by local sequence.</summary>
    public async Task<IReadOnlyList<G9CChangeFeedEntry>> ReadChangeFeedAsync(string localTable, long afterSequence, int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ReadChangeFeedAsync(localTable, afterSequence, limit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     The change feed for one table narrowed to the keys <paramref name="keyFilter" /> selects (plus every deletion), so a
    ///     projection of one site does not walk the entries of every other one. See <see cref="G9CChangeFeedKeyFilter" />.
    /// </summary>
    public async Task<IReadOnlyList<G9CChangeFeedEntry>> ReadChangeFeedAsync(string localTable, long afterSequence, int limit,
        G9CChangeFeedKeyFilter keyFilter, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(keyFilter);
        keyFilter.Validate();
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.ReadChangeFeedAsync(localTable, afterSequence, limit, keyFilter, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The highest sequence <see cref="ReadChangeFeedAsync(string, long, int, CancellationToken)" /> can return today.</summary>
    /// <remarks>
    ///     Compare a stored feed watermark against this before resuming from it: a watermark above the current sequence belongs to a
    ///     store that no longer exists, and resuming from it would leave the projection permanently stale.
    /// </remarks>
    public async Task<long> GetChangeSequenceAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetChangeSequenceAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The highest sequence ever purged from the change feed; 0 when nothing was.</summary>
    /// <remarks>
    ///     The other half of the watermark check. <see cref="GetChangeSequenceAsync" /> catches a watermark that is too HIGH (the store
    ///     was rebuilt); this catches one that is too LOW: deletions between the watermark and the floor were purged and can no longer
    ///     be read, so a projection resuming from there would keep rows that are gone. Rebuild it instead (ADR-0059).
    /// </remarks>
    public async Task<long> GetChangeFeedFloorAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetChangeFeedFloorAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Registers local rows written before capture existed, so a subscription can adopt them without a download.</summary>
    public async Task<int> AdoptExistingRowsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.AdoptExistingRowsAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Flags rows as locally changed so the next push uploads them.</summary>
    public async Task<int> MarkDirtyAsync(string localTable, IReadOnlyCollection<string> keys, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        return await _store.MarkDirtyAsync(localTable, keys, cancellationToken).ConfigureAwait(false);
    }

    public async Task<G9CSyncHealth> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        var state = await _store.GetServerStateAsync(cancellationToken).ConfigureAwait(false);
        return new G9CSyncHealth
        {
            ClientId = _clientId,
            IsConnected = _transport.IsConnected,
            IsRunning = IsRunning,
            QueuedRuns = QueuedRuns,
            ServerEpoch = state.Epoch,
            LastHelloUtc = state.LastHelloUtc,
            Pending = await _store.GetPendingSummaryAsync(cancellationToken).ConfigureAwait(false),
            Subscriptions = await _store.GetSubscriptionsAsync(cancellationToken).ConfigureAwait(false),
            RecentRuns = await _store.GetRecentRunReportsAsync(10, cancellationToken).ConfigureAwait(false)
        };
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        StopAutomaticSync();
        _transport.ChangesAvailable -= OnChangesAvailable;
        await _lifetime.CancelAsync().ConfigureAwait(false);

        List<G9CSyncRun> abandoned = [];
        lock (_queueLock)
        {
            while (_queue.TryDequeue(out var run, out _)) abandoned.Add(run);
            Volatile.Write(ref _queuedCount, 0);
        }

        foreach (var run in abandoned)
        {
            run.Complete(new G9CSyncRunResult
            {
                RunId = run.RunId, Outcome = G9ESyncOutcome.Canceled, Error = new G9CSyncException(G9SyncErrorCodes.Canceled, "The sync engine was disposed.")
            });
        }

        _current?.Cancel();
        if (_worker is not null)
        {
            try
            {
                await _worker.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // shutting down
            }
        }

        lock (_pokeLock)
        {
            _pokeTimer?.Dispose();
            _pokeTimer = null;
        }

        _lifetime.Dispose();
        _signal.Dispose();
        _initLock.Dispose();
    }

    // ================================================================ internals

    private async Task WorkerAsync()
    {
        var token = _lifetime.Token;
        while (!token.IsCancellationRequested)
        {
            try
            {
                await _signal.WaitAsync(token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            G9CSyncRun? run;
            lock (_queueLock)
            {
                if (!_queue.TryDequeue(out run, out _)) continue;
                Volatile.Write(ref _queuedCount, _queue.Count);
                run.IsStarted = true;
                _current = run;
            }

            using (var activity = G9CSyncClientTelemetry.StartRun(run))
            {
                try
                {
                    await ExecuteRunAsync(run).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    LogUnexpected(_logger, exception, run.RunId);
                    run.Complete(new G9CSyncRunResult { RunId = run.RunId, Outcome = G9ESyncOutcome.Failed, Error = G9CSyncErrors.Normalize(exception) });
                }
                finally
                {
                    lock (_queueLock) _current = null;
                }

                G9CSyncClientTelemetry.RecordRun(run, await run.Completion.ConfigureAwait(false), activity);
            }

            // A pre-empted background run still owes its work: queue it again behind the run that pre-empted it.
            if (run.Preempted && run.Request.Priority == G9ESyncPriority.Background) TryQueueBackground(run.Request.Subscriptions);
        }
    }

    private async Task AutomaticLoopAsync(TimeSpan interval, CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                TryQueueBackground(null);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped
        }
    }

    private void TryQueueBackground(IReadOnlyList<string>? subscriptions)
    {
        if (!_initialized || IsBackgroundSuppressed || _hooks is { IsNetworkAvailable: false } || _lifetime.IsCancellationRequested) return;
        Run(new G9CSyncRequest { Priority = G9ESyncPriority.Background, Subscriptions = subscriptions });
    }

    private void OnChangesAvailable(G9DtChangesAvailable payload)
    {
        if (!_options.PullOnChangesAvailable || payload.Tables.Count == 0) return;
        lock (_pokeLock)
        {
            foreach (var table in payload.Tables) _pokedTables.Add(table);
            _pokeTimer ??= new Timer(_ => FlushPokes(), null, _options.ChangesDebounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushPokes()
    {
        string[] tables;
        lock (_pokeLock)
        {
            tables = _pokedTables.ToArray();
            _pokedTables.Clear();
            _pokeTimer?.Dispose();
            _pokeTimer = null;
        }

        if (tables.Length == 0 || !_initialized) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var token = _lifetime.Token;
                var state = await _store.GetServerStateAsync(token).ConfigureAwait(false);
                var names = state.Definitions
                    .Where(d => !d.Heavy && d.Tables.Any(t => tables.Contains(t, StringComparer.OrdinalIgnoreCase)))
                    .Select(d => d.Name)
                    .ToHashSet(StringComparer.Ordinal);
                if (names.Count == 0) return;
                var keys = (await _store.GetSubscriptionsAsync(token).ConfigureAwait(false))
                    .Where(s => names.Contains(s.Name)).Select(s => s.Key).ToList();
                if (keys.Count > 0) TryQueueBackground(keys);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                LogPokeFailed(_logger, exception);
            }
            catch (OperationCanceledException)
            {
                // disposed
            }
        });
    }

    private void RaiseRowsApplied(G9CApplyReport report, G9ESyncPhase source, G9CApplySlice? slice = null)
    {
        try
        {
            RowsApplied?.Invoke(this, new G9CRowsAppliedEventArgs(report, source, slice));
        }
        catch (Exception exception)
        {
            LogHandlerFailed(_logger, exception, nameof(RowsApplied));
        }
    }

    private sealed class SuppressionScope(G9CSyncEngine engine) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) Interlocked.Decrement(ref engine._suppressions);
        }
    }

    [LoggerMessage(EventId = 20001, Level = LogLevel.Information, Message = "Background run {RunId} pre-empted by run {NewRunId}")]
    private static partial void LogPreempted(ILogger logger, Guid runId, Guid newRunId);

    [LoggerMessage(EventId = 20002, Level = LogLevel.Error, Message = "Unexpected failure executing run {RunId}")]
    private static partial void LogUnexpected(ILogger logger, Exception exception, Guid runId);

    [LoggerMessage(EventId = 20003, Level = LogLevel.Warning, Message = "Handling a server change notification failed")]
    private static partial void LogPokeFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 20004, Level = LogLevel.Warning, Message = "A {Event} handler threw")]
    private static partial void LogHandlerFailed(ILogger logger, Exception exception, string @event);
}
