using G9SyncData.Protocol;
using G9SyncData.Server.Storage;

namespace G9SyncData.Server.SnapshotFiles;

/// <summary>One table of a file: the device's shape of it, the columns the frames carry, and its snapshot rows.</summary>
public sealed class G9CSnapshotFileTableSource
{
    public required int TableId { get; init; }
    public required string ServerName { get; init; }
    public required G9DtSnapshotFileTable Shape { get; init; }
    public required IReadOnlyList<G9DtColumnModel> FrameColumns { get; init; }

    /// <summary>The table's snapshot part as frames, at the build's snapshot (the store's ordinary snapshot read).</summary>
    public required Func<CancellationToken, IAsyncEnumerable<G9CStorePage>> ReadAsync { get; init; }
}
