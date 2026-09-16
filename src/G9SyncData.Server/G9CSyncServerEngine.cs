using System.Diagnostics;
using System.Runtime.CompilerServices;
using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Diagnostics;
using G9SyncData.Server.Engine;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;
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
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private volatile bool _initialized;

    public G9CSyncServerEngine(
        G9CSyncServerModel model,
        IG9SyncServerStore store,
        IServiceScopeFactory scopeFactory,
        IOptions<G9CSyncServerOptions> options,
        IG9SyncAuthContextProvider authProvider,
        IEnumerable<IG9SyncConnectGate> gates,
        ILogger<G9CSyncServerEngine> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _model = model;
        _store = store;
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _authProvider = authProvider;
        _gates = gates.ToArray();
        _logger = logger;
        _tokens = new G9CSyncTokenService(_options.TokenSigningKey, _options.TokenLifetime);
    }

    public G9CSyncServerModel Model => _model;

    public IG9SyncServerStore Store => _store;

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

        var result = new G9DtHelloResult { Epoch = _store.Epoch, ServerTimeUtc = DateTime.UtcNow };
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
        var payload = new G9DtPullTokenPayload { ClientId = request.ClientId, UserId = auth.UserId, Epoch = _store.Epoch, NewSnapshot = snapshot };
        var resolvedList = new List<G9CResolvedSubscription>();

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

            foreach (var include in resolved.Definition.Tables)
            {
                if (!shapes.TryGetValue(_store.GetTableId(include.Table), out var shape)) continue;
                payload.Parts.Add(new G9DtTokenPart
                {
                    Subscription = subscriptionIndex,
                    Table = include.Table.LogicalName,
                    Kind = previous is null ? G9EPullPartKind.Snapshot : G9EPullPartKind.Delta,
                    PreviousSnapshot = previous,
                    Columns = shape.Columns.Select(c => c.Name).ToList()
                });
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
            }
        }

        foreach (var part in payload.Parts)
        {
            var partRequest = BuildPartRequest(part, payload, resolvedList[part.Subscription], afterKey: null);
            var estimate = await _store.EstimatePartAsync(partRequest, cancellationToken).ConfigureAwait(false);
            plan.Parts.Add(new G9DtPlannedPart
            {
                SubscriptionKey = resolvedList[part.Subscription].Key,
                TableId = _store.GetTableId(partRequest.Table),
                Kind = part.Kind,
                Rows = estimate.Rows,
                EstimatedBytes = estimate.EstimatedBytes
            });
            plan.TotalRows += estimate.Rows;
            plan.EstimatedBytes += estimate.EstimatedBytes;
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

        var sequence = continuation?.NextSequence ?? 0;
        var after = continuation?.AfterKey;
        for (var index = Math.Max(0, continuation?.PartIndex ?? 0); index < payload.Parts.Count; index++)
        {
            var part = payload.Parts[index];
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

        if (request.ClientId == Guid.Empty) throw new G9CSyncException(G9SyncErrorCodes.InvalidParameters, "A push requires a client id.");

        var auth = await _authProvider.ResolveAsync(caller, cancellationToken).ConfigureAwait(false);
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
            PostCommitHandlers = services.GetServices<IG9PostCommitHandler>().ToArray()
        };

        var result = await _store.ApplyPushAsync(new G9CStorePushRequest
        {
            ClientId = request.ClientId,
            BatchId = request.BatchId,
            Caller = caller,
            Auth = auth,
            Frames = frames,
            Tables = shapes,
            Subscriptions = subscriptions,
            Pipeline = pipeline,
            Services = services,
            MaxRejections = _options.MaxRejectionsPerPush,
            MaxRows = _options.MaxPushRows
        }, cancellationToken).ConfigureAwait(false);

        if (!result.AlreadyApplied && result.Error is null && pipeline.PostCommitHandlers.Count > 0)
        {
            await RunPostCommitHandlersAsync(caller, auth, request, result, pipeline, services).ConfigureAwait(false);
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

        var tables = new List<G9CStoreReconcileTable>();
        foreach (var digest in request.Tables)
        {
            var table = _store.FindTable(digest.TableId);
            var include = table is null ? null : resolved.Definition.FindTable(table);
            if (table is null || include is null) continue;
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
                               Subscription = resolved, Tables = tables, PageRows = Math.Max(_options.PageRows, 5000)
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
        if (request.Keys.Count > _options.MaxFetchKeys)
        {
            throw new G9CSyncException(G9SyncErrorCodes.InvalidParameters, $"At most {_options.MaxFetchKeys} keys per fetch.");
        }

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
                                   Table = table, Include = include, Columns = columns, Subscription = resolved, Keys = keys, PageRows = _options.PageRows
                               }, cancellationToken).ConfigureAwait(false))
            {
                yield return new G9DtSyncChunk { Sequence = sequence++, Payload = page.Frame, RowCount = page.RowCount, SubscriptionKey = resolved.Key };
            }
        }

        yield return new G9DtSyncChunk { Sequence = sequence, IsFinal = true, SubscriptionKey = resolved.Key };
    }

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
                negotiated.MissingColumns.Add(clientColumn.Name);
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
        if (epoch != _store.Epoch) return (G9ESubscriptionAction.ResyncRequired, "The server epoch changed (database restored or capture reinstalled).");

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
            PageRows = _options.PageRows
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
}
