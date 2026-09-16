namespace G9SyncData.Protocol;

/// <summary>
///     The column order the client uses for one table: the order of values inside push frames, and the columns a pull
///     must return. The server is stateless, so every request that moves rows names its columns.
/// </summary>
public sealed class G9DtTableColumns
{
    public int TableId { get; set; }
    public List<string> Columns { get; set; } = [];
}

public sealed class G9DtPushRequest
{
    public Guid ClientId { get; set; }

    /// <summary>Client-assigned, monotonic per client. A batch id at or below the last applied one is acknowledged, never re-applied.</summary>
    public long BatchId { get; set; }

    /// <summary>The client's subscriptions; write authorisation checks rows against these scopes.</summary>
    public List<G9DtSubscriptionRef> Subscriptions { get; set; } = [];

    public List<G9DtTableColumns> Tables { get; set; } = [];
}

public sealed class G9DtAcceptedRows
{
    public int TableId { get; set; }
    public List<string> Keys { get; set; } = [];

    /// <summary>The server version of each accepted row after the push (0 when the delete removed nothing tracked).</summary>
    public List<long> Versions { get; set; } = [];

    public List<bool> Deleted { get; set; } = [];
}

public sealed class G9DtRowRejection
{
    public int TableId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Code { get; set; } = G9SyncErrorCodes.RejectConstraint;
    public string? Message { get; set; }
    public string? SqlState { get; set; }
    public string? Constraint { get; set; }
    public string? Column { get; set; }
    public string? Details { get; set; }
}

public sealed class G9DtRowConflict
{
    public int TableId { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Code { get; set; } = G9SyncErrorCodes.ConflictServerWins;

    /// <summary>Bits (in push column order) of columns the server changed after the client's base version; -1 when unknown.</summary>
    public long ServerChangedMask { get; set; }

    /// <summary>True when some of the client's columns were applied (column merge).</summary>
    public bool PartiallyApplied { get; set; }
}

public sealed class G9DtPushResult
{
    public long BatchId { get; set; }
    public bool AlreadyApplied { get; set; }
    public List<G9DtAcceptedRows> Accepted { get; set; } = [];
    public List<G9DtRowRejection> Rejections { get; set; } = [];
    public List<G9DtRowConflict> Conflicts { get; set; } = [];

    /// <summary>G9RowBatch frames with the canonical server rows for conflicted or server-transformed rows.</summary>
    public List<byte[]> CanonicalFrames { get; set; } = [];

    public G9DtSyncError? Error { get; set; }
}

public sealed class G9DtBackfillRequest
{
    public int TableId { get; set; }
    public List<string> Columns { get; set; } = [];
}

public sealed class G9DtPullRequest
{
    public Guid ClientId { get; set; }
    public List<G9DtSubscriptionRef> Subscriptions { get; set; } = [];

    /// <summary>The negotiated columns the client wants for every table it holds.</summary>
    public List<G9DtTableColumns> Tables { get; set; } = [];

    public List<G9DtBackfillRequest> Backfills { get; set; } = [];
}

public sealed class G9DtPlannedPart
{
    public string SubscriptionKey { get; set; } = string.Empty;
    public int TableId { get; set; }
    public G9EPullPartKind Kind { get; set; }
    public long Rows { get; set; }
    public long EstimatedBytes { get; set; }
}

public sealed class G9DtSubscriptionCursor
{
    public string Key { get; set; } = string.Empty;
    public string Cursor { get; set; } = string.Empty;
    public string DefinitionHash { get; set; } = string.Empty;
    public string AuthHash { get; set; } = string.Empty;
}

public sealed class G9DtPullPlan
{
    /// <summary>Signed token naming every part. Any server instance can serve it (stateless).</summary>
    public string Token { get; set; } = string.Empty;

    public List<G9DtPlannedPart> Parts { get; set; } = [];
    public long TotalRows { get; set; }
    public long EstimatedBytes { get; set; }

    /// <summary>The cursors to store once every chunk has been applied.</summary>
    public List<G9DtSubscriptionCursor> Cursors { get; set; } = [];

    /// <summary>Subscriptions left out of the plan and why (the client reconciles or reports them).</summary>
    public List<G9DtSubscriptionState> Skipped { get; set; } = [];

    public G9DtSyncError? Error { get; set; }
}

public sealed class G9DtTableDigest
{
    public int TableId { get; set; }

    /// <summary>Computed columns (in order) whose canonical text the client folded into each row hash.</summary>
    public List<string> ComputedColumns { get; set; } = [];

    public long[] Digests { get; set; } = new long[G9CSyncDigest.BucketCount];
    public int[] Counts { get; set; } = new int[G9CSyncDigest.BucketCount];
}

public sealed class G9DtReconcileRequest
{
    public Guid ClientId { get; set; }
    public G9DtSubscriptionRef Subscription { get; set; } = new();
    public List<G9DtTableDigest> Tables { get; set; } = [];
}

public sealed class G9DtTableBuckets
{
    public int TableId { get; set; }
    public List<int> Buckets { get; set; } = [];
}

/// <summary>Carried by the final reconcile chunk.</summary>
public sealed class G9DtReconcileSummary
{
    public string Cursor { get; set; } = string.Empty;
    public string DefinitionHash { get; set; } = string.Empty;
    public string AuthHash { get; set; } = string.Empty;
    public List<G9DtTableBuckets> Mismatched { get; set; } = [];
}

public sealed class G9DtFetchRowsRequest
{
    public Guid ClientId { get; set; }
    public G9DtSubscriptionRef Subscription { get; set; } = new();
    public int TableId { get; set; }
    public List<string> Keys { get; set; } = [];
    public List<string> Columns { get; set; } = [];
}
