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
}

public sealed class G9CStoreFetchRequest
{
    public required G9CSyncServerTable Table { get; init; }
    public required G9CSyncSubscriptionTable Include { get; init; }
    public required IReadOnlyList<G9CSyncServerColumn> Columns { get; init; }
    public required G9CResolvedSubscription Subscription { get; init; }
    public required IReadOnlyList<string> Keys { get; init; }
    public required int PageRows { get; init; }
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
    public required IServiceProvider Services { get; init; }
    public required int MaxRejections { get; init; }
    public required int MaxRows { get; init; }
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
