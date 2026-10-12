namespace G9SyncData.Protocol;

/// <summary>What a subscription must do next, as decided by the server at Hello / PlanPull.</summary>
public enum G9ESubscriptionAction : byte
{
    /// <summary>Cursor valid: pull the delta.</summary>
    Ok = 0,

    /// <summary>No cursor: pull the initial snapshot.</summary>
    Initial = 1,

    /// <summary>Definition or authorisation changed: reconcile, then pull.</summary>
    Reconcile = 2,

    /// <summary>Cursor unusable (epoch changed, tombstone horizon passed): reconcile, then pull.</summary>
    ResyncRequired = 3,

    /// <summary>Subscription not defined on this server.</summary>
    Unknown = 4,

    /// <summary>Parameters missing or invalid.</summary>
    InvalidParameters = 5,

    /// <summary>The caller may not use this subscription.</summary>
    Forbidden = 6
}

/// <summary>Kind of a planned pull part.</summary>
public enum G9EPullPartKind : byte
{
    Snapshot = 0,
    Delta = 1,
    Backfill = 2,

    /// <summary>
    ///     A subscription's snapshot as a SQLite file (ADR-0072): one part for all of the subscription's tables, streamed as file segments
    ///     (<see cref="G9DtSyncChunk.File" />). The part's <see cref="G9DtPlannedPart.File" /> names the file and its tables.
    /// </summary>
    SnapshotFile = 3
}

/// <summary>Structured error. Code is from <see cref="G9SyncErrorCodes" />.</summary>
public sealed class G9DtSyncError
{
    public string Code { get; set; } = G9SyncErrorCodes.ServerError;
    public string Message { get; set; } = string.Empty;
    public bool Retryable { get; set; }
    public string? Details { get; set; }

    public static G9DtSyncError From(G9CSyncException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new G9DtSyncError
        {
            Code = exception.Code, Message = exception.Message, Retryable = exception.Retryable, Details = exception.Details
        };
    }

    public G9CSyncException ToException() =>
        new(Code, Message) { Retryable = Retryable, Details = Details };
}

/// <summary>A column in a table model (client) or a negotiated table (server answer).</summary>
public sealed class G9DtColumnModel
{
    public string Name { get; set; } = string.Empty;
    public G9ESyncColumnType Type { get; set; }
    public bool Nullable { get; set; }
}

/// <summary>A subscription as the client holds it: name, canonical parameters and its sync state.</summary>
public sealed class G9DtSubscriptionRef
{
    public string Name { get; set; } = string.Empty;
    public Dictionary<string, string> Parameters { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Opaque server cursor of the last completed pull. Null before the first pull.</summary>
    public string? Cursor { get; set; }

    public string? DefinitionHash { get; set; }
    public string? AuthHash { get; set; }

    /// <summary>The canonical subscription key.</summary>
    public string GetKey() => G9CSyncParameters.BuildSubscriptionKey(Name, G9CSyncParameters.FromValues(Parameters));
}

/// <summary>Resume point after a chunk. Only chunks that carry one are safe restart points.</summary>
public sealed class G9DtPullContinuation
{
    public int PartIndex { get; set; }
    public string? AfterKey { get; set; }
    public int NextSequence { get; set; }
}

/// <summary>One unit of a data stream. <see cref="Payload" /> is a G9RowBatch frame.</summary>
public sealed class G9DtSyncChunk
{
    public int Sequence { get; set; }
    public byte[]? Payload { get; set; }
    public int RowCount { get; set; }
    public string? SubscriptionKey { get; set; }
    public bool IsFinal { get; set; }
    public G9DtPullContinuation? Continuation { get; set; }
    public G9DtReconcileSummary? ReconcileSummary { get; set; }

    /// <summary>
    ///     Set when the chunk carries a snapshot-file segment (ADR-0072): <see cref="Payload" /> is then the segment, Brotli-compressed on
    ///     its own, not a G9RowBatch frame.
    /// </summary>
    public G9DtFileSegment? File { get; set; }
}

/// <summary>Server → client hint that tables changed.</summary>
public sealed class G9DtChangesAvailable
{
    public List<string> Tables { get; set; } = [];
}
