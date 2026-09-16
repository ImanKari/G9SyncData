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

    /// <summary>Changed keys reported per table in an apply report (RowsApplied event).</summary>
    public int MaxChangedKeysPerTable { get; set; } = 5_000;

    internal string BuildConnectionString()
    {
        if (!string.IsNullOrWhiteSpace(ConnectionString)) return ConnectionString;
        if (string.IsNullOrWhiteSpace(DatabasePath)) throw new InvalidOperationException("Set DatabasePath or ConnectionString.");
        return "Data Source=" + DatabasePath + ";Pooling=True";
    }
}
