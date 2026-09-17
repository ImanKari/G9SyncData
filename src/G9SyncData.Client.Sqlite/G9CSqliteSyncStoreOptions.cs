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

    /// <summary>How long a writer waits for the database lock (the app may be writing).</summary>
    public int BusyTimeoutMilliseconds { get; set; } = 30_000;

    public bool UseWriteAheadLog { get; set; } = true;

    /// <summary>
    ///     What happens to a row no subscription holds any more (ADR-0026). Default: deleted, or quarantined when it has pending local
    ///     changes.
    /// </summary>
    public Storage.G9CRevocationPolicy Revocation { get; set; } = Storage.G9CRevocationPolicy.Default;

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

    internal string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return ConnectionString;
        if (string.IsNullOrWhiteSpace(DatabasePath)) throw new InvalidOperationException("Set DatabasePath or ConnectionString.");
        return "Data Source=" + DatabasePath + ";Pooling=True";
    }
}
