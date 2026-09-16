namespace G9SyncData;

/// <summary>
///     The portable column types G9SyncData carries between providers. Every provider maps these to its own
///     storage types; the wire format (see <c>G9CRowBatchWriter</c>) encodes exactly these.
/// </summary>
public enum G9ESyncColumnType : byte
{
    /// <summary>Not specified. Only valid on attributes, where it means "infer from the property type".</summary>
    Unspecified = 0,
    Boolean = 1,
    Int32 = 2,
    Int64 = 3,
    Double = 4,
    Decimal = 5,
    String = 6,
    Guid = 7,
    DateTime = 8,
    DateTimeOffset = 9,
    TimeSpan = 10,
    Bytes = 11
}

/// <summary>Which way rows of a table may travel.</summary>
public enum G9ESyncDirection : byte
{
    /// <summary>Server changes download, client changes upload.</summary>
    Bidirectional = 0,

    /// <summary>Server changes download; the client never uploads (no capture triggers are installed on the client).</summary>
    DownloadOnly = 1,

    /// <summary>The client uploads; the server never sends rows back except as push results.</summary>
    UploadOnly = 2
}

/// <summary>How the server resolves an uploaded row that the server changed after the client last saw it.</summary>
public enum G9EConflictPolicy : byte
{
    /// <summary>The server keeps its row, the conflict is reported and the canonical row is returned. Default.</summary>
    ServerWins = 0,

    /// <summary>The client's changed columns are applied regardless.</summary>
    ClientWins = 1,

    /// <summary>
    ///     The client's changed columns that the server did NOT change since the client's base version are applied;
    ///     overlapping columns keep the server value and are reported. Requires per-column versions on the server.
    /// </summary>
    ColumnMerge = 2,

    /// <summary>Hybrid-logical-clock last writer wins: the newer of the client edit time and the server change time.</summary>
    LastWriterWins = 3,

    /// <summary>A registered <c>IG9ConflictResolver</c> decides.</summary>
    Custom = 4
}

/// <summary>What the client does with a row that left a subscription (filter no longer matches, or unsubscribed).</summary>
public enum G9EMoveOutPolicy : byte
{
    /// <summary>Delete the local row once no other subscription holds it and it has no pending local change.</summary>
    Delete = 0,

    /// <summary>Only drop the membership; keep the row on the device.</summary>
    Keep = 1
}

/// <summary>Nullability override on <see cref="G9AttrSyncColumnAttribute" />.</summary>
public enum G9ESyncNullability : byte
{
    Infer = 0,
    Nullable = 1,
    NotNull = 2
}
