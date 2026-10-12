namespace G9SyncData.Protocol;

// Snapshot files (ADR-0072): a subscription's first download delivered as a SQLite file the device copies b-tree to b-tree, instead of
// rows it inserts one by one. Everything here is additive: a client that does not send G9DtPullRequest.SnapshotFiles is never sent a
// file, and a server that does not know the field ignores it.

/// <summary>
///     The client's offer to receive snapshot files, sent with a pull request. It says how the device stores values and what its tables
///     look like, so the server can write a file whose tables are byte-compatible with the device's - same column order, declared types,
///     collations and defaults - which is what lets SQLite copy the pages instead of the values (the transfer optimisation).
/// </summary>
public sealed class G9DtSnapshotFileRequest
{
    /// <summary>The newest file format the client reads (<see cref="G9SyncProtocol.SnapshotFileFormatVersion" />).</summary>
    public int FormatVersion { get; set; }

    /// <summary>How the device stores the values the frames carry.</summary>
    public G9DtSnapshotFileStorage Storage { get; set; } = new();

    /// <summary>The device's synced tables, by server table id, with their columns in LOCAL order.</summary>
    public List<G9DtSnapshotFileTable> Tables { get; set; } = [];

    /// <summary>A subscription is sent as a file only when its snapshot is at least this many rows (0 = the server's threshold).</summary>
    public long MinRows { get; set; }

    /// <summary>The largest file the client accepts, uncompressed (0 = no limit). Larger subscriptions come as rows.</summary>
    public long MaxBytes { get; set; }

    /// <summary>The most file parts one plan may carry (each is ATTACHed during the apply; SQLite attaches at most 10 databases).</summary>
    public int MaxFiles { get; set; }
}

/// <summary>The storage forms of the device's SQLite store (the forms <c>G9CSqliteSyncStoreOptions</c> configures).</summary>
public sealed class G9DtSnapshotFileStorage
{
    /// <summary>DateTime as INTEGER ticks instead of text.</summary>
    public bool DateTimeAsTicks { get; set; }

    /// <summary>The text format of DateTime values when <see cref="DateTimeAsTicks" /> is false.</summary>
    public string DateTimeFormat { get; set; } = "yyyy-MM-dd'T'HH:mm:ss.fffffff";

    /// <summary>Decimal as exact TEXT instead of REAL.</summary>
    public bool DecimalAsText { get; set; }
}

/// <summary>One device table: its local name, key and synced columns in the device's own order (the key is not among them).</summary>
public sealed class G9DtSnapshotFileTable
{
    public int TableId { get; set; }
    public string LocalName { get; set; } = string.Empty;
    public string KeyColumn { get; set; } = string.Empty;
    public G9ESyncColumnType KeyType { get; set; }
    public List<G9DtColumnModel> Columns { get; set; } = [];

    /// <summary>
    ///     The indexes the device declares on the table. The file carries them too, so the copy moves their b-trees as they are and the
    ///     device builds no index at all.
    /// </summary>
    public List<G9DtSnapshotFileIndex> Indexes { get; set; } = [];
}

/// <summary>A declared index: name, columns in order, uniqueness.</summary>
public sealed class G9DtSnapshotFileIndex
{
    public string Name { get; set; } = string.Empty;
    public List<string> Columns { get; set; } = [];
    public bool Unique { get; set; }
}

/// <summary>The file a planned part of kind <see cref="G9EPullPartKind.SnapshotFile" /> delivers.</summary>
public sealed class G9DtPlannedFile
{
    public string FileId { get; set; } = string.Empty;

    /// <summary>
    ///     When the server's state the file holds was taken. The delta parts that follow it in the plan bring the rows up to the plan's
    ///     snapshot; the device counts its next safety reconciliation from this moment, not from the end of the pull.
    /// </summary>
    public DateTime SnapshotUtc { get; set; }

    /// <summary>Rows per table (an upper bound until the file is built).</summary>
    public List<G9DtPlannedFileTable> Tables { get; set; } = [];
}

public sealed class G9DtPlannedFileTable
{
    public int TableId { get; set; }
    public long Rows { get; set; }
}

/// <summary>
///     One segment of a snapshot file, carried by a pull chunk whose payload is the segment compressed on its own (Brotli). A segment is
///     <see cref="G9DtSnapshotFileManifest.SegmentBytes" /> of the raw file at <see cref="Offset" />; every segment decompresses alone,
///     so a download resumes at any segment and the device writes each one straight to its place in the file.
/// </summary>
public sealed class G9DtFileSegment
{
    public string FileId { get; set; } = string.Empty;
    public int Index { get; set; }
    public long Offset { get; set; }

    /// <summary>Raw (decompressed) length.</summary>
    public int Length { get; set; }

    /// <summary>SHA-256 of the raw segment, lower-case hex.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Set on the first segment a stream sends for the file (also after a resume): what the whole file is.</summary>
    public G9DtSnapshotFileManifest? Manifest { get; set; }
}

/// <summary>What a snapshot file is: its size, its segments and the tables inside it.</summary>
public sealed class G9DtSnapshotFileManifest
{
    public string FileId { get; set; } = string.Empty;
    public int FormatVersion { get; set; }

    /// <summary>Raw file size.</summary>
    public long Bytes { get; set; }

    public int SegmentBytes { get; set; }
    public int Segments { get; set; }

    /// <summary>SHA-256 of the whole raw file, lower-case hex. A resumed download whose manifest differs starts the file over.</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>Segment codec: <c>br</c> (Brotli).</summary>
    public string Compression { get; set; } = "br";

    public DateTime BuiltUtc { get; set; }
    public List<G9DtSnapshotFileTableInfo> Tables { get; set; } = [];
}

/// <summary>A table inside a snapshot file: its rows in <see cref="Name" />, and the server version of each row in <see cref="VersionTable" />.</summary>
public sealed class G9DtSnapshotFileTableInfo
{
    public int TableId { get; set; }

    /// <summary>The table holding the rows: the device's local name, with the device's own DDL.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary><c>(pk TEXT PRIMARY KEY, ver INTEGER) WITHOUT ROWID</c>: canonical key text and server version of every row in <see cref="Name" />.</summary>
    public string VersionTable { get; set; } = string.Empty;

    public long Rows { get; set; }
}
