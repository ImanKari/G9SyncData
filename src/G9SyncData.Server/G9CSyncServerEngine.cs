using System.Diagnostics;
using System.Runtime.CompilerServices;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Diagnostics;
using G9SyncData.Server.Engine;
using G9SyncData.Server.Hosting;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
using G9SyncData.Server.SnapshotFiles;
using G9SyncData.Server.Storage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace G9SyncData.Server;

/// <summary>
///     The transport-neutral protocol engine. A transport (the SignalR hub, or the in-memory test transport) translates
///     its calls into these methods and nothing else; all protocol rules live here and in the store.
/// </summary>
public sealed partial class G9CSyncServerEngine
{
    private readonly G9CSyncServerModel _model;
    private readonly IG9SyncServerStore _store;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly G9CSyncServerOptions _options;
    private readonly IG9SyncAuthContextProvider _authProvider;
    private readonly IG9SyncConnectGate[] _gates;
    private readonly ILogger<G9CSyncServerEngine> _logger;
    private readonly G9CSyncTokenService _tokens;
    private readonly SemaphoreSlim? _pullSlots;
    private readonly SemaphoreSlim? _pushSlots;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private readonly IG9SnapshotFileService? _files;
    private volatile bool _initialized;

    public G9CSyncServerEngine(
        G9CSyncServerModel model,
        IG9SyncServerStore store,
        IServiceScopeFactory scopeFactory,
        IOptions<G9CSyncServerOptions> options,
        IG9SyncAuthContextProvider authProvider,
        IEnumerable<IG9SyncConnectGate> gates,
        ILogger<G9CSyncServerEngine> logger,
        IG9SnapshotFileService? snapshotFiles = null)
    {
        _files = snapshotFiles;
        ArgumentNullException.ThrowIfNull(options);
        _model = model;
        _store = store;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _authProvider = authProvider;
        _gates = gates.ToArray();
        _logger = logger;
        _tokens = new G9CSyncTokenService(_options.TokenSigningKey, _options.TokenLifetime, [.. _options.PreviousTokenSigningKeys], _options.RequireTokenSigningKey);
        _pullSlots = _options.MaxConcurrentPulls > 0 ? new SemaphoreSlim(_options.MaxConcurrentPulls, _options.MaxConcurrentPulls) : null;
        _pushSlots = _options.MaxConcurrentPushes > 0 ? new SemaphoreSlim(_options.MaxConcurrentPushes, _options.MaxConcurrentPushes) : null;
    }

    /// <summary>
    ///     Takes one of the engine's concurrency slots, or refuses with the retryable <c>G9S-SRV-BUSY</c> after
    ///     <see cref="G9CSyncServerOptions.AdmissionWait" /> (ADR-0062). The slot is released when the result is disposed.
    /// </summary>
    private async ValueTask<AdmissionSlot> AdmitAsync(SemaphoreSlim? slots, string operation, CancellationToken cancellationToken)
    {
        if (slots is null) return default;
        if (await slots.WaitAsync(_options.AdmissionWait, cancellationToken).ConfigureAwait(false)) return new AdmissionSlot(slots);

        G9CSyncServerTelemetry.Refused.Add(1, new KeyValuePair<string, object?>("operation", operation));
        LogBusy(_logger, operation);
        throw new G9CSyncException(G9SyncErrorCodes.ServerBusy, $"The server is at its limit of concurrent {operation} requests; try again shortly.");
    }

    private readonly struct AdmissionSlot(SemaphoreSlim? slots) : IDisposable
    {
        public void Dispose() => slots?.Release();
    }

    public G9CSyncServerModel Model => _model;

    public IG9SyncServerStore Store => _store;

    /// <summary>How many effects wait, how many failed for good, and how old the oldest waiting one is (ADR-0065).</summary>
    public async Task<G9CEffectQueueStatus> GetEffectQueueStatusAsync(CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetEffectQueueStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Effects that exhausted their attempts, oldest first, with the error of their last attempt.</summary>
    public async Task<IReadOnlyList<G9CPendingEffect>> GetFailedEffectsAsync(int max = 100, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        return await _store.GetFailedEffectsAsync(max, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    ///     Puts failed effects back in the queue with a fresh allowance of attempts; null requeues all of them (ADR-0065). Run it after
    ///     fixing the handler that failed them: an effect is work a COMMITTED push owes, and until it is requeued that work stays undone
    ///     however correct the handler has become. Handlers are idempotent by contract (ADR-0052), so requeueing one that had in fact
    ///     partly run is safe.
    /// </summary>
    public async Task<int> RequeueFailedEffectsAsync(IReadOnlyCollection<Guid>? effectIds = null, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var requeued = await _store.RequeueFailedEffectsAsync(effectIds, cancellationToken).ConfigureAwait(false);
        if (requeued > 0) LogEffectsRequeued(_logger, requeued);
        return requeued;
    }

    /// <summary>Initialises the store once. Safe to call concurrently.</summary>
    public async Task EnsureInitializedAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized) return;
        await _initLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized) return;
            var stopwatch = Stopwatch.StartNew();
            await _store.InitializeAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
            LogInitialized(_logger, _model.Tables.Count, _model.Subscriptions.Count, _store.Epoch, stopwatch.ElapsedMilliseconds);

            // Said at every start, because the consequence is invisible until it hurts (ADR-0064): with a random key every restart
            // and every deploy invalidates the pulls in flight, and a second instance rejects the first one's tokens.
            if (_tokens.IsEphemeralKey) LogEphemeralSigningKey(_logger);
            else LogConfiguredSigningKey(_logger, _options.PreviousTokenSigningKeys.Count);
        }
        finally
        {
            _initLock.Release();
        }
    }

    // ================================================================ Hello

    public async Task<G9DtHelloResult> HelloAsync(G9CSyncCaller caller, G9DtHelloRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        G9CSyncServerTelemetry.Hellos.Add(1);

        var result = new G9DtHelloResult
        {
            Epoch = _store.Epoch, ServerTimeUtc = DateTime.UtcNow, MaxPushRows = _options.MaxPushRows, MaxPushBytes = _options.MaxPushBytes
        };
        if (request.ProtocolVersion != G9SyncProtocol.Version)
        {
            result.Error = new G9DtSyncError
            {
                Code = G9SyncErrorCodes.ProtocolMismatch,
                Message = $"Server speaks protocol {G9SyncProtocol.Version}, client sent {request.ProtocolVersion}."
            };
            return result;
        }

        foreach (var gate in _gates)
        {
            var error = await gate.CheckAsync(caller, request, cancellationToken).ConfigureAwait(false);
            if (error is null) continue;
            result.Error = error;
            return result;
        }

        var auth = await _authProvider.ResolveAsync(caller, cancellationToken).ConfigureAwait(false);

        foreach (var clientTable in request.Tables)
        {
            var table = _model.FindTable(clientTable.Name);
            if (table is null
                || !string.Equals(table.KeyColumn, clientTable.KeyColumn, StringComparison.OrdinalIgnoreCase)
                || table.KeyType != clientTable.KeyType)
            {
                result.UnknownTables.Add(clientTable.Name);
                continue;
            }

            result.Tables.Add(Negotiate(table, clientTable));
        }

        if (request.ClientId != Guid.Empty)
        {
            await _store.TouchClientAsync(request.ClientId, auth.UserId, request.AppVersion, cancellationToken).ConfigureAwait(false);
        }

        result.Definitions = _model.Subscriptions.Where(s => auth.IsSubscriptionAllowed(s.Name)).Select(ToDto).ToList();
        foreach (var reference in request.Subscriptions)
        {
            result.Subscriptions.Add(await DecideAsync(reference, auth, cancellationToken).ConfigureAwait(false));
        }

        return result;
    }

    // ================================================================ PlanPull / Pull

    public async Task<G9DtPullPlan> PlanPullAsync(G9CSyncCaller caller, G9DtPullRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var activity = G9CSyncServerTelemetry.ActivitySource.StartActivity("G9Sync.PlanPull");
        activity?.SetTag("g9sync.client_id", request.ClientId.ToString());
        var auth = await _authProvider.ResolveAsync(caller, cancellationToken).ConfigureAwait(false);

        var plan = new G9DtPullPlan();
        var shapes = ResolveShapes(request.Tables, writableOnly: false);
        var snapshot = await _store.GetCurrentSnapshotAsync(cancellationToken).ConfigureAwait(false);
        var payload = new G9DtPullTokenPayload
        {
            ClientId = request.ClientId, UserId = auth.UserId, Epoch = _store.Epoch, NewSnapshot = snapshot,
            FrameVersion = G9SyncProtocol.NegotiateFrameVersion(request.FrameVersion)
        };
        var resolvedList = new List<G9CResolvedSubscription>();

        // A part the snapshot-file planner already estimated keeps its estimate here (null = estimate below), aligned with payload.Parts.
        var estimated = new List<G9DtPlannedPart?>();

        foreach (var reference in request.Subscriptions)
        {
            if (!G9CSubscriptionResolver.TryResolve(_model, reference, auth, out var resolved, out var failure))
            {
                plan.Skipped.Add(failure);
                continue;
            }

            string? previous = null;
            if (!string.IsNullOrEmpty(reference.Cursor))
            {
                var check = await CheckCursorAsync(resolved, reference.Cursor, cancellationToken).ConfigureAwait(false);
                if (check is not null)
                {
                    plan.Skipped.Add(State(resolved, check.Value.Action, check.Value.Message));
                    continue;
                }

                if (HashesDiffer(reference, resolved))
                {
                    plan.Skipped.Add(State(resolved, G9ESubscriptionAction.Reconcile, "Definition or authorisation changed; reconcile first."));
                    continue;
                }

                G9CSyncCursor.TryParse(reference.Cursor, out _, out previous);
            }

            var subscriptionIndex = payload.Subscriptions.Count;
            payload.Subscriptions.Add(new G9DtTokenSubscription
            {
                Name = resolved.Definition.Name,
                Parameters = new Dictionary<string, string>(resolved.Parameters.Values, StringComparer.Ordinal),
                AuthHash = resolved.AuthHash
            });
            resolvedList.Add(resolved);

            if (previous is null
                && await TryPlanSnapshotFileAsync(request, resolved, subscriptionIndex, shapes, payload, estimated, cancellationToken).ConfigureAwait(false))
            {
                plan.Cursors.Add(new G9DtSubscriptionCursor
                {
                    Key = resolved.Key,
                    Cursor = G9CSyncCursor.Format(_store.Epoch, snapshot),
                    DefinitionHash = resolved.Definition.DefinitionHash,
                    AuthHash = resolved.AuthHash
                });
                continue;
            }

            foreach (var include in resolved.Definition.Tables)
            {
                // An upload-only table's rows belong to the devices that write them: the server never sends them back.
                if (include.Table.Direction == G9ESyncDirection.UploadOnly) continue;
                if (!shapes.TryGetValue(_store.GetTableId(include.Table), out var shape)) continue;
                payload.Parts.Add(new G9DtTokenPart
                {
                    Subscription = subscriptionIndex,
                    Table = include.Table.LogicalName,
                    Kind = previous is null ? G9EPullPartKind.Snapshot : G9EPullPartKind.Delta,
                    PreviousSnapshot = previous,
                    Columns = shape.Columns.Select(c => c.Name).ToList()
                });
                estimated.Add(null);
            }

            plan.Cursors.Add(new G9DtSubscriptionCursor
            {
                Key = resolved.Key,
                Cursor = G9CSyncCursor.Format(_store.Epoch, snapshot),
                DefinitionHash = resolved.Definition.DefinitionHash,
                AuthHash = resolved.AuthHash
            });
        }

        foreach (var backfill in request.Backfills)
        {
            var table = _store.FindTable(backfill.TableId);
            if (table is null) continue;
            var columns = backfill.Columns.Select(table.FindColumn).OfType<G9CSyncServerColumn>().DistinctBy(c => c.Name).ToList();
            if (columns.Count == 0) continue;
            for (var i = 0; i < resolvedList.Count; i++)
            {
                if (resolvedList[i].Definition.FindTable(table) is null) continue;
                payload.Parts.Add(new G9DtTokenPart
                {
                    Subscription = i, Table = table.LogicalName, Kind = G9EPullPartKind.Backfill, Columns = columns.Select(c => c.Name).ToList()
                });
                estimated.Add(null);
            }
        }

        for (var index = 0; index < payload.Parts.Count; index++)
        {
            var planned = estimated[index];
            if (planned is null)
            {
                var part = payload.Parts[index];
                var partRequest = BuildPartRequest(part, payload, resolvedList[part.Subscription], afterKey: null);
                var estimate = await _store.EstimatePartAsync(partRequest, cancellationToken).ConfigureAwait(false);
                planned = new G9DtPlannedPart
                {
                    SubscriptionKey = resolvedList[part.Subscription].Key,
                    TableId = _store.GetTableId(partRequest.Table),
                    Kind = part.Kind,
                    Rows = estimate.Rows,
                    EstimatedBytes = estimate.EstimatedBytes
                };
            }

            plan.Parts.Add(planned);
            plan.TotalRows += planned.Rows;
            plan.EstimatedBytes += planned.EstimatedBytes;
        }

        plan.Token = _tokens.Create(payload);
        G9CSyncServerTelemetry.PullPlans.Add(1);
        G9CSyncServerTelemetry.PlannedRows.Add(plan.TotalRows);
        activity?.SetTag("g9sync.rows", plan.TotalRows);
        LogPlanned(_logger, request.ClientId, plan.Parts.Count, plan.TotalRows, plan.Skipped.Count);
        return plan;
    }

    public async IAsyncEnumerable<G9DtSyncChunk> PullAsync(G9CSyncCaller caller, string token, G9DtPullContinuation? continuation,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var payload = _tokens.Read(token);
        if (payload.Epoch != _store.Epoch) throw new G9CSyncException(G9SyncErrorCodes.ResyncEpoch, "The server epoch changed since the pull was planned.");
        if (continuation is null && await _store.IsSnapshotAheadAsync(payload.NewSnapshot, cancellationToken).ConfigureAwait(false))
        {
            // Checked once per pull, not per resumed page: the database cannot go back in time between two pages of one stream
            // without also losing the connection, and the resumed call re-reads the token anyway.
            throw new G9CSyncException(G9SyncErrorCodes.ResyncEpoch, "The pull was planned against a newer state of the database (restore or failover).");
        }

        var auth = await _authProvider.ResolveAsync(caller, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(payload.UserId, auth.UserId, StringComparison.Ordinal))
        {
            throw new G9CSyncException(G9SyncErrorCodes.TokenInvalid, "The pull token was issued to a different user.");
        }

        var resolved = new List<G9CResolvedSubscription>(payload.Subscriptions.Count);
        foreach (var subscription in payload.Subscriptions)
        {
            var reference = new G9DtSubscriptionRef { Name = subscription.Name, Parameters = subscription.Parameters };
            if (!G9CSubscriptionResolver.TryResolve(_model, reference, auth, out var item, out var failure))
            {
                throw new G9CSyncException(G9CSubscriptionResolver.FailureCode(failure), failure.Message ?? "Subscription is no longer valid.");
            }

            if (!string.Equals(item.AuthHash, subscription.AuthHash, StringComparison.Ordinal))
            {
                throw new G9CSyncException(G9SyncErrorCodes.ResyncAuthChanged, $"Authorisation for '{item.Key}' changed since the pull was planned.");
            }

            resolved.Add(item);
        }

        // Held for the whole stream: a slot is a database connection and a page of rows in memory (ADR-0062). Taken after the token
        // and the caller were checked, so a request that was never going to be served does not occupy one.
        using var slot = await AdmitAsync(_pullSlots, "pull", cancellationToken).ConfigureAwait(false);

        var sequence = continuation?.NextSequence ?? 0;
        var after = continuation?.AfterKey;
        for (var index = Math.Max(0, continuation?.PartIndex ?? 0); index < payload.Parts.Count; index++)
        {
            var part = payload.Parts[index];
            if (part.Kind == G9EPullPartKind.SnapshotFile)
            {
                var resume = after;
                after = null;
                await foreach (var chunk in StreamSnapshotFileAsync(part, index, resolved[part.Subscription], payload, resume, sequence, cancellationToken).ConfigureAwait(false))
                {
                    sequence = chunk.Sequence + 1;
                    yield return chunk;
                }

                continue;
            }

            var partRequest = BuildPartRequest(part, payload, resolved[part.Subscription], after);
            after = null;
            await foreach (var page in _store.ReadPartAsync(partRequest, cancellationToken).ConfigureAwait(false))
            {
                G9CSyncServerTelemetry.PulledRows.Add(page.RowCount);
                yield return new G9DtSyncChunk
                {
                    Sequence = sequence,
                    Payload = page.Frame,
                    RowCount = page.RowCount,
                    SubscriptionKey = resolved[part.Subscription].Key,
                    Continuation = new G9DtPullContinuation
                    {
                        PartIndex = page.IsLast ? index + 1 : index,
                        AfterKey = page.IsLast ? null : page.LastKey,
                        NextSequence = sequence + 1
                    }
                };
                sequence++;
            }
        }

        yield return new G9DtSyncChunk
        {
            Sequence = sequence,
            IsFinal = true,
            Continuation = new G9DtPullContinuation { PartIndex = payload.Parts.Count, NextSequence = sequence + 1 }
        };
    }

    // ================================================================ Push

    public async Task<G9DtPushResult> PushAsync(G9CSyncCaller caller, G9DtPushRequest request, IAsyncEnumerable<G9DtSyncChunk> chunks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(chunks);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        using var activity = G9CSyncServerTelemetry.ActivitySource.StartActivity("G9Sync.Push");
        activity?.SetTag("g9sync.client_id", request.ClientId.ToString());
        activity?.SetTag("g9sync.batch_id", request.BatchId);
        var stopwatch = Stopwatch.StartNew();

        // Who is calling, and whether there is room, BEFORE a byte of the upload is held (ADR-0062). This used to drain the whole
        // stream first - up to MaxPushBytes, 128 MiB, per caller - and only then ask who the caller was: memory committed to a
        // request the server might refuse, with nothing limiting how many did it at once.
        if (request.ClientId == Guid.Empty) throw new G9CSyncException(G9SyncErrorCodes.InvalidParameters, "A push requires a client id.");
        var auth = await _authProvider.ResolveAsync(caller, cancellationToken).ConfigureAwait(false);
        using var slot = await AdmitAsync(_pushSlots, "push", cancellationToken).ConfigureAwait(false);

        var frames = new List<byte[]>();
        long bytes = 0;
        await foreach (var chunk in chunks.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            if (chunk.Payload is not { Length: > 0 } frame) continue;
            bytes += frame.Length;
            if (bytes > _options.MaxPushBytes)
            {
                throw new G9CSyncException(G9SyncErrorCodes.RejectTooMany, $"The push exceeds {_options.MaxPushBytes} bytes; send smaller batches.");
            }

            frames.Add(frame);
        }

        var subscriptions = new List<G9CResolvedSubscription>();
        foreach (var reference in request.Subscriptions)
        {
            if (G9CSubscriptionResolver.TryResolve(_model, reference, auth, out var resolved, out _)) subscriptions.Add(resolved);
        }

        var shapes = ResolveShapes(request.Tables, writableOnly: true);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var pipeline = new G9CSyncPipeline
        {
            Validators = services.GetServices<IG9UploadValidator>().ToArray(),
            Transformers = services.GetServices<IG9UploadTransformer>().ToArray(),
            DeleteHandlers = services.GetServices<IG9DeleteHandler>().ToArray(),
            ConflictResolvers = services.GetServices<IG9ConflictResolver>().ToArray(),
            PostCommitHandlers = services.GetServices<IG9PostCommitHandler>().ToArray(),
            EffectHandlers = services.GetServices<IG9EffectHandler>().ToArray(),
            RowWriters = services.GetServices<IG9RowWriter>().ToArray(),
            CommandHandlers = services.GetServices<IG9CommandHandler>().ToArray()
        };

        var result = await _store.ApplyPushAsync(new G9CStorePushRequest
        {
            ClientId = request.ClientId,
            BatchId = request.BatchId,
            Caller = caller,
            Auth = auth,
            Frames = frames,
            Tables = shapes,
            Commands = request.Commands.OrderBy(c => c.Sequence).ToList(),
            HeldTables = request.HeldTables.Count == 0 ? new Dictionary<int, G9CPushTableShape>() : ResolveShapes(request.HeldTables, writableOnly: false),
            Subscriptions = subscriptions,
            Pipeline = pipeline,
            Services = services,
            MaxRejections = _options.MaxRejectionsPerPush,
            MaxRows = _options.MaxPushRows,
            MaxDecodedFrameBytes = _options.MaxDecodedFrameBytes,
            MaxDecodedPushBytes = _options.MaxDecodedPushBytes,
            FrameVersion = G9SyncProtocol.NegotiateFrameVersion(request.FrameVersion)
        }, cancellationToken).ConfigureAwait(false);

        if (!result.AlreadyApplied && result.Error is null && pipeline.PostCommitHandlers.Count > 0)
        {
            await RunPostCommitHandlersAsync(caller, auth, request, result, pipeline, services).ConfigureAwait(false);
        }

        // The effects are already durable; this only saves them waiting for the next poll (ADR-0052).
        if (!result.AlreadyApplied && result.Error is null && pipeline.EffectHandlers.Count > 0 && result.Accepted.Count > 0)
        {
            services.GetService<G9CSyncEffectDispatcher>()?.Nudge();
        }

        var accepted = result.Accepted.Sum(a => a.Keys.Count);
        RecordPush(result, accepted, stopwatch.Elapsed.TotalMilliseconds, activity);
        LogPushed(_logger, request.ClientId, request.BatchId, accepted, result.Rejections.Count,
            result.Conflicts.Count, result.AlreadyApplied, stopwatch.ElapsedMilliseconds);
        return result;
    }

    private static void RecordPush(G9DtPushResult result, int accepted, double elapsedMs, Activity? activity)
    {
        G9CSyncServerTelemetry.Pushes.Add(1, new KeyValuePair<string, object?>("already_applied", result.AlreadyApplied));
        if (accepted > 0) G9CSyncServerTelemetry.PushRows.Add(accepted, new KeyValuePair<string, object?>("result", "accepted"));
        if (result.Rejections.Count > 0) G9CSyncServerTelemetry.PushRows.Add(result.Rejections.Count, new KeyValuePair<string, object?>("result", "rejected"));
        if (result.Conflicts.Count > 0) G9CSyncServerTelemetry.PushRows.Add(result.Conflicts.Count, new KeyValuePair<string, object?>("result", "conflict"));
        G9CSyncServerTelemetry.PushDuration.Record(elapsedMs);
        if (activity is null) return;
        activity.SetTag("g9sync.accepted", accepted);
        activity.SetTag("g9sync.rejected", result.Rejections.Count);
        activity.SetTag("g9sync.conflicts", result.Conflicts.Count);
        activity.SetTag("g9sync.already_applied", result.AlreadyApplied);
    }

    // ================================================================ Reconcile / FetchRows

    public async IAsyncEnumerable<G9DtSyncChunk> ReconcileAsync(G9CSyncCaller caller, G9DtReconcileRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var auth = await _authProvider.ResolveAsync(caller, cancellationToken).ConfigureAwait(false);
        if (!G9CSubscriptionResolver.TryResolve(_model, request.Subscription, auth, out var resolved, out var failure))
        {
            throw new G9CSyncException(G9CSubscriptionResolver.FailureCode(failure), failure.Message ?? "Invalid subscription.");
        }

        using var slot = await AdmitAsync(_pullSlots, "reconcile", cancellationToken).ConfigureAwait(false);
        var tables = new List<G9CStoreReconcileTable>();
        foreach (var digest in request.Tables)
        {
            var table = _store.FindTable(digest.TableId);
            var include = table is null ? null : resolved.Definition.FindTable(table);
            if (table is null || include is null || table.Direction == G9ESyncDirection.UploadOnly) continue;
            if (digest.Digests is not { Length: G9CSyncDigest.BucketCount } || digest.Counts is not { Length: G9CSyncDigest.BucketCount })
            {
                throw new G9CSyncException(G9SyncErrorCodes.InvalidParameters, $"Digest for table {digest.TableId} must have {G9CSyncDigest.BucketCount} buckets.");
            }

            var computed = digest.ComputedColumns.Select(table.FindColumn).OfType<G9CSyncServerColumn>().Where(c => c.IsComputed).ToArray();
            tables.Add(new G9CStoreReconcileTable(table, include, digest, computed));
        }

        var sequence = 0;
        var mismatchedBuckets = 0;
        await foreach (var item in _store.ReconcileAsync(new G9CStoreReconcileRequest
                           {
                               Subscription = resolved, Tables = tables, PageRows = Math.Max(_options.PageRows, 5000),
                               FrameVersion = G9SyncProtocol.NegotiateFrameVersion(request.FrameVersion)
                           }, cancellationToken).ConfigureAwait(false))
        {
            if (!item.IsFinal)
            {
                yield return new G9DtSyncChunk { Sequence = sequence++, Payload = item.Frame, RowCount = item.RowCount, SubscriptionKey = resolved.Key };
                continue;
            }

            mismatchedBuckets = item.Mismatched.Sum(m => m.Buckets.Count);
            G9CSyncServerTelemetry.Reconciles.Add(1);
            G9CSyncServerTelemetry.MismatchedBuckets.Add(mismatchedBuckets);
            yield return new G9DtSyncChunk
            {
                Sequence = sequence++,
                IsFinal = true,
                SubscriptionKey = resolved.Key,
                ReconcileSummary = new G9DtReconcileSummary
                {
                    Cursor = G9CSyncCursor.Format(_store.Epoch, item.Snapshot ?? throw new InvalidOperationException("Store returned no snapshot.")),
                    DefinitionHash = resolved.Definition.DefinitionHash,
                    AuthHash = resolved.AuthHash,
                    Mismatched = item.Mismatched.ToList()
                }
            };
        }

        LogReconciled(_logger, resolved.Key, tables.Count, mismatchedBuckets);
    }

    public async IAsyncEnumerable<G9DtSyncChunk> FetchRowsAsync(G9CSyncCaller caller, G9DtFetchRowsRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(request);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);
        var auth = await _authProvider.ResolveAsync(caller, cancellationToken).ConfigureAwait(false);
        if (!G9CSubscriptionResolver.TryResolve(_model, request.Subscription, auth, out var resolved, out var failure))
        {
            throw new G9CSyncException(G9CSubscriptionResolver.FailureCode(failure), failure.Message ?? "Invalid subscription.");
        }

        var table = _store.FindTable(request.TableId)
                    ?? throw new G9CSyncException(G9SyncErrorCodes.UnknownTable, $"Unknown table id {request.TableId}.");
        var include = resolved.Definition.FindTable(table)
                      ?? throw new G9CSyncException(G9SyncErrorCodes.InvalidParameters, $"'{table.LogicalName}' is not part of '{resolved.Definition.Name}'.");
        if (table.Direction == G9ESyncDirection.UploadOnly)
        {
            throw new G9CSyncException(G9SyncErrorCodes.RejectDirection, $"'{table.LogicalName}' is upload-only: its rows are never sent to devices.");
        }
        if (request.Keys.Count > _options.MaxFetchKeys)
        {
            throw new G9CSyncException(G9SyncErrorCodes.InvalidParameters, $"At most {_options.MaxFetchKeys} keys per fetch.");
        }

        using var slot = await AdmitAsync(_pullSlots, "fetch", cancellationToken).ConfigureAwait(false);
        var keys = new List<string>(request.Keys.Count);
        foreach (var key in request.Keys)
        {
            if (G9CSyncKey.TryNormalize(table.KeyType, key, out var canonical)) keys.Add(canonical);
        }

        var columns = request.Columns.Select(table.FindColumn).OfType<G9CSyncServerColumn>().DistinctBy(c => c.Name).ToArray();
        var sequence = 0;
        if (keys.Count > 0)
        {
            await foreach (var page in _store.FetchRowsAsync(new G9CStoreFetchRequest
                               {
                                   Table = table, Include = include, Columns = columns, Subscription = resolved, Keys = keys, PageRows = _options.PageRows, MaxPageBytes = _options.MaxPageBytes,
                                   FrameVersion = G9SyncProtocol.NegotiateFrameVersion(request.FrameVersion)
                               }, cancellationToken).ConfigureAwait(false))
            {
                yield return new G9DtSyncChunk { Sequence = sequence++, Payload = page.Frame, RowCount = page.RowCount, SubscriptionKey = resolved.Key };
            }
        }

        yield return new G9DtSyncChunk { Sequence = sequence, IsFinal = true, SubscriptionKey = resolved.Key };
    }

    // ================================================================ snapshot files (ADR-0072)

    /// <summary>
    ///     Plans a subscription's first download as a snapshot file when the device offered one, the subscription allows it and the snapshot
    ///     is large enough - reusing a file already built for the same identity when the rows changed since are few, and otherwise asking
    ///     for one at this plan's snapshot. Adds the token parts (file + the deltas that bring a reused file up to date) with their estimates
    ///     and returns true; or, when a file is not worth it after all, adds the ordinary snapshot parts it already estimated and returns
    ///     true too. False = nothing was added; plan the subscription as rows.
    /// </summary>
    private async Task<bool> TryPlanSnapshotFileAsync(G9DtPullRequest request, G9CResolvedSubscription resolved, int subscriptionIndex,
        Dictionary<int, G9CPushTableShape> shapes, G9DtPullTokenPayload payload, List<G9DtPlannedPart?> estimated, CancellationToken cancellationToken)
    {
        if (_files is null || request.SnapshotFiles is not { } offer || offer.FormatVersion < 1) return false;
        if (resolved.Definition.Ephemeral || resolved.Definition.SnapshotFilePolicy == G9ESnapshotFilePolicy.Never) return false;
        var maxFiles = offer.MaxFiles > 0 ? offer.MaxFiles : 8;
        if (payload.Parts.Count(p => p.Kind == G9EPullPartKind.SnapshotFile) >= maxFiles) return false;

        var tables = new List<(G9CSyncSubscriptionTable Include, int TableId, G9CPushTableShape Columns, G9DtSnapshotFileTable Shape)>();
        foreach (var include in resolved.Definition.Tables)
        {
            if (include.Table.Direction == G9ESyncDirection.UploadOnly) continue;
            var tableId = _store.GetTableId(include.Table);
            if (!shapes.TryGetValue(tableId, out var columns)) continue;

            // A table the device cannot describe cannot go in a file it will read: rows, for the whole subscription.
            var shape = offer.Tables.FirstOrDefault(t => t.TableId == tableId);
            if (shape is null) return false;
            tables.Add((include, tableId, columns, shape));
        }

        if (tables.Count == 0) return false;
        var policy = _files.Policy;
        var minRows = Math.Max(policy.MinRows, offer.MinRows);
        var identity = SnapshotFileIdentity(resolved, offer.Storage, tables.Select(t => (t.Include.Table.LogicalName, t.Columns, t.Shape)));

        // A file already built (or being built) for exactly these rows, at a snapshot the deltas can still reach.
        var candidate = _files.FindLatest(identity);
        if (candidate is not null && candidate.Epoch == _store.Epoch && DateTime.UtcNow - candidate.SnapshotUtc <= policy.MaxAge
            && (offer.MaxBytes <= 0 || candidate.RawBytes <= offer.MaxBytes)
            && !await _store.IsSnapshotAheadAsync(candidate.Snapshot, cancellationToken).ConfigureAwait(false)
            && !await _store.IsBeyondTombstoneHorizonAsync(candidate.Snapshot, tables.Select(t => t.Include.Table).ToArray(), cancellationToken).ConfigureAwait(false))
        {
            var deltas = new List<(G9DtTokenPart Part, G9DtPlannedPart Planned)>();
            long deltaRows = 0;
            foreach (var table in tables)
            {
                var part = new G9DtTokenPart
                {
                    Subscription = subscriptionIndex, Table = table.Include.Table.LogicalName, Kind = G9EPullPartKind.Delta,
                    PreviousSnapshot = candidate.Snapshot, Columns = table.Columns.Columns.Select(c => c.Name).ToList()
                };
                var estimate = await _store.EstimatePartAsync(BuildPartRequest(part, payload, resolved, afterKey: null), cancellationToken).ConfigureAwait(false);
                deltas.Add((part, new G9DtPlannedPart
                {
                    SubscriptionKey = resolved.Key, TableId = table.TableId, Kind = G9EPullPartKind.Delta, Rows = estimate.Rows, EstimatedBytes = estimate.EstimatedBytes
                }));
                deltaRows += estimate.Rows;
            }

            if (!candidate.Ready || deltaRows <= policy.MaxDeltaRatio * Math.Max(candidate.Rows, minRows))
            {
                AddFilePart(candidate.FileId, candidate.Snapshot, candidate.SnapshotUtc, candidate.Ready ? candidate.RowsByTable : null,
                    candidate.Ready ? candidate.CompressedBytes : 0, null);
                foreach (var (part, planned) in deltas)
                {
                    payload.Parts.Add(part);
                    estimated.Add(planned);
                }

                G9CSyncServerTelemetry.SnapshotFilesPlanned.Add(1, new KeyValuePair<string, object?>("source", candidate.Ready ? "reused" : "building"));
                LogSnapshotFileReused(_logger, resolved.Key, candidate.FileId, deltaRows);
                return true;
            }
        }

        // No usable file: what would the snapshot be as rows?
        var snapshotParts = new List<(G9DtTokenPart Part, G9DtPlannedPart Planned)>();
        long rows = 0;
        foreach (var table in tables)
        {
            var part = new G9DtTokenPart
            {
                Subscription = subscriptionIndex, Table = table.Include.Table.LogicalName, Kind = G9EPullPartKind.Snapshot,
                Columns = table.Columns.Columns.Select(c => c.Name).ToList()
            };
            var estimate = await _store.EstimatePartAsync(BuildPartRequest(part, payload, resolved, afterKey: null), cancellationToken).ConfigureAwait(false);
            snapshotParts.Add((part, new G9DtPlannedPart
            {
                SubscriptionKey = resolved.Key, TableId = table.TableId, Kind = G9EPullPartKind.Snapshot, Rows = estimate.Rows, EstimatedBytes = estimate.EstimatedBytes
            }));
            rows += estimate.Rows;
        }

        var snapshotUtc = DateTime.UtcNow;
        var fileId = SnapshotFileId(identity, payload.NewSnapshot);
        if (rows >= minRows && _files.Prepare(BuildSnapshotFile(fileId, identity, payload.NewSnapshot, snapshotUtc, offer.Storage, resolved,
                tables.Select(t => new G9DtTokenFileTable
                {
                    Table = t.Include.Table.LogicalName, Columns = t.Columns.Columns.Select(c => c.Name).ToList(), Shape = t.Shape
                }).ToList(), payload.FrameVersion)))
        {
            AddFilePart(fileId, payload.NewSnapshot, snapshotUtc, snapshotParts.ToDictionary(p => p.Planned.TableId, p => p.Planned.Rows),
                snapshotParts.Sum(p => p.Planned.EstimatedBytes), snapshotParts);
            G9CSyncServerTelemetry.SnapshotFilesPlanned.Add(1, new KeyValuePair<string, object?>("source", "built"));
            LogSnapshotFilePlanned(_logger, resolved.Key, fileId, rows);
            return true;
        }

        // Rows after all - with the estimates just made, so nothing is estimated twice.
        foreach (var (part, planned) in snapshotParts)
        {
            payload.Parts.Add(part);
            estimated.Add(planned);
        }

        return true;

        void AddFilePart(string id, string fileSnapshot, DateTime fileSnapshotUtc, IReadOnlyDictionary<int, long>? rowsByTable, long bytes,
            List<(G9DtTokenPart Part, G9DtPlannedPart Planned)>? estimates)
        {
            var fileTables = tables.Select(t => new G9DtTokenFileTable
            {
                Table = t.Include.Table.LogicalName, Columns = t.Columns.Columns.Select(c => c.Name).ToList(), Shape = t.Shape
            }).ToList();
            payload.Parts.Add(new G9DtTokenPart
            {
                Subscription = subscriptionIndex,
                Kind = G9EPullPartKind.SnapshotFile,
                File = new G9DtTokenFile
                {
                    FileId = id, Identity = identity, Snapshot = fileSnapshot, SnapshotUtc = fileSnapshotUtc, Storage = offer.Storage, Tables = fileTables
                }
            });

            // A file still being built has no exact counts yet: its upper bound is the snapshot estimate, made now if it was not.
            var plannedTables = tables.Select(t => new G9DtPlannedFileTable
            {
                TableId = t.TableId,
                Rows = rowsByTable?.GetValueOrDefault(t.TableId) ?? estimates?.FirstOrDefault(e => e.Planned.TableId == t.TableId).Planned?.Rows ?? 0
            }).ToList();
            estimated.Add(new G9DtPlannedPart
            {
                SubscriptionKey = resolved.Key,
                TableId = 0,
                Kind = G9EPullPartKind.SnapshotFile,
                Rows = plannedTables.Sum(t => t.Rows),
                EstimatedBytes = bytes,
                File = new G9DtPlannedFile { FileId = id, SnapshotUtc = fileSnapshotUtc, Tables = plannedTables }
            });
        }
    }

    /// <summary>Streams a file part's segments as chunks; the continuation of each names the file's hash and the next segment.</summary>
    private async IAsyncEnumerable<G9DtSyncChunk> StreamSnapshotFileAsync(G9DtTokenPart part, int partIndex, G9CResolvedSubscription subscription,
        G9DtPullTokenPayload payload, string? resume, int sequence, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_files is null)
        {
            throw new G9CSyncException(G9SyncErrorCodes.SnapshotFileUnavailable,
                "This server instance has no snapshot files (UseSnapshotFiles) and cannot serve a pull planned with one.") { Retryable = false };
        }

        var file = part.File ?? throw new G9CSyncException(G9SyncErrorCodes.TokenInvalid, "A snapshot-file part without its file.");
        var build = BuildSnapshotFile(file.FileId, file.Identity, file.Snapshot, file.SnapshotUtc, file.Storage, subscription, file.Tables, payload.FrameVersion);
        ParseFileResume(resume, out var fromSegment, out var expectedSha);

        var first = true;
        await foreach (var segment in _files.ReadAsync(build, fromSegment, expectedSha, cancellationToken).ConfigureAwait(false))
        {
            var last = segment.Segment.Index == segment.Manifest.Segments - 1;
            var info = new G9DtFileSegment
            {
                FileId = segment.Segment.FileId, Index = segment.Segment.Index, Offset = segment.Segment.Offset, Length = segment.Segment.Length,
                Sha256 = segment.Segment.Sha256, Manifest = first ? segment.Manifest : null
            };
            first = false;
            G9CSyncServerTelemetry.SnapshotFileBytes.Add(segment.Compressed.Length);
            G9CSyncServerTelemetry.PulledRows.Add(segment.Rows);
            yield return new G9DtSyncChunk
            {
                Sequence = sequence,
                Payload = segment.Compressed,
                RowCount = segment.Rows,
                SubscriptionKey = subscription.Key,
                File = info,
                Continuation = new G9DtPullContinuation
                {
                    PartIndex = last ? partIndex + 1 : partIndex,
                    AfterKey = last ? null : FileResumePrefix + segment.Manifest.Sha256 + ":" + (segment.Segment.Index + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    NextSequence = sequence + 1
                }
            };
            sequence++;
        }
    }

    private const string FileResumePrefix = "seg:";

    private static void ParseFileResume(string? resume, out int fromSegment, out string? sha)
    {
        fromSegment = 0;
        sha = null;
        if (resume is null || !resume.StartsWith(FileResumePrefix, StringComparison.Ordinal)) return;
        var colon = resume.LastIndexOf(':');
        if (colon <= FileResumePrefix.Length
            || !int.TryParse(resume.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var next))
        {
            return;
        }

        fromSegment = next;
        sha = resume[FileResumePrefix.Length..colon];
    }

    /// <summary>The build of a file: the device's shapes and storage, and each table's snapshot rows read at the file's snapshot.</summary>
    private G9CSnapshotFileBuild BuildSnapshotFile(string fileId, string identity, string snapshot, DateTime snapshotUtc, G9DtSnapshotFileStorage storage,
        G9CResolvedSubscription subscription, IReadOnlyList<G9DtTokenFileTable> tables, int frameVersion)
    {
        var sources = new List<G9CSnapshotFileTableSource>(tables.Count);
        foreach (var table in tables)
        {
            var model = _model.FindTable(table.Table) ?? throw new G9CSyncException(G9SyncErrorCodes.TokenInvalid, $"Unknown table '{table.Table}' in token.");
            var columns = table.Columns
                .Select(name => model.FindColumn(name) ?? throw new G9CSyncException(G9SyncErrorCodes.TokenInvalid, $"Unknown column '{name}' in token."))
                .ToArray();
            var part = new G9DtTokenPart { Table = table.Table, Kind = G9EPullPartKind.Snapshot, Columns = table.Columns };
            var reader = new G9DtPullTokenPayload { NewSnapshot = snapshot, FrameVersion = frameVersion };
            sources.Add(new G9CSnapshotFileTableSource
            {
                TableId = _store.GetTableId(model),
                ServerName = model.LogicalName,
                Shape = table.Shape,
                FrameColumns = columns.Select(c => new G9DtColumnModel { Name = c.Name, Type = c.Type, Nullable = c.Nullable }).ToList(),
                ReadAsync = ct => _store.ReadPartAsync(BuildPartRequest(part, reader, subscription, afterKey: null), ct)
            });
        }

        return new G9CSnapshotFileBuild
        {
            FileId = fileId, Identity = identity, Snapshot = snapshot, Epoch = _store.Epoch, SnapshotUtc = snapshotUtc, SubscriptionKey = subscription.Key,
            Storage = storage, Tables = sources
        };
    }

    /// <summary>
    ///     What a file holds, independent of when: the format, the server epoch, the subscription (key, definition, the caller's server
    ///     parameters), the device's storage forms and, per table, the columns sent and the device's shape. Two plans with the same identity
    ///     would build byte-identical files from the same rows, so they share one.
    /// </summary>
    private string SnapshotFileIdentity(G9CResolvedSubscription subscription, G9DtSnapshotFileStorage storage,
        IEnumerable<(string Table, G9CPushTableShape Columns, G9DtSnapshotFileTable Shape)> tables)
    {
        var text = new System.Text.StringBuilder("g9snap|").Append(G9SyncProtocol.SnapshotFileFormatVersion).Append('|').Append(_store.Epoch.ToString("N"))
            .Append('|').Append(subscription.Key).Append('|').Append(subscription.Definition.DefinitionHash).Append('|').Append(subscription.AuthHash)
            .Append("|s:").Append(storage.DateTimeAsTicks).Append(':').Append(storage.DateTimeFormat).Append(':').Append(storage.DecimalAsText);
        foreach (var (table, columns, shape) in tables)
        {
            text.Append("|t:").Append(table).Append(':');
            foreach (var column in columns.Columns) text.Append(column.Name).Append(',').Append((int)column.Type).Append(';');
            text.Append("|l:").Append(shape.LocalName).Append(':').Append(shape.KeyColumn).Append(':').Append((int)shape.KeyType).Append(':');
            foreach (var column in shape.Columns) text.Append(column.Name).Append(',').Append((int)column.Type).Append(',').Append(column.Nullable).Append(';');
            foreach (var index in shape.Indexes) text.Append("ix:").Append(index.Name).Append(',').Append(index.Unique).Append(',').AppendJoin(',', index.Columns).Append(';');
        }

        return G9CHash.Sha256Hex(text.ToString());
    }

    private static string SnapshotFileId(string identity, string snapshot) => G9CHash.Sha256Hex(identity + "|" + snapshot)[..32];

    // ================================================================ helpers

    private G9DtNegotiatedTable Negotiate(G9CSyncServerTable table, G9DtClientTableModel client)
    {
        var clientColumns = new Dictionary<string, G9DtColumnModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var column in client.Columns) clientColumns.TryAdd(column.Name, column);

        var negotiated = new G9DtNegotiatedTable
        {
            TableId = _store.GetTableId(table),
            Name = table.LogicalName,
            KeyColumn = table.KeyColumn,
            KeyType = table.KeyType,
            Direction = table.Direction,
            ConflictPolicy = table.ConflictPolicy
        };

        foreach (var column in table.Columns)
        {
            if (!clientColumns.Remove(column.Name, out var clientColumn)) continue;
            if (clientColumn.Type != column.Type)
            {
                // Its own list, and said in the server's log too (ADR-0061). Filed under MissingColumns this was indistinguishable from
                // "a newer client asked for a column this server does not have yet", nothing on either side read that list, and a
                // column declared Guid over a varchar simply stopped syncing in both directions - in production, for weeks.
                negotiated.TypeMismatchColumns.Add(new G9DtColumnTypeMismatch { Name = clientColumn.Name, ClientType = clientColumn.Type, ServerType = column.Type });
                LogColumnTypeMismatch(_logger, table.LogicalName, clientColumn.Name, clientColumn.Type, column.Type);
                continue;
            }

            negotiated.Columns.Add(new G9DtColumnModel { Name = column.Name, Type = column.Type, Nullable = column.Nullable });
            if (column.IsComputed) negotiated.ComputedColumns.Add(column.Name);
            if (column.IsReadOnly) negotiated.ReadOnlyColumns.Add(column.Name);
        }

        negotiated.MissingColumns.AddRange(clientColumns.Keys);
        return negotiated;
    }

    private G9DtSubscriptionDefinition ToDto(G9CSyncSubscriptionDefinition definition) => new()
    {
        Name = definition.Name,
        Parameters = definition.ClientParameters
            .Select(p => new G9DtParameterDefinition { Name = p.Name, Type = p.Type, Required = p.Required, IsList = p.IsList }).ToList(),
        DependsOn = definition.DependsOn.Select(d => new G9DtSubscriptionDependency { Name = d.Name, Required = d.Required }).ToList(),
        Tables = definition.Tables.Select(t => t.Table.LogicalName).ToList(),
        Ephemeral = definition.Ephemeral,
        TtlSeconds = (int)Math.Min(int.MaxValue, definition.Ttl.TotalSeconds),
        Heavy = definition.Heavy,
        MoveOutPolicy = definition.MoveOutPolicy,
        ReconcileIntervalMinutes = (int)Math.Min(int.MaxValue, definition.ReconcileInterval.TotalMinutes),
        DefinitionHash = definition.DefinitionHash
    };

    private async Task<G9DtSubscriptionState> DecideAsync(G9DtSubscriptionRef reference, G9CSyncAuthContext auth, CancellationToken cancellationToken)
    {
        if (!G9CSubscriptionResolver.TryResolve(_model, reference, auth, out var resolved, out var failure)) return failure;

        var cursorCheck = await CheckCursorAsync(resolved, reference.Cursor, cancellationToken).ConfigureAwait(false);
        if (cursorCheck is not null) return State(resolved, cursorCheck.Value.Action, cursorCheck.Value.Message);

        return HashesDiffer(reference, resolved)
            ? State(resolved, G9ESubscriptionAction.Reconcile, "The subscription definition or the caller's authorisation changed.")
            : State(resolved, G9ESubscriptionAction.Ok, null);
    }

    private async Task<(G9ESubscriptionAction Action, string Message)?> CheckCursorAsync(G9CResolvedSubscription resolved, string? cursor, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(cursor)) return (G9ESubscriptionAction.Initial, "No cursor: initial snapshot.");
        if (!G9CSyncCursor.TryParse(cursor, out var epoch, out var snapshot)) return (G9ESubscriptionAction.ResyncRequired, "The cursor is unreadable.");
        if (epoch != _store.Epoch) return (G9ESubscriptionAction.ResyncRequired, "The server epoch changed (database restored or copied, or capture reinstalled).");
        if (await _store.IsSnapshotAheadAsync(snapshot, cancellationToken).ConfigureAwait(false))
        {
            return (G9ESubscriptionAction.ResyncRequired, "The cursor is newer than the database (point-in-time restore or failover).");
        }

        var tables = resolved.Definition.Tables.Select(t => t.Table).ToArray();
        return await _store.IsBeyondTombstoneHorizonAsync(snapshot, tables, cancellationToken).ConfigureAwait(false)
            ? (G9ESubscriptionAction.ResyncRequired, "The cursor is older than the tombstone horizon.")
            : null;
    }

    private static bool HashesDiffer(G9DtSubscriptionRef reference, G9CResolvedSubscription resolved) =>
        !string.Equals(reference.DefinitionHash, resolved.Definition.DefinitionHash, StringComparison.Ordinal)
        || !string.Equals(reference.AuthHash ?? string.Empty, resolved.AuthHash, StringComparison.Ordinal);

    private static G9DtSubscriptionState State(G9CResolvedSubscription resolved, G9ESubscriptionAction action, string? message) => new()
    {
        Key = resolved.Key,
        Action = action,
        DefinitionHash = resolved.Definition.DefinitionHash,
        AuthHash = resolved.AuthHash,
        Message = message
    };

    private Dictionary<int, G9CPushTableShape> ResolveShapes(IEnumerable<G9DtTableColumns> tables, bool writableOnly)
    {
        var shapes = new Dictionary<int, G9CPushTableShape>();
        foreach (var requested in tables)
        {
            var table = _store.FindTable(requested.TableId);
            if (table is null) continue;
            var columns = new List<G9CSyncServerColumn>(requested.Columns.Count);
            var valid = true;
            foreach (var name in requested.Columns)
            {
                var column = table.FindColumn(name);
                if (column is null || (writableOnly && !column.IsWritable))
                {
                    valid = false;
                    break;
                }

                columns.Add(column);
            }

            if (valid) shapes[requested.TableId] = new G9CPushTableShape(table, columns);
        }

        return shapes;
    }

    private G9CStorePartRequest BuildPartRequest(G9DtTokenPart part, G9DtPullTokenPayload payload, G9CResolvedSubscription subscription, string? afterKey)
    {
        var table = _model.FindTable(part.Table) ?? throw new G9CSyncException(G9SyncErrorCodes.TokenInvalid, $"Unknown table '{part.Table}' in token.");
        var include = subscription.Definition.FindTable(table)
                      ?? throw new G9CSyncException(G9SyncErrorCodes.TokenInvalid, $"'{part.Table}' is no longer part of '{subscription.Definition.Name}'.");
        var columns = part.Columns
            .Select(name => table.FindColumn(name) ?? throw new G9CSyncException(G9SyncErrorCodes.TokenInvalid, $"Unknown column '{name}' in token."))
            .ToArray();
        return new G9CStorePartRequest
        {
            Table = table,
            Include = include,
            Kind = part.Kind,
            Columns = columns,
            Subscription = subscription,
            PreviousSnapshot = part.PreviousSnapshot,
            NewSnapshot = payload.NewSnapshot,
            AfterKey = afterKey,
            PageRows = _options.PageRows,
            MaxPageBytes = _options.MaxPageBytes,
            FrameVersion = G9SyncProtocol.NegotiateFrameVersion(payload.FrameVersion)
        };
    }

    private async Task RunPostCommitHandlersAsync(G9CSyncCaller caller, G9CSyncAuthContext auth, G9DtPushRequest request, G9DtPushResult result,
        G9CSyncPipeline pipeline, IServiceProvider services)
    {
        foreach (var accepted in result.Accepted)
        {
            var table = _store.FindTable(accepted.TableId);
            if (table is null) continue;
            var upserted = new List<string>();
            var deleted = new List<string>();
            for (var i = 0; i < accepted.Keys.Count; i++)
            {
                (i < accepted.Deleted.Count && accepted.Deleted[i] ? deleted : upserted).Add(accepted.Keys[i]);
            }

            var context = new G9CPostCommitContext
            {
                Table = table,
                ClientId = request.ClientId,
                BatchId = request.BatchId,
                Caller = caller,
                Auth = auth,
                UpsertedKeys = upserted,
                DeletedKeys = deleted,
                Services = services
            };

            foreach (var handler in pipeline.PostCommitHandlers)
            {
                if (!handler.AppliesTo(table)) continue;
                try
                {
                    // Deliberately not cancellable: the transaction already committed.
                    await handler.HandleAsync(context, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    LogPostCommitFailed(_logger, ex, handler.GetType().Name, table.LogicalName, request.ClientId, request.BatchId);
                }
            }
        }
    }

    [LoggerMessage(EventId = 21001, Level = LogLevel.Information, Message = "G9SyncData server initialised: {Tables} tables, {Subscriptions} subscriptions, epoch {Epoch}, {ElapsedMs} ms")]
    private static partial void LogInitialized(ILogger logger, int tables, int subscriptions, Guid epoch, long elapsedMs);

    [LoggerMessage(EventId = 21002, Level = LogLevel.Debug, Message = "Pull planned for client {ClientId}: {Parts} parts, {Rows} rows, {Skipped} skipped subscriptions")]
    private static partial void LogPlanned(ILogger logger, Guid clientId, int parts, long rows, int skipped);

    [LoggerMessage(EventId = 21003, Level = LogLevel.Information, Message = "Push from client {ClientId} batch {BatchId}: {Accepted} accepted, {Rejected} rejected, {Conflicts} conflicts, already applied {AlreadyApplied}, {ElapsedMs} ms")]
    private static partial void LogPushed(ILogger logger, Guid clientId, long batchId, int accepted, int rejected, int conflicts, bool alreadyApplied, long elapsedMs);

    [LoggerMessage(EventId = 21004, Level = LogLevel.Information, Message = "Reconciled {Subscription}: {Tables} tables, {Buckets} mismatched buckets")]
    private static partial void LogReconciled(ILogger logger, string subscription, int tables, int buckets);

    [LoggerMessage(EventId = 21005, Level = LogLevel.Error, Message = "Post-commit handler {Handler} failed for {Table}, client {ClientId} batch {BatchId}")]
    private static partial void LogPostCommitFailed(ILogger logger, Exception exception, string handler, string table, Guid clientId, long batchId);

    [LoggerMessage(EventId = 21007, Level = LogLevel.Warning,
        Message = "G9SyncData signs pull tokens with a RANDOM key: no TokenSigningKey is configured. Every restart or deploy invalidates the pulls in flight, " +
                  "and a second instance rejects this one's tokens. Configure at least 32 random bytes from the secret store")]
    private static partial void LogEphemeralSigningKey(ILogger logger);

    [LoggerMessage(EventId = 21008, Level = LogLevel.Information, Message = "G9SyncData pull tokens: signing key configured, {PreviousKeys} previous key(s) still accepted")]
    private static partial void LogConfiguredSigningKey(ILogger logger, int previousKeys);

    [LoggerMessage(EventId = 21013, Level = LogLevel.Information, Message = "Requeued {Count} failed effect(s) for delivery")]
    private static partial void LogEffectsRequeued(ILogger logger, int count);

    [LoggerMessage(EventId = 21009, Level = LogLevel.Warning, Message = "Refused a {Operation} request: the concurrency limit was reached and no slot came free in time")]
    private static partial void LogBusy(ILogger logger, string operation);

    [LoggerMessage(EventId = 21027, Level = LogLevel.Information, Message = "Snapshot file {FileId} reused for {Subscription}; {DeltaRows} row(s) changed since it was built")]
    private static partial void LogSnapshotFileReused(ILogger logger, string subscription, string fileId, long deltaRows);

    [LoggerMessage(EventId = 21028, Level = LogLevel.Information, Message = "Snapshot file {FileId} planned for {Subscription}: about {Rows} row(s)")]
    private static partial void LogSnapshotFilePlanned(ILogger logger, string subscription, string fileId, long rows);

    // Debug here, Warning on the device: every connecting client of a wrong build repeats it, and the device is where the model is fixed.
    [LoggerMessage(EventId = 21006, Level = LogLevel.Debug,
        Message = "A client declares {Table}.{Column} as {ClientType}; the server's column is {ServerType}. The column is not negotiated and syncs in neither direction")]
    private static partial void LogColumnTypeMismatch(ILogger logger, string table, string column, G9ESyncColumnType clientType, G9ESyncColumnType serverType);
}
