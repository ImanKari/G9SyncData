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

    /// <summary>
    ///     Releases the rows a subscription holds because the server refused it for this caller, following the move-out policy last
    ///     announced for it (ADR-0048). The subscription itself stays, cursor cleared, so regaining access refills it; rows another
    ///     subscription still holds are untouched.
    /// </summary>
    Task<G9CApplyReport> ReleaseSubscriptionRowsAsync(string key, CancellationToken cancellationToken);

    Task UpdateSubscriptionAsync(string key, G9CSubscriptionUpdate update, CancellationToken cancellationToken);

    /// <summary>Deletes ephemeral subscriptions unused longer than their TTL. Returns how many were removed.</summary>
    Task<int> CollectExpiredEphemeralAsync(DateTime utcNow, CancellationToken cancellationToken);

    // ------------------------------------------------ push

    /// <summary>Builds the next push batch from dirty rows in local change order, or null when nothing is pending. Reuses an in-flight batch id.</summary>
    /// <summary>
    ///     The next batch of pending changes, oldest first: at most <paramref name="maxRows" /> rows and about <paramref name="maxBytes" />
    ///     encoded bytes (the server's announced limits). A row that alone exceeds <paramref name="maxBytes" /> is rejected locally
    ///     (<c>G9S-REJ-TOO-LARGE</c>) instead of blocking every later push.
    /// </summary>
    Task<G9CPushBatch?> BuildPushBatchAsync(IReadOnlyList<G9DtNegotiatedTable> tables, int maxRows, long maxBytes, CancellationToken cancellationToken);

    /// <summary>
    ///     Removes rows of an UPLOAD-ONLY table from the device without uploading a delete: uploaded rows (no pending change) only, all of
    ///     them or the given keys. The way to free space for data the server keeps, such as readings or logs.
    /// </summary>
    Task<int> ForgetUploadedRowsAsync(string localTable, IReadOnlyCollection<string>? keys, CancellationToken cancellationToken);

    Task<G9CApplyReport> ApplyPushResultAsync(G9CPushBatch batch, G9DtPushResult result, CancellationToken cancellationToken);

    // ------------------------------------------------ pull

    /// <summary>The newest staged pull, whatever it was planned for.</summary>
    Task<G9CStagedPull?> GetStagedPullAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     The newest staged pull planned under exactly this generation, or null (ADR-0060). A store keeps a stage per generation, so a
    ///     download interrupted for one set of subscriptions is still there after a run for a different set has come and gone.
    /// </summary>
    async Task<G9CStagedPull?> GetStagedPullAsync(string generation, CancellationToken cancellationToken) =>
        await GetStagedPullAsync(cancellationToken).ConfigureAwait(false) is { } staged && string.Equals(staged.Generation, generation, StringComparison.Ordinal)
            ? staged
            : null;

    /// <summary>
    ///     Starts a stage. A stage of the SAME generation is replaced; stages of other generations are left alone - they belong to other
    ///     subscription sets and may still be resumed (ADR-0060).
    /// </summary>
    Task BeginStagedPullAsync(G9CStagedPull pull, CancellationToken cancellationToken);

    /// <summary>
    ///     Removes stages created before <paramref name="olderThanUtc" />, then the oldest remaining ones until the stages other than
    ///     <paramref name="keepGeneration" />'s hold at most <paramref name="maxBytes" /> of payload. Returns how many were removed.
    ///     The kept generation is only ever removed for its age: a stage larger than the cap must still be resumable.
    /// </summary>
    Task<int> PruneStagedPullsAsync(DateTime olderThanUtc, long maxBytes, string? keepGeneration, CancellationToken cancellationToken) => Task.FromResult(0);

    /// <summary>Removes every staged pull. Returns how many there were.</summary>
    async Task<int> DiscardAllStagedPullsAsync(CancellationToken cancellationToken)
    {
        var count = 0;
        while (await GetStagedPullAsync(cancellationToken).ConfigureAwait(false) is { } staged)
        {
            await DiscardStagedPullAsync(staged.RunId, cancellationToken).ConfigureAwait(false);
            count++;
        }

        return count;
    }

    Task StageChunkAsync(string runId, G9DtSyncChunk chunk, CancellationToken cancellationToken);

    /// <summary>
    ///     The store's offer to receive large first downloads as SQLite snapshot files (ADR-0072): how it stores values and the shape of
    ///     every table it receives. Null (the default) = rows only. A store that returns an offer must stage the file segments
    ///     (<see cref="G9DtSyncChunk.File" />) in <see cref="StageChunkAsync" /> and copy the files in <see cref="ApplyStagedPullAsync" />.
    /// </summary>
    G9DtSnapshotFileRequest? DescribeSnapshotFiles(IReadOnlyList<G9DtNegotiatedTable> negotiated) => null;

    /// <summary>The last safe resume point of a staged run; chunks staged after it are discarded.</summary>
    Task<G9CResumePoint> GetResumePointAsync(string runId, CancellationToken cancellationToken);

    /// <summary>
    ///     Applies a staged pull. With <c>ApplySliceRows</c> set the apply commits in slices and reports each one through
    ///     <paramref name="slices" /> as it lands, so the application can show rows while the rest are still arriving (ADR-0053);
    ///     otherwise the whole pull lands in one transaction and one slice is reported at the end.
    /// </summary>
    Task<G9CApplyReport> ApplyStagedPullAsync(string runId, IProgress<long>? rowsApplied, IProgress<G9CApplySlice>? slices,
        CancellationToken cancellationToken);

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

    /// <summary>
    ///     Records a command and, when <paramref name="applyLocally" /> is given, writes its local effects in the same transaction
    ///     (ADR-0025). If <paramref name="applyLocally" /> throws, nothing is recorded.
    /// </summary>
    Task<G9CLocalCommand> EnqueueCommandAsync(G9CCommandRequest request, Func<IG9LocalCommandWriter, CancellationToken, Task>? applyLocally,
        CancellationToken cancellationToken);

    /// <summary>Rows in the holding area (ADR-0026).</summary>
    Task<IReadOnlyList<G9CQuarantinedRow>> GetQuarantinedAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     Puts a quarantined row back into its table as a pending local change (every column dirty), so it uploads on the next push —
    ///     useful once access is restored. Returns 1 when restored.
    /// </summary>
    Task<int> RestoreQuarantinedAsync(string localTable, string key, CancellationToken cancellationToken);

    /// <summary>Discards quarantined rows (all when both arguments are null).</summary>
    Task<int> DiscardQuarantinedAsync(string? localTable, string? key, CancellationToken cancellationToken);

    /// <summary>Rows kept in their table after revocation, with the rule that kept them.</summary>
    Task<IReadOnlyList<G9CRetainedRow>> GetRetainedAsync(CancellationToken cancellationToken);

    /// <summary>Re-checks rows retained by a <c>RetainWhile</c> rule and deletes those no longer retained. Returns the number deleted.</summary>
    Task<int> ReevaluateRetainedAsync(CancellationToken cancellationToken);

    /// <summary>Recorded commands, newest first; filtered by state when given.</summary>
    Task<IReadOnlyList<G9CLocalCommand>> GetCommandsAsync(G9ELocalCommandState? state, int limit, CancellationToken cancellationToken);

    /// <summary>Change-feed entries for one table after a sequence, oldest first.</summary>
    /// <remarks>
    ///     The sequence is the store's own clock and is shared by every table, so ONE watermark covers a projection built from
    ///     several of them.
    /// </remarks>
    Task<IReadOnlyList<G9CChangeFeedEntry>> ReadChangeFeedAsync(string localTable, long afterSequence, int limit, CancellationToken cancellationToken);

    /// <summary>
    ///     Change-feed entries for one table after a sequence, oldest first, for the keys <paramref name="keyFilter" /> selects
    ///     and every deletion.
    /// </summary>
    /// <remarks>
    ///     A read that returns fewer than <paramref name="limit" /> entries is complete: no entry of a selected key exists between
    ///     the last one returned and the store's sequence at the time of the read, so a projection may move its watermark there.
    /// </remarks>
    Task<IReadOnlyList<G9CChangeFeedEntry>> ReadChangeFeedAsync(string localTable, long afterSequence, int limit,
        G9CChangeFeedKeyFilter keyFilter, CancellationToken cancellationToken);

    /// <summary>The store's current change sequence: the highest value <see cref="ReadChangeFeedAsync(string, long, int, CancellationToken)" /> can return today.</summary>
    /// <remarks>
    ///     A consumer that keeps a derived projection (a cache, a map layer, a search index) holds a watermark into the feed, and
    ///     that watermark is only meaningful against the store it came from. Compare it with this: a watermark ABOVE the current
    ///     sequence means the store was rebuilt underneath the projection - a reinstall, a reset, a restore - and the projection has
    ///     to be rebuilt rather than resumed. Without it the consumer would sit above every row the feed will ever return and go
    ///     silently, permanently stale.
    /// </remarks>
    Task<long> GetChangeSequenceAsync(CancellationToken cancellationToken);

    /// <summary>
    ///     The highest sequence <see cref="PurgeDeletedFeedAsync" /> has ever removed from the feed; 0 when nothing was purged (ADR-0059).
    /// </summary>
    /// <remarks>
    ///     The feed keeps only the newest deletions. A consumer whose watermark is BELOW this floor has missed deletions it can no longer
    ///     read - it was offline, or its scope was not opened, while more than the kept number piled up - and resuming would leave it
    ///     holding rows that are gone. It has to rebuild its projection instead. A watermark at or above the floor is safe to resume from.
    ///     A store that does not purge returns 0.
    /// </remarks>
    Task<long> GetChangeFeedFloorAsync(CancellationToken cancellationToken) => Task.FromResult(0L);

    Task<IReadOnlyList<G9CSyncRejection>> GetRejectionsAsync(CancellationToken cancellationToken);

    /// <summary>Clears rejection flags so the rows are pushed again. Null arguments widen the scope.</summary>
    Task<int> RetryRejectedAsync(string? localTable, string? key, CancellationToken cancellationToken);

    /// <summary>
    ///     Gives up the local change of rejected rows and takes the server's copy instead (LES-0070). A row the server never had
    ///     (<c>server_ver = 0</c> and held by no subscription) is removed locally; any other drops its pending change and gets
    ///     <c>server_ver = -1</c>, a version no server issues, and the subscriptions holding it become
    ///     <see cref="G9ESubscriptionState.NeedsReconcile" />: their next sync fetches the server's copy. Nothing is uploaded either
    ///     way. Null arguments widen the scope.
    /// </summary>
    Task<int> RevertRejectedAsync(string? localTable, string? key, CancellationToken cancellationToken);

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

    /// <summary>The move-out policy the server last announced for this subscription; kept so it still applies once access is gone.</summary>
    public G9EMoveOutPolicy? MoveOutPolicy { get; init; }

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

    /// <summary>True when the byte budget ended the batch early: more pending rows follow.</summary>
    public bool Truncated { get; init; }

    /// <summary>Rows rejected on the device while building the batch (<c>G9S-REJ-TOO-LARGE</c>); they are not in the batch.</summary>
    public int RejectedLocally { get; init; }

    /// <summary>Commands carried by this batch, in device order (ADR-0025).</summary>
    public IReadOnlyList<G9DtCommand> Commands { get; init; } = [];
}

/// <summary>A pull being staged (persisted, survives app restarts).</summary>
public sealed class G9CStagedPull
{
    public required string RunId { get; init; }
    public required G9DtPullPlan Plan { get; init; }
    public required DateTime CreatedUtc { get; init; }

    /// <summary>The backfills requested with the plan: they give backfill sections their column order and are marked synced on apply.</summary>
    public IReadOnlyList<G9DtBackfillRequest> Backfills { get; init; } = [];

    /// <summary>
    ///     What the world looked like when the plan was made - server epoch, subscription definition and authorisation hashes, negotiated
    ///     columns (ADR-0046). A run only resumes a stage whose generation still matches; otherwise the stage describes a world that no
    ///     longer exists and applying it would undo whatever replaced it.
    /// </summary>
    public string? Generation { get; init; }

    /// <summary>
    ///     The cursor each planned subscription had when the plan was made (null for a first pull), by subscription key (ADR-0060).
    ///     Stages outlive runs for other subscription sets, and one of those can include a subscription of this stage - a dependency
    ///     usually - and move its cursor on. The apply then leaves that cursor where it is: it only moves a cursor that is still the one
    ///     the plan started from. A subscription missing from the map (a stage written before it existed) is moved as before.
    /// </summary>
    public IReadOnlyDictionary<string, string?> PlannedFromCursors { get; init; } = new Dictionary<string, string?>(StringComparer.Ordinal);

    /// <summary>
    ///     The device clock at the moment the stage began, assigned by the store; the value given here is ignored. Rows changed after it
    ///     are newer than anything the stage carries, so the apply leaves them alone.
    /// </summary>
    public long StageSequence { get; init; }
}

/// <summary>
///     One committed slice of a staged pull. The rows it names are in the database and readable now; the pull as a whole may still
///     have slices to come, and its cursors do not move until the last one lands.
/// </summary>
/// <param name="Report">What this slice changed, including the keys, so a projection can refresh only those rows.</param>
/// <param name="RowsApplied">Rows applied by this slice.</param>
/// <param name="RowsAppliedTotal">Rows applied by this run so far, this slice included.</param>
/// <param name="RowsPlanned">Rows the plan expects in total, for a progress fraction.</param>
/// <param name="IsFinal">Whether this was the last slice: cursors have advanced and the stage is gone.</param>
public sealed record G9CApplySlice(G9CApplyReport Report, long RowsApplied, long RowsAppliedTotal, long RowsPlanned, bool IsFinal);

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

    /// <summary>
    ///     Conflicts the device LOST that were kept on it as rejections (LES-0072): the server kept its own values for some of the device's
    ///     changed columns (or the whole row), so the device's values were put back over the canonical row and flagged, never silently replaced.
    /// </summary>
    public long ConflictsKept { get; set; }

    /// <summary>Commands whose server result was applied.</summary>
    public long CommandsCompleted { get; set; }

    /// <summary>Rows no subscription held any more that were deleted (ADR-0026).</summary>
    public long RevokedDeleted { get; set; }

    /// <summary>Rows moved to the holding area.</summary>
    public long RevokedQuarantined { get; set; }

    /// <summary>Rows kept in their table (marked or retained).</summary>
    public long RevokedRetained { get; set; }

    /// <summary>Changed keys per local table (capped per table).</summary>
    public Dictionary<string, List<string>> ChangedKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Upserts whose row already held exactly the incoming values (a range pulled again after an interruption, a reconciliation
    ///     fetch of a row that was right all along). They count in <see cref="Upserts" /> - the row was delivered - but nothing was
    ///     written for them, they are not in <see cref="ChangedKeys" />, and the change feed does not announce them (ADR-0058).
    /// </summary>
    public long UnchangedRows { get; set; }

    /// <summary>How the apply ran: which large-download optimisations engaged and what they cost (ADR-0058).</summary>
    public G9CApplyDiagnostics Diagnostics { get; } = new();

    public long Total => Upserts + Deletes + MoveOuts;

    public void Merge(G9CApplyReport other)
    {
        ArgumentNullException.ThrowIfNull(other);
        UnchangedRows += other.UnchangedRows;
        Diagnostics.Merge(other.Diagnostics);
        Upserts += other.Upserts;
        Deletes += other.Deletes;
        MoveOuts += other.MoveOuts;
        MembershipAdded += other.MembershipAdded;
        SkippedDirty += other.SkippedDirty;
        Accepted += other.Accepted;
        Rejected += other.Rejected;
        Conflicts += other.Conflicts;
        ConflictsKept += other.ConflictsKept;
        CommandsCompleted += other.CommandsCompleted;
        RevokedDeleted += other.RevokedDeleted;
        RevokedQuarantined += other.RevokedQuarantined;
        RevokedRetained += other.RevokedRetained;
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

    /// <summary>Commands recorded on the device and not yet executed by the server.</summary>
    public int PendingCommands { get; init; }
    public IReadOnlyList<G9CPendingTable> Tables { get; init; } = [];
}

/// <summary>One entry of the local change feed (downloaded and local changes alike).</summary>
public sealed record G9CChangeFeedEntry(long Sequence, string Key, bool Deleted, bool LocalPending);

/// <summary>
///     Narrows a change-feed read to the keys a query of the app's own tables selects - "the rows of the site I am projecting" -
///     so the entries of everything else are dropped inside the database instead of being read, allocated and thrown away.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it exists.</b> The feed is one clock over every table and every subscription, so a projection of ONE site reads
///         the whole feed since its watermark: an app that kept a map of one farm walked 460,000 entries of another farm, in passes
///         of 100,000 a table, to learn that its own farm had not changed (AgriPad, 2026-10-16: ~5 s of CPU and a GC every 50 ms
///         on an emulator, several times that on a phone).
///     </para>
///     <para>
///         <b>Deletions are always returned</b>, whatever the query says: a deleted row is gone from its table, so no query of
///         the app's tables can say which site it belonged to, and a projection that missed it would keep showing it.
///     </para>
///     <para>
///         <see cref="KeySql" /> is a single <c>SELECT</c> returning one column of keys, run in the store's database; it is
///         embedded as <c>pk IN (...)</c>. Its parameters are bound by name (<c>$name</c>, <c>@name</c> or <c>:name</c>); the
///         names <c>$g9t</c>, <c>$g9a</c> and <c>$g9l</c> belong to the read itself.
///     </para>
/// </remarks>
/// <param name="KeySql">A <c>SELECT</c> of one column: the keys whose entries the read returns.</param>
/// <param name="Parameters">The query's named parameters.</param>
public sealed record G9CChangeFeedKeyFilter(string KeySql, IReadOnlyDictionary<string, object?>? Parameters = null)
{
    private static readonly string[] ReservedNames = ["$g9t", "$g9a", "$g9l"];

    /// <summary>Throws when the filter cannot be embedded safely: not a single SELECT, or a parameter the read owns.</summary>
    public void Validate()
    {
        var sql = KeySql?.Trim() ?? string.Empty;
        if (!sql.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase) || sql.Contains(';', StringComparison.Ordinal))
        {
            throw new ArgumentException("A change-feed key filter is a single SELECT statement of one column.", nameof(KeySql));
        }

        foreach (var name in Parameters?.Keys ?? [])
        {
            if (name.Length < 2 || name[0] is not ('$' or '@' or ':'))
            {
                throw new ArgumentException($"Change-feed key filter parameter '{name}' must be named ($name, @name or :name).", nameof(Parameters));
            }

            if (ReservedNames.Any(reserved => string.Equals(reserved, name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ArgumentException($"Change-feed key filter parameter '{name}' is reserved by the read.", nameof(Parameters));
            }
        }
    }
}

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

    /// <summary>Whether the run's applies ran with trigger execution off; null when the run applied nothing (ADR-0058).</summary>
    public bool? TriggersSkipped { get; set; }

    /// <summary>Triggers the library did not create that kept trigger execution on.</summary>
    public List<string> ForeignTriggers { get; set; } = [];

    /// <summary>Apply transactions of the run (slices; 1 for an atomic apply).</summary>
    public int ApplyTransactions { get; set; }

    /// <summary>Plain indexes dropped for a load and rebuilt after it.</summary>
    public int IndexesDeferred { get; set; }

    public long IndexRebuildMs { get; set; }

    /// <summary>Tables large enough for index deferral that kept live indexes, with the reason.</summary>
    public Dictionary<string, string> LiveIndexReasons { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Upserts that found the row already holding the incoming values.</summary>
    public long UnchangedRows { get; set; }
}
