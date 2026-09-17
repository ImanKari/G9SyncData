using SQLitePCL;

namespace G9SyncData.Client.Sqlite;

/// <summary>
///     A SQLite error raised by the store, with SQLite's primary and extended result codes (for example 5 / <c>SQLITE_BUSY</c> when the
///     app holds the write lock longer than the busy timeout). It replaces Microsoft.Data.Sqlite's <c>SqliteException</c> (ADR-0032).
/// </summary>
public sealed class G9CSqliteException : Exception
{
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
