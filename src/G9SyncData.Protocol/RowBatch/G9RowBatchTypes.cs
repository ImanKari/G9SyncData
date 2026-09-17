namespace G9SyncData.Protocol.RowBatch;

/// <summary>What the rows of a G9RowBatch section mean.</summary>
public enum G9ERowBatchOp : byte
{
    /// <summary>Key, version, full negotiated column values. Insert or update.</summary>
    Upsert = 1,

    /// <summary>Key and version of a deleted row.</summary>
    Tombstone = 2,

    /// <summary>Key and version of a row that left the chunk's subscription (still exists on the server).</summary>
    MoveOut = 3,

    /// <summary>Key, version and row hash (reconciliation listing).</summary>
    KeyVersions = 4,

    /// <summary>Client upload rows: key, op, local sequence, base version, dirty mask, HLC ms, values for upserts.</summary>
    Push = 5,

    /// <summary>Key, version and ONLY the backfilled columns of rows the client already holds.</summary>
    Backfill = 6
}

/// <summary>Operation of a push row.</summary>
public enum G9EPushRowOp : byte
{
    Upsert = 1,
    Delete = 2
}

/// <summary>Frame body compression.</summary>
public enum G9ECompression : byte
{
    None = 0,
    Brotli = 1
}

internal static class G9RowBatchFormat
{
    /// <summary>Row-wise body: each row's fields in turn.</summary>
    public const byte Version = 1;

    /// <summary>Column-grouped body (<see cref="G9RowBatchColumnLayout" />, ADR-0040). Same fields, same encodings, grouped by field.</summary>
    public const byte ColumnVersion = 2;

    /// <summary>The newest layout this build reads and writes.</summary>
    public const int MaxVersion = ColumnVersion;
    public const int HeaderSize = 10;
    public const int MaxBodyBytes = 512 * 1024 * 1024;

    /// <summary>
    ///     The largest body any reader will decode: what a writer may produce, plus the column layout's allowance. It is the ceiling of
    ///     the format, not a budget - a reader handling frames it did not produce passes its own, smaller one (ADR-0051).
    /// </summary>
    public const int MaxDecodedBytes = MaxBodyBytes + (64 * 1024 * 1024);

    public static bool HasValues(G9ERowBatchOp op) => op is G9ERowBatchOp.Upsert or G9ERowBatchOp.Backfill;
}
