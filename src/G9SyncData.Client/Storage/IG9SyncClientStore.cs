using G9SyncData.Client.Model;
using G9SyncData.Protocol;

namespace G9SyncData.Client.Storage;

/// <summary>
///     The client storage provider: local tables, change capture, subscription membership, staging, apply and metadata.
///     The engine is provider-agnostic; <c>G9SyncData.Client.Sqlite</c> is the v1 implementation.
/// </summary>
public interface IG9SyncClientStore : IAsyncDisposable
{
    /// <summary>Creates or updates local tables, indexes, capture triggers and metadata for the model. Idempotent.</summary>
    Task InitializeAsync(G9CSyncClientModel model, CancellationToken cancellationToken);

    Task<Guid> GetClientIdAsync(CancellationToken cancellationToken);

    /// <summary>The last Hello answer (negotiated tables, definitions, epoch).</summary>
    Task<G9CClientServerState> GetServerStateAsync(CancellationToken cancellationToken);

    /// <summary>Stores the Hello answer and works out which newly negotiated columns need a backfill.</summary>
    Task SaveHelloAsync(G9DtHelloResult hello, CancellationToken cancellationToken);

    Task<IReadOnlyList<G9DtBackfillRequest>> GetPendingBackfillsAsync(CancellationToken cancellationToken);

    // ------------------------------------------------ subscriptions

    Task<IReadOnlyList<G9CSyncSubscriptionRecord>> GetSubscriptionsAsync(CancellationToken cancellationToken);

    Task<G9CSyncSubscriptionRecord> EnsureSubscriptionAsync(string name, G9CSyncParameters parameters, bool ephemeral, TimeSpan? ttl,
        bool adoptExistingRows, CancellationToken cancellationToken);

    /// <summary>Removes a subscription and its membership; rows no other subscription holds follow <paramref name="policy" />.</summary>
    Task RemoveSubscriptionAsync(string key, G9EMoveOutPolicy policy, CancellationToken cancellationToken);

    Task UpdateSubscriptionAsync(string key, G9CSubscriptionUpdate update, CancellationToken cancellationToken);

    /// <summary>Deletes ephemeral subscriptions unused longer than their TTL. Returns how many were removed.</summary>
    Task<int> CollectExpiredEphemeralAsync(DateTime utcNow, CancellationToken cancellationToken);

    // ------------------------------------------------ push

    /// <summary>Builds the next push batch from dirty rows in local change order, or null when nothing is pending. Reuses an in-flight batch id.</summary>
    Task<G9CPushBatch?> BuildPushBatchAsync(IReadOnlyList<G9DtNegotiatedTable> tables, int maxRows, CancellationToken cancellationToken);

    Task<G9CApplyReport> ApplyPushResultAsync(G9CPushBatch batch, G9DtPushResult result, CancellationToken cancellationToken);

    // ------------------------------------------------ pull

    Task<G9CStagedPull?> GetStagedPullAsync(CancellationToken cancellationToken);

    Task BeginStagedPullAsync(G9CStagedPull pull, CancellationToken cancellationToken);

    Task StageChunkAsync(string runId, G9DtSyncChunk chunk, CancellationToken cancellationToken);

    /// <summary>The last safe resume point of a staged run; chunks staged after it are discarded.</summary>
    Task<G9CResumePoint> GetResumePointAsync(string runId, CancellationToken cancellationToken);

    /// <summary>Applies every staged chunk of a completed run atomically and stores the new cursors.</summary>
    Task<G9CApplyReport> ApplyStagedPullAsync(string runId, IProgress<long>? rowsApplied, CancellationToken cancellationToken);

    Task DiscardStagedPullAsync(string runId, CancellationToken cancellationToken);

    // ------------------------------------------------ reconcile

    Task<G9DtTableDigest> ComputeDigestAsync(string subscriptionKey, G9DtNegotiatedTable table, CancellationToken cancellationToken);

    /// <summary>
    ///     Local entries for reconciliation: every member row of the subscription whose key falls in <paramref name="buckets" />,
    ///     plus every local row (member or not) whose key is in <paramref name="serverKeys" />.
    /// </summary>
    Task<IReadOnlyList<G9CLocalRowEntry>> ReadLocalEntriesAsync(string subscriptionKey, G9DtNegotiatedTable table, IReadOnlyCollection<int> buckets,
        IReadOnlyCollection<string> serverKeys, CancellationToken cancellationToken);

    Task<G9CApplyReport> ApplyReconcileAsync(string subscriptionKey, G9DtNegotiatedTable table, IReadOnlyList<byte[]> fetchedFrames,
        IReadOnlyList<string> addMembership, IReadOnlyList<string> moveOut, G9EMoveOutPolicy policy, CancellationToken cancellationToken);

    // ------------------------------------------------ queries and maintenance

    Task<G9CPendingSummary> GetPendingSummaryAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<G9CChangeFeedEntry>> ReadChangeFeedAsync(string localTable, long afterSequence, int limit, CancellationToken cancellationToken);

    Task<IReadOnlyList<G9CSyncRejection>> GetRejectionsAsync(CancellationToken cancellationToken);

    /// <summary>Clears rejection flags so the rows are pushed again. Null arguments widen the scope.</summary>
    Task<int> RetryRejectedAsync(string? localTable, string? key, CancellationToken cancellationToken);

    /// <summary>Flags existing local rows as changed (full column mask), e.g. to import pending edits from another sync system.</summary>
    Task<int> MarkDirtyAsync(string localTable, IReadOnlyCollection<string> keys, CancellationToken cancellationToken);

    /// <summary>Registers metadata for local rows that have none (rows written before capture existed). Returns how many were adopted.</summary>
    Task<int> AdoptExistingRowsAsync(CancellationToken cancellationToken);

    Task SaveRunReportAsync(G9CSyncRunReport report, int keep, CancellationToken cancellationToken);

    Task<IReadOnlyList<G9CSyncRunReport>> GetRecentRunReportsAsync(int count, CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, double>> GetPhaseStatisticsAsync(CancellationToken cancellationToken);

    Task RecordPhaseStatisticsAsync(IReadOnlyDictionary<string, double> samples, CancellationToken cancellationToken);

    /// <summary>Purges deleted change-feed entries beyond the newest <paramref name="keep" />.</summary>
    Task<int> PurgeDeletedFeedAsync(int keep, CancellationToken cancellationToken);
}

/// <summary>What the client last learned from the server.</summary>
public sealed class G9CClientServerState
{
    public Guid? Epoch { get; init; }
    public DateTime? LastHelloUtc { get; init; }
    public TimeSpan ServerClockOffset { get; init; }
    public IReadOnlyList<G9DtNegotiatedTable> Tables { get; init; } = [];
    public IReadOnlyList<G9DtSubscriptionDefinition> Definitions { get; init; } = [];
}

public enum G9ESubscriptionState : byte
{
    Active = 0,
    NeedsReconcile = 1,
    Failed = 2
}

/// <summary>A local subscription.</summary>
public sealed class G9CSyncSubscriptionRecord
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required G9CSyncParameters Parameters { get; init; }

    /// <summary>Membership slot (bit) for persistent subscriptions; the shared ephemeral slot otherwise.</summary>
    public required int Slot { get; init; }

    public required bool Ephemeral { get; init; }
    public TimeSpan? Ttl { get; init; }
    public string? Cursor { get; init; }
    public string? DefinitionHash { get; init; }
    public string? AuthHash { get; init; }
    public G9ESubscriptionState State { get; init; }
    public string? LastErrorCode { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime? LastSuccessUtc { get; init; }
    public DateTime? LastReconcileUtc { get; init; }
    public DateTime? LastAccessUtc { get; init; }

    public G9DtSubscriptionRef ToReference() => new()
    {
        Name = Name,
        Parameters = new Dictionary<string, string>(Parameters.Values, StringComparer.Ordinal),
        Cursor = Cursor,
        DefinitionHash = DefinitionHash,
        AuthHash = AuthHash
    };
}

/// <summary>A partial update of a subscription record. Null members are left unchanged.</summary>
public sealed class G9CSubscriptionUpdate
{
    public string? Cursor { get; init; }
    public string? DefinitionHash { get; init; }
    public string? AuthHash { get; init; }
    public G9ESubscriptionState? State { get; init; }

    /// <summary>Set to clear or set the last error. Use <see cref="ClearError" /> to clear.</summary>
    public string? ErrorCode { get; init; }

    public bool ClearError { get; init; }
    public bool Succeeded { get; init; }
    public bool Reconciled { get; init; }
    public bool Accessed { get; init; }
    public bool ClearCursor { get; init; }
}

/// <summary>A push batch built from dirty rows.</summary>
public sealed class G9CPushBatch
{
    public required long BatchId { get; init; }

    /// <summary>Highest local change sequence included; rows changed after it stay dirty after the acknowledgement.</summary>
    public required long MaxSequence { get; init; }

    public required IReadOnlyList<byte[]> Frames { get; init; }
    public required IReadOnlyList<G9DtTableColumns> Tables { get; init; }
    public required int RowCount { get; init; }
}

/// <summary>A pull being staged (persisted, survives app restarts).</summary>
public sealed class G9CStagedPull
{
    public required string RunId { get; init; }
    public required G9DtPullPlan Plan { get; init; }
    public required DateTime CreatedUtc { get; init; }

    /// <summary>The backfills requested with the plan: they give backfill sections their column order and are marked synced on apply.</summary>
    public IReadOnlyList<G9DtBackfillRequest> Backfills { get; init; } = [];
}

public sealed record G9CResumePoint(G9DtPullContinuation? Continuation, long StagedRows, long StagedBytes, bool Complete);

/// <summary>What an apply changed.</summary>
public sealed class G9CApplyReport
{
    public long Upserts { get; set; }
    public long Deletes { get; set; }
    public long MoveOuts { get; set; }
    public long MembershipAdded { get; set; }

    /// <summary>Rows skipped because they carry a pending local change.</summary>
    public long SkippedDirty { get; set; }

    public long Accepted { get; set; }
    public long Rejected { get; set; }
    public long Conflicts { get; set; }

    /// <summary>Changed keys per local table (capped per table).</summary>
    public Dictionary<string, List<string>> ChangedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public long Total => Upserts + Deletes + MoveOuts;

    public void Merge(G9CApplyReport other)
    {
        ArgumentNullException.ThrowIfNull(other);
        Upserts += other.Upserts;
        Deletes += other.Deletes;
        MoveOuts += other.MoveOuts;
        MembershipAdded += other.MembershipAdded;
        SkippedDirty += other.SkippedDirty;
        Accepted += other.Accepted;
        Rejected += other.Rejected;
        Conflicts += other.Conflicts;
        foreach (var (table, keys) in other.ChangedKeys)
        {
            if (!ChangedKeys.TryGetValue(table, out var list)) ChangedKeys[table] = list = [];
            list.AddRange(keys);
        }
    }
}

/// <summary>A local row as seen by reconciliation.</summary>
public sealed record G9CLocalRowEntry(string Key, int Bucket, long Version, long Hash, bool Member, bool Dirty);

public sealed record G9CPendingTable(string LocalTable, int Upserts, int Deletes, int Rejected);

public sealed class G9CPendingSummary
{
    public int TotalRows { get; init; }
    public int RejectedRows { get; init; }
    public IReadOnlyList<G9CPendingTable> Tables { get; init; } = [];
}

/// <summary>One entry of the local change feed (downloaded and local changes alike).</summary>
public sealed record G9CChangeFeedEntry(long Sequence, string Key, bool Deleted, bool LocalPending);

public sealed class G9CSyncRejection
{
    public required string LocalTable { get; init; }
    public required string Key { get; init; }
    public required string Code { get; init; }
    public string? Message { get; init; }
    public string? SqlState { get; init; }
    public string? Constraint { get; init; }
    public string? Column { get; init; }
    public string? Details { get; init; }
    public DateTime AtUtc { get; init; }
}

/// <summary>A persisted summary of one run (feeds diagnostics and learned progress weights).</summary>
public sealed class G9CSyncRunReport
{
    public Guid RunId { get; set; }
    public DateTime StartedUtc { get; set; }
    public long DurationMs { get; set; }
    public string Outcome { get; set; } = string.Empty;
    public string Priority { get; set; } = string.Empty;
    public string? ErrorCode { get; set; }
    public string? ErrorMessage { get; set; }
    public int Pushed { get; set; }
    public int Rejected { get; set; }
    public int Conflicts { get; set; }
    public long Downloaded { get; set; }
    public long Applied { get; set; }
    public long DownloadedBytes { get; set; }
    public List<string> Subscriptions { get; set; } = [];
    public Dictionary<string, long> PhaseMs { get; set; } = new(StringComparer.Ordinal);
}
