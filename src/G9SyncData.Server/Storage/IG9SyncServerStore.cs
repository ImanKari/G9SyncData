using G9SyncData.Protocol;
using G9SyncData.Server.Auth;
using G9SyncData.Server.Model;
using G9SyncData.Server.Pipeline;

namespace G9SyncData.Server.Storage;

/// <summary>
///     The server storage provider. Everything database-specific lives behind this contract: change capture, the cursor
///     (for PostgreSQL a transaction snapshot), reads, the upload apply, reconciliation digests, notifications and cleanup.
///     The engine stays provider-agnostic, which is what lets another database implement G9SyncData later.
/// </summary>
public interface IG9SyncServerStore
{
    /// <summary>Installs or verifies capture objects, validates the model against the live schema and loads table ids.</summary>
    Task InitializeAsync(CancellationToken cancellationToken);

    /// <summary>Identity of this capture installation. Changes when the database is restored elsewhere or capture is reinstalled.</summary>
    Guid Epoch { get; }

    int GetTableId(G9CSyncServerTable table);

    G9CSyncServerTable? FindTable(int tableId);

    /// <summary>An opaque, provider-specific snapshot marking "everything committed up to now".</summary>
    Task<string> GetCurrentSnapshotAsync(CancellationToken cancellationToken);

    /// <summary>True when deletes the snapshot has not seen were already purged, so a delta from it would miss tombstones.</summary>
    Task<bool> IsBeyondTombstoneHorizonAsync(string snapshot, IReadOnlyCollection<G9CSyncServerTable> tables, CancellationToken cancellationToken);

    /// <summary>
    ///     True when a snapshot a device holds is newer than the database itself (a point-in-time restore or a failover to a lagging
    ///     standby): nothing read with it can be trusted, and the device must reconcile.
    /// </summary>
    Task<bool> IsSnapshotAheadAsync(string snapshot, CancellationToken cancellationToken);

    Task TouchClientAsync(Guid clientId, string? userId, string? appVersion, CancellationToken cancellationToken);

    Task<G9CStorePartEstimate> EstimatePartAsync(G9CStorePartRequest request, CancellationToken cancellationToken);

    /// <summary>Reads one pull part in key order, one frame per page. Pages with no rows are never yielded.</summary>
    IAsyncEnumerable<G9CStorePage> ReadPartAsync(G9CStorePartRequest request, CancellationToken cancellationToken);

    IAsyncEnumerable<G9CStorePage> FetchRowsAsync(G9CStoreFetchRequest request, CancellationToken cancellationToken);

    /// <summary>Applies one push atomically (rejected rows excluded) and records the batch id in the same transaction.</summary>
    Task<G9DtPushResult> ApplyPushAsync(G9CStorePushRequest request, CancellationToken cancellationToken);

    /// <summary>Computes digests, streams key/version listings of mismatched buckets, and finishes with the snapshot used.</summary>
    IAsyncEnumerable<G9CStoreReconcileItem> ReconcileAsync(G9CStoreReconcileRequest request, CancellationToken cancellationToken);

    /// <summary>Yields batches of table ids whose changes committed. Completes or throws when the listener connection drops.</summary>
    IAsyncEnumerable<IReadOnlyList<int>> ListenForChangesAsync(CancellationToken cancellationToken);

    // ------------------------------------------------ durable effects (ADR-0052)

    /// <summary>
    ///     Takes up to <paramref name="max" /> effects that are due, counting an attempt against each and putting the next one
    ///     <paramref name="retryDelay" /> away, so a crashed worker's claims come back on their own and two workers never take the same
    ///     effect.
    /// </summary>
    Task<IReadOnlyList<G9CPendingEffect>> ClaimEffectsAsync(int max, TimeSpan retryDelay, CancellationToken cancellationToken);

    /// <summary>Marks an effect delivered; it is gone for good.</summary>
    Task CompleteEffectAsync(Guid effectId, CancellationToken cancellationToken);

    /// <summary>
    ///     Records a failed delivery. Past <paramref name="maxAttempts" /> the effect stops being retried and stays visible as failed,
    ///     rather than disappearing or retrying forever.
    /// </summary>
    Task FailEffectAsync(Guid effectId, string error, int maxAttempts, CancellationToken cancellationToken);

    /// <summary>Effects that exhausted their attempts, for operators and health checks.</summary>
    Task<IReadOnlyList<G9CPendingEffect>> GetFailedEffectsAsync(int max, CancellationToken cancellationToken);

    /// <summary>
    ///     Puts effects that exhausted their attempts back in the queue: due now, attempts reset (ADR-0065). Null requeues every failed
    ///     effect. Returns how many were requeued. Until this existed a handler bug that was later fixed left every effect it had failed
    ///     marked failed for good - the fix reached the next state change, never the ones already lost.
    /// </summary>
    Task<int> RequeueFailedEffectsAsync(IReadOnlyCollection<Guid>? effectIds, CancellationToken cancellationToken) => Task.FromResult(0);

    /// <summary>How many effects wait and how many failed for good, and how long the oldest waiting one has waited (ADR-0065).</summary>
    Task<G9CEffectQueueStatus> GetEffectQueueStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new G9CEffectQueueStatus(0, 0, null));

    /// <summary>Deletes tombstones older than <paramref name="retention" /> and advances the horizon. Returns the number removed.</summary>
    Task<int> CleanupTombstonesAsync(TimeSpan retention, CancellationToken cancellationToken);

    /// <summary>
    ///     Changes of one table committed between two snapshots, in key order, at most <see cref="G9CStoreChangeRequest.Limit" />
    ///     rows after <see cref="G9CStoreChangeRequest.AfterKey" /> — the server-side change feed (plan D-15).
    /// </summary>
    Task<IReadOnlyList<G9CStoreChange>> ReadChangesAsync(G9CStoreChangeRequest request, CancellationToken cancellationToken);
}

public sealed class G9CStoreChangeRequest
{
    public required G9CSyncServerTable Table { get; init; }
    public required string PreviousSnapshot { get; init; }
    public required string NewSnapshot { get; init; }
    public string? AfterKey { get; init; }
    public required int Limit { get; init; }
}

/// <summary>One tracked change. <see cref="ChangedMask" /> has bit i set for physical column i; -1 means all or unknown.</summary>
public sealed record G9CStoreChange(string Key, long Version, bool Deleted, long ChangedMask, Guid? OriginClient, DateTime ChangedAtUtc);

/// <summary>A subscription reference validated against the model and the caller's authorisation.</summary>
public sealed class G9CResolvedSubscription
{
    public required G9CSyncSubscriptionDefinition Definition { get; init; }

    /// <summary>Canonical subscription key.</summary>
    public required string Key { get; init; }

    /// <summary>Canonical client parameters.</summary>
    public required G9CSyncParameters Parameters { get; init; }

    /// <summary>Typed values for every declared parameter (client and server). Null binds SQL NULL.</summary>
    public required IReadOnlyDictionary<string, object?> Values { get; init; }

    /// <summary>Hash of the server-injected parameter values (empty when there are none).</summary>
    public required string AuthHash { get; init; }

    /// <summary>The caller's authorisation context the subscription was resolved with.</summary>
    public G9CSyncAuthContext Auth { get; init; } = G9CSyncAuthContext.Anonymous;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<G9CSyncSubscriptionTable, Task<G9CSyncScopeFragment>> _scopes = new();

    /// <summary>
    ///     The provider-built key query of an include, computed once per resolved subscription (i.e. once per request) however
    ///     many statements of that request need it.
    /// </summary>
    public Task<G9CSyncScopeFragment> GetScopeAsync(G9CSyncSubscriptionTable include, IServiceProvider services, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(include);
        ArgumentNullException.ThrowIfNull(services);
        var provider = include.ScopeProvider
                       ?? throw new InvalidOperationException($"{include.Table.LogicalName} in '{Definition.Name}' has no scope provider.");

        return _scopes.GetOrAdd(include, _ => provider.CreateAsync(new G9CSyncScopeContext
        {
            Subscription = Definition,
            Table = include.Table,
            Values = Values,
            Auth = Auth,
            Services = services
        }, cancellationToken).AsTask());
    }
}

/// <summary>Cursor text: <c>v1:{epoch}:{provider snapshot}</c>.</summary>
public static class G9CSyncCursor
{
    private const string Prefix = "v1:";

    public static string Format(Guid epoch, string snapshot)
    {
        ArgumentException.ThrowIfNullOrEmpty(snapshot);
        return Prefix + epoch.ToString("N") + ":" + snapshot;
    }

    public static bool TryParse(string? cursor, out Guid epoch, out string snapshot)
    {
        epoch = Guid.Empty;
        snapshot = string.Empty;
        if (cursor is null || !cursor.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        var rest = cursor.AsSpan(Prefix.Length);
        var separator = rest.IndexOf(':');
        if (separator <= 0 || !Guid.TryParseExact(rest[..separator], "N", out epoch)) return false;
        snapshot = rest[(separator + 1)..].ToString();
        return snapshot.Length > 0;
    }
}

public sealed record G9CStorePartEstimate(long Rows, long EstimatedBytes);

/// <summary>One page of rows as a G9RowBatch frame. <see cref="LastKey" /> is the canonical key of the last row (resume point).</summary>
public sealed record G9CStorePage(byte[] Frame, int RowCount, string? LastKey, bool IsLast);

public sealed class G9CStorePartRequest
{
    public required G9CSyncServerTable Table { get; init; }
    public required G9CSyncSubscriptionTable Include { get; init; }
    public required G9EPullPartKind Kind { get; init; }
    public required IReadOnlyList<G9CSyncServerColumn> Columns { get; init; }
    public required G9CResolvedSubscription Subscription { get; init; }
    public string? PreviousSnapshot { get; init; }
    public required string NewSnapshot { get; init; }
    public string? AfterKey { get; init; }
    public required int PageRows { get; init; }

    /// <summary>G9RowBatch body layout of the frames this request produces (<see cref="G9SyncData.Protocol.G9SyncProtocol.NegotiateFrameVersion" />).</summary>
    public int FrameVersion { get; init; } = 1;
}

public sealed class G9CStoreFetchRequest
{
    public required G9CSyncServerTable Table { get; init; }
    public required G9CSyncSubscriptionTable Include { get; init; }
    public required IReadOnlyList<G9CSyncServerColumn> Columns { get; init; }
    public required G9CResolvedSubscription Subscription { get; init; }
    public required IReadOnlyList<string> Keys { get; init; }
    public required int PageRows { get; init; }

    /// <summary>G9RowBatch body layout of the frames this request produces (<see cref="G9SyncData.Protocol.G9SyncProtocol.NegotiateFrameVersion" />).</summary>
    public int FrameVersion { get; init; } = 1;
}

/// <summary>The column layout of one table inside the push frames.</summary>
public sealed record G9CPushTableShape(G9CSyncServerTable Table, IReadOnlyList<G9CSyncServerColumn> Columns);

public sealed class G9CStorePushRequest
{
    public required Guid ClientId { get; init; }
    public required long BatchId { get; init; }
    public required G9CSyncCaller Caller { get; init; }
    public required G9CSyncAuthContext Auth { get; init; }
    public required IReadOnlyList<byte[]> Frames { get; init; }
    public required IReadOnlyDictionary<int, G9CPushTableShape> Tables { get; init; }
    public required IReadOnlyList<G9CResolvedSubscription> Subscriptions { get; init; }
    public required G9CSyncPipeline Pipeline { get; init; }

    /// <summary>Commands to execute before the rows, in device order (ADR-0025).</summary>
    public IReadOnlyList<G9SyncData.Protocol.G9DtCommand> Commands { get; init; } = [];

    /// <summary>
    ///     Tables the device holds, with its columns in the device's order: the shape of every canonical row the push returns
    ///     (for the pushed tables always; for every table when the push carries commands).
    /// </summary>
    public IReadOnlyDictionary<int, G9CPushTableShape> HeldTables { get; init; } = new Dictionary<int, G9CPushTableShape>();
    public required IServiceProvider Services { get; init; }
    public required int MaxRejections { get; init; }
    public required int MaxRows { get; init; }

    /// <summary>
    ///     The largest decoded body accepted from one pushed frame, and from the push as a whole. A frame's declared body length is what
    ///     it is decompressed into, so these bound the memory a caller can ask the server for, whatever the frame weighed on the wire
    ///     (ADR-0051).
    /// </summary>
    public required int MaxDecodedFrameBytes { get; init; }

    public required long MaxDecodedPushBytes { get; init; }

    /// <summary>G9RowBatch body layout of the frames this request produces (<see cref="G9SyncData.Protocol.G9SyncProtocol.NegotiateFrameVersion" />).</summary>
    public int FrameVersion { get; init; } = 1;
}

public sealed record G9CStoreReconcileTable(
    G9CSyncServerTable Table,
    G9CSyncSubscriptionTable Include,
    G9DtTableDigest Digest,
    IReadOnlyList<G9CSyncServerColumn> ComputedColumns);

public sealed class G9CStoreReconcileRequest
{
    public required G9CResolvedSubscription Subscription { get; init; }
    public required IReadOnlyList<G9CStoreReconcileTable> Tables { get; init; }
    public required int PageRows { get; init; }

    /// <summary>G9RowBatch body layout of the frames this request produces (<see cref="G9SyncData.Protocol.G9SyncProtocol.NegotiateFrameVersion" />).</summary>
    public int FrameVersion { get; init; } = 1;
}

/// <summary>Either a key/version page or the final item carrying the snapshot and the mismatched buckets.</summary>
public sealed class G9CStoreReconcileItem
{
    public byte[]? Frame { get; init; }
    public int RowCount { get; init; }
    public bool IsFinal { get; init; }
    public string? Snapshot { get; init; }
    public IReadOnlyList<G9DtTableBuckets> Mismatched { get; init; } = [];
}

/// <summary>An effect recorded by a committed push and not yet delivered (ADR-0052).</summary>
/// <summary>The state of the durable effect queue.</summary>
/// <param name="Pending">Effects still to be delivered (including those waiting out a retry delay).</param>
/// <param name="Failed">Effects that exhausted their attempts and are no longer retried.</param>
/// <param name="OldestPendingAge">How long the oldest pending effect has existed; null when nothing is pending.</param>
public sealed record G9CEffectQueueStatus(long Pending, long Failed, TimeSpan? OldestPendingAge);

public sealed class G9CPendingEffect
{
    public required Guid EffectId { get; init; }
    public required int TableId { get; init; }
    public required Guid ClientId { get; init; }
    public required long BatchId { get; init; }
    public required string? UserId { get; init; }
    public required IReadOnlyList<string> UpsertedKeys { get; init; }
    public required IReadOnlyList<string> DeletedKeys { get; init; }

    /// <summary>How many deliveries have been attempted, this one included.</summary>
    public required int Attempt { get; init; }

    /// <summary>Why the last attempt failed, when one has.</summary>
    public string? LastError { get; init; }
}
