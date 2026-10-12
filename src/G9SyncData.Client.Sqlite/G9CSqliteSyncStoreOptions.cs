namespace G9SyncData.Client.Sqlite;

/// <summary>How <see cref="G9ESyncColumnType.DateTime" /> values are stored locally.</summary>
public enum G9ESqliteDateTimeStorage : byte
{
    /// <summary>ISO-like text using <see cref="G9CSqliteSyncStoreOptions.DateTimeFormat" /> (sqlite-net's text format by default).</summary>
    Text = 0,

    /// <summary>.NET ticks as INTEGER (sqlite-net <c>storeDateTimeAsTicks: true</c>).</summary>
    Ticks = 1
}

public enum G9ESqliteDecimalStorage : byte
{
    /// <summary>REAL, like sqlite-net.</summary>
    Real = 0,

    /// <summary>Invariant text, exact.</summary>
    Text = 1
}

public sealed class G9CSqliteSyncStoreOptions
{
    /// <summary>Database file path. Ignored when <see cref="ConnectionString" /> is set.</summary>
    public string? DatabasePath { get; set; }

    /// <summary>A full Microsoft.Data.Sqlite connection string.</summary>
    public string? ConnectionString { get; set; }

    public G9ESqliteDateTimeStorage DateTimeStorage { get; set; } = G9ESqliteDateTimeStorage.Text;

    /// <summary>Text format for DateTime values (default matches sqlite-net).</summary>
    public string DateTimeFormat { get; set; } = "yyyy-MM-dd'T'HH:mm:ss.fffffff";

    public G9ESqliteDecimalStorage DecimalStorage { get; set; } = G9ESqliteDecimalStorage.Real;

    /// <summary>
    ///     A file OUTSIDE the platform's backup set that records which installation this database belongs to (ADR-0067): on Android a
    ///     path under <c>Context.NoBackupFilesDir</c>, on iOS a file with <c>NSURLIsExcludedFromBackupKey</c>. One per database file. When
    ///     the database names a different incarnation than the marker, it was restored from a backup or copied from another device, and
    ///     the store gives it a new client id so two histories never share one identity (pending rows are kept and upload normally).
    ///     Null (default) turns the check off.
    /// </summary>
    public string? IncarnationMarkerPath { get; set; }

    /// <summary>How long a writer waits for the database lock (the app may be writing).</summary>
    public int BusyTimeoutMilliseconds { get; set; } = 30_000;

    public bool UseWriteAheadLog { get; set; } = true;

    /// <summary>
    ///     What happens to a row no subscription holds any more (ADR-0026). Default: deleted, or quarantined when it has pending local
    ///     changes.
    /// </summary>
    public Storage.G9CRevocationPolicy Revocation { get; set; } = Storage.G9CRevocationPolicy.Default;

    /// <summary>
    ///     The largest decoded body the store accepts from one frame it applies (ADR-0051, ADR-0066): the length comes from the frame's
    ///     own header and is what gets allocated. Default 64 MiB.
    /// </summary>
    public int MaxDecodedFrameBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Changed keys reported per table in an apply report (RowsApplied event).</summary>
    public int MaxChangedKeysPerTable { get; set; } = 5_000;

    /// <summary>
    ///     Applies downloaded rows with trigger execution switched off on the store's own connection (<c>SQLITE_DBCONFIG_ENABLE_TRIGGER</c>,
    ///     ADR-0034). The capture triggers do nothing during an apply anyway; merely evaluating them made a 500 k-row load 2.6x slower.
    ///     Nothing in the schema changes and the app's connections keep their triggers. Never used while the database holds a trigger
    ///     the library did not create: the app's own triggers keep firing for synced rows. Default true.
    /// </summary>
    public bool SkipTriggersDuringApply { get; set; } = true;

    /// <summary>
    ///     A download that fills an EMPTY table with at least this many rows drops the table's plain (non-unique) indexes first and builds
    ///     each once after the rows are in, inside the same transaction (ADR-0034): one sorted build instead of a random insert per row,
    ///     1.8x faster at 500 k rows. A crash rolls the indexes back with everything else. 0 turns it off.
    /// </summary>
    public int RebuildIndexesAfterRows { get; set; } = 20_000;

    /// <summary>
    ///     For a table that ALREADY holds rows: drop and rebuild its plain indexes anyway when the snapshot brings at least this many
    ///     times the rows already there (and at least <see cref="RebuildIndexesAfterRows" />). 0 = never for a table that is not empty.
    ///     Default 1 (ADR-0058).
    /// </summary>
    /// <remarks>
    ///     Measured with 500,000 rows present: live index maintenance won at 5 %, 10 % and 30 % incoming rows - by about 2x, because a
    ///     rebuild sorts the rows that were already there as well - and the rebuild only came out ahead (4.5 s against 5.9 s) once the
    ///     incoming load was as large as the table. A device's flash favours the rebuild more than the desktop these numbers are from,
    ///     so 1 is the conservative end; do not lower it without a measurement on the device.
    /// </remarks>
    public double RebuildIndexesMinimumGrowth { get; set; } = 1d;

    /// <summary>
    ///     Rows per committed slice of a staged pull, or 0 to apply the whole pull in one transaction (the default).
    /// </summary>
    /// <remarks>
    ///     <para>A staged pull normally lands in a single transaction: nothing is visible until every row is in, which on a large
    ///     site means a long wait and then everything at once. Set this and the apply commits every <c>n</c> rows instead, announcing
    ///     each slice, so the application can show rows as they arrive (ADR-0053).</para>
    ///     <para>What this does NOT give up: subscription cursors still advance only when the last slice has landed, so a run
    ///     interrupted halfway has applied real rows but not moved its cursor - the next run pulls the same range again and the
    ///     upserts are idempotent. A partially applied pull converges; it does not corrupt.</para>
    ///     <para>What it costs: <see cref="RebuildIndexesAfterRows" /> is ignored while slicing, because dropping an index for the
    ///     load would leave it missing between commits, exactly when the application is reading the rows it was shown. Large first
    ///     syncs are therefore somewhat slower than the atomic path; deltas are unaffected.</para>
    /// </remarks>
    public int ApplySliceRows { get; set; }

    /// <summary>
    ///     The largest slice a sliced apply grows to: each slice is twice the one before, from <see cref="ApplySliceRows" /> up to
    ///     this. 0 keeps every slice at <see cref="ApplySliceRows" />. Default 32,000 (ADR-0058).
    /// </summary>
    /// <remarks>
    ///     A commit is not free: 500,000 rows in 2,000-row commits measured 2.3x slower than one transaction with the same live
    ///     indexes, and 20,000-row commits 1.5x. The first slices are what the application is waiting to see, so they stay small; the
    ///     rows behind them arrive in ever larger commits, and a 100,000-row delta costs a handful of commits instead of fifty.
    /// </remarks>
    public int ApplySliceRowsMax { get; set; } = 32_000;

    /// <summary>
    ///     After an apply of at least this many rows the store runs <c>PRAGMA optimize</c> (so SQLite's planner has statistics for the
    ///     tables that just changed size) and attempts one <c>wal_checkpoint(TRUNCATE)</c>. Both are best effort: a failure is logged
    ///     and ignored. 0 turns both off. Default 10,000 (ADR-0058).
    /// </summary>
    /// <remarks>
    ///     The checkpoint matters when the application reads while a sliced apply commits: measured, a reader that is always active
    ///     starves the automatic checkpoint and the WAL grew from 12 MB to 492 MB. The attempt uses a short busy timeout and gives up
    ///     rather than wait for a reader.
    /// </remarks>
    public int MaintenanceAfterRows { get; set; } = 10_000;

    /// <summary>
    ///     In WAL mode, transactions that write only server-derived data (staged download pages and applying them) commit with
    ///     <c>synchronous=NORMAL</c> (ADR-0034). A power loss can undo the last such commits but never corrupts the database, and an undone
    ///     commit is downloaded or applied again - its cursor is in the same transaction. Local writes (edits, commands) keep the
    ///     connection's own setting. Default true.
    /// </summary>
    public bool SynchronousNormalForDownloads { get; set; } = true;

    /// <summary>
    ///     <c>journal_size_limit</c> for the store's connections: after a checkpoint the WAL file is truncated to this size instead of
    ///     keeping the size of the largest transaction (a 500 k-row initial sync left a 200 MB WAL on disk). Negative = no limit.
    /// </summary>
    public long WalSizeLimitBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    ///     Accept a large first download as a SQLite snapshot file (ADR-0072): the server writes the subscription's rows into a file with
    ///     this store's own DDL, and the apply copies the file's b-trees instead of inserting rows one by one. Default true; a server
    ///     without snapshot files ignores the offer and sends rows.
    /// </summary>
    public bool AcceptSnapshotFiles { get; set; } = true;

    /// <summary>A subscription is sent as a file only when its snapshot is at least this many rows; 0 leaves the threshold to the server.</summary>
    public long SnapshotFileMinRows { get; set; }

    /// <summary>The largest snapshot file accepted, uncompressed. Larger subscriptions come as rows. Default 2 GiB.</summary>
    public long SnapshotFileMaxBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>
    ///     Where downloaded snapshot files are staged until the apply copies them; null = a <c>.g9files</c> directory beside the database.
    ///     Use a non-backed-up location (a cache directory). Files are deleted when their pull is applied or discarded.
    /// </summary>
    public string? SnapshotFileDirectory { get; set; }

    /// <summary>
    ///     Bytes of a snapshot file written between two durable resume points (fsync of the file, then the stage row): a download
    ///     interrupted mid-file resumes from the last one. Default 8 MiB.
    /// </summary>
    public int SnapshotFileCheckpointBytes { get; set; } = 8 * 1024 * 1024;

    internal string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return ConnectionString;
        if (string.IsNullOrWhiteSpace(DatabasePath)) throw new InvalidOperationException("Set DatabasePath or ConnectionString.");
        return "Data Source=" + DatabasePath + ";Pooling=True";
    }
}
