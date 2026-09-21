using SQLitePCL;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     A SQLite error raised by the store, with SQLite's primary and extended result codes (for example 5 / <c>SQLITE_BUSY</c> when the
///     app holds the write lock longer than the busy timeout). It replaces Microsoft.Data.Sqlite's <c>SqliteException</c> (ADR-0032).
/// </summary>
public sealed class G9CSqliteException : Exception, Storage.IG9SyncLocalStoreFailure
{
    /// <summary>
    ///     SQLite's result code in the engine's terms (ADR-0068): <c>SQLITE_CORRUPT</c> (11) and <c>SQLITE_NOTADB</c> (26) are a damaged
    ///     file, <c>SQLITE_FULL</c> (13) is a full disk, <c>SQLITE_BUSY</c> (5) and <c>SQLITE_LOCKED</c> (6) are a lock that was not released
    ///     in time.
    /// </summary>
    public Storage.G9ELocalStoreFailure Failure => SqliteErrorCode switch
    {
        11 or 26 => Storage.G9ELocalStoreFailure.Corrupt,
        13 => Storage.G9ELocalStoreFailure.DiskFull,
        5 or 6 => Storage.G9ELocalStoreFailure.Busy,
        _ => Storage.G9ELocalStoreFailure.Other
    };

    public G9CSqliteException()
    {
    }

    public G9CSqliteException(string message)
        : base(message)
    {
    }

    public G9CSqliteException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public G9CSqliteException(string message, int errorCode, int extendedErrorCode)
        : base(message)
    {
        SqliteErrorCode = errorCode;
        SqliteExtendedErrorCode = extendedErrorCode;
    }

    /// <summary>SQLite's primary result code (<c>SQLITE_BUSY</c> = 5, <c>SQLITE_CONSTRAINT</c> = 19, …).</summary>
    public int SqliteErrorCode { get; }

    /// <summary>SQLite's extended result code (<c>SQLITE_CONSTRAINT_UNIQUE</c> = 2067, …).</summary>
    public int SqliteExtendedErrorCode { get; }

    internal static void Check(sqlite3 db, int rc, string what)
    {
        if (rc is raw.SQLITE_OK or raw.SQLITE_ROW or raw.SQLITE_DONE) return;
        throw new G9CSqliteException($"SQLite error {rc} during {what}: {raw.sqlite3_errmsg(db).utf8_to_string()}", rc, raw.sqlite3_extended_errcode(db));
    }
}
