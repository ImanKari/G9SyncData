namespace G9SyncData.Server.Capture;

/// <summary>What happened to a row, as reported by the capture backend.</summary>
public enum G9EChangeKind : byte
{
    Insert = 0,
    Update = 1,

    /// <summary>The row was deleted. A row that merely stopped being VISIBLE to a client is not this: visibility is resolved by the view query, not by the capture backend (ADR-0027).</summary>
    Delete = 2
}

/// <summary>
///     An opaque, ordered position in a change stream. The value is produced and understood only by the backend that
///     issued it (an LSN for logical replication, a snapshot cursor for the trigger backend), so the engine can persist
///     and resume it without knowing which backend is in use.
/// </summary>
public readonly record struct G9CChangePosition(string Token)
{
    /// <summary>"Wherever the backend would naturally start" - the slot's confirmed position, or the beginning.</summary>
    public static G9CChangePosition Start => new(string.Empty);

    public bool IsStart => string.IsNullOrEmpty(Token);

    public override string ToString() => IsStart ? "(start)" : Token;
}

/// <summary>
///     One changed row: the table, what happened, and the primary key. Deliberately NOT the column values - per
///     ADR-0027 the capture backend says WHICH rows changed and the visibility query says what a client may see, so
///     that authorisation has exactly one implementation.
/// </summary>
/// <param name="TableLogicalName"><c>schema.table</c>, matching the sync model's logical name.</param>
/// <param name="Kind">Insert, update or delete.</param>
/// <param name="Key">The primary key, in its canonical text form.</param>
public readonly record struct G9CChangedRow(string TableLogicalName, G9EChangeKind Kind, string Key);

/// <summary>
///     The changes of one committed transaction, in commit order.
///     <para>
///         A batch is the unit of ordering and of acknowledgement: everything in it became visible at the same instant,
///         and confirming <see cref="Position" /> means every row in it has been durably handled.
///     </para>
/// </summary>
public sealed class G9CChangeBatch
{
    /// <summary>Rows changed by this transaction, de-duplicated per key (the last change for a key wins).</summary>
    public required IReadOnlyList<G9CChangedRow> Rows { get; init; }

    /// <summary>The position AFTER this batch; persist it, and pass it to <see cref="IG9ChangeSource.ConfirmAsync" /> once the batch is handled.</summary>
    public required G9CChangePosition Position { get; init; }

    /// <summary>
    ///     When the transaction committed, according to the SERVER. Ordering never depends on this (ADR-0022 - the
    ///     position is the authority); it exists for diagnostics, lag metrics and conflict explanations.
    /// </summary>
    public required DateTime CommitTimestampUtc { get; init; }

    /// <summary>The transaction id, for diagnostics. Zero when the backend does not report one.</summary>
    public uint TransactionId { get; init; }

    /// <summary>
    ///     Set when the transaction was a G9SyncData push, to the pushing device's client id. Those changes are already
    ///     tracked by the push itself (ADR-0028), so a consumer skips their rows — but must still confirm the position.
    /// </summary>
    public Guid? OriginClientId { get; init; }

    /// <summary>
    ///     Set on a marker batch: a push asked "have you applied everything committed before now?". A marker carries no
    ///     rows and must NOT be confirmed; the consumer answers it once every earlier batch is durably applied.
    /// </summary>
    public Guid? SyncPoint { get; init; }

    /// <summary>True for a marker batch (see <see cref="SyncPoint" />).</summary>
    public bool IsSyncPoint => SyncPoint.HasValue;

    public override string ToString() => IsSyncPoint
        ? $"sync point {SyncPoint}"
        : $"{Rows.Count} row(s) @ {Position}{(OriginClientId is { } origin ? $" (push by {origin})" : string.Empty)}";
}

/// <summary>Logical-message prefixes G9SyncData writes into the WAL (ADR-0028).</summary>
public static class G9CChangeMessages
{
    /// <summary>Transactional: "this transaction is a push by the client id in the payload".</summary>
    public const string PushPrefix = "g9sync.push";

    /// <summary>Non-transactional: "tell me when you have applied everything before this point"; payload is a nonce.</summary>
    public const string SyncPointPrefix = "g9sync.sync";
}

/// <summary>
///     A source of committed row changes. Implementations: PostgreSQL logical replication (default) and the trigger
///     backend (ADR-0021). The engine above this interface does not know which is in use.
/// </summary>
public interface IG9ChangeSource : IAsyncDisposable
{
    /// <summary>A short name for logs and metrics, e.g. <c>wal</c> or <c>triggers</c>.</summary>
    string Name { get; }

    /// <summary>
    ///     Streams batches in commit order, starting after <paramref name="from" />. The stream is long-lived: it stays
    ///     open and yields batches as transactions commit, until the token is cancelled.
    /// </summary>
    IAsyncEnumerable<G9CChangeBatch> ReadAsync(G9CChangePosition from, CancellationToken cancellationToken);

    /// <summary>
    ///     Tells the backend that everything up to <paramref name="position" /> is durably handled and its resources may
    ///     be released.
    ///     <para>
    ///         ⚠ For logical replication this is what lets PostgreSQL recycle WAL. A consumer that reads but never
    ///         confirms makes the slot retain WAL until the disk (or <c>max_slot_wal_keep_size</c>) says otherwise -
    ///         see <c>AiGuides/20-Infrastructure-PostgreSQL.md</c>.
    ///     </para>
    /// </summary>
    ValueTask ConfirmAsync(G9CChangePosition position, CancellationToken cancellationToken);
}
