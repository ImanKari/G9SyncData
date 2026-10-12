namespace G9SyncData.Server.SnapshotFiles;

/// <summary>A file the planner may offer: built (<see cref="Ready" />) or still being built.</summary>
/// <param name="FileId">The file's id.</param>
/// <param name="Snapshot">The provider snapshot the file was planned at; deltas from it bring a device up to date.</param>
/// <param name="Epoch">The server epoch the snapshot belongs to.</param>
/// <param name="SnapshotUtc">When that snapshot was taken.</param>
/// <param name="Ready">True once the file is built.</param>
/// <param name="Rows">Rows in the file (0 until ready).</param>
/// <param name="RawBytes">Uncompressed size (0 until ready).</param>
/// <param name="CompressedBytes">Bytes on the wire (0 until ready).</param>
/// <param name="RowsByTable">Rows per server table id (empty until ready).</param>
public sealed record G9CSnapshotFileCandidate(
    string FileId,
    string Snapshot,
    Guid Epoch,
    DateTime SnapshotUtc,
    bool Ready,
    long Rows,
    long RawBytes,
    long CompressedBytes,
    IReadOnlyDictionary<int, long> RowsByTable);
