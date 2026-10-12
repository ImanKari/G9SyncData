using G9SyncData.Protocol;

namespace G9SyncData.Server.SnapshotFiles;

/// <summary>Everything a snapshot file is built from. Built by the engine; the service never resolves subscriptions itself.</summary>
public sealed class G9CSnapshotFileBuild
{
    public required string FileId { get; init; }

    /// <summary>What the file holds, without the snapshot: the same identity at a later snapshot is the next version of the same file.</summary>
    public required string Identity { get; init; }

    public required string Snapshot { get; init; }
    public required Guid Epoch { get; init; }
    public required DateTime SnapshotUtc { get; init; }

    /// <summary>The subscription key, for logs only.</summary>
    public required string SubscriptionKey { get; init; }

    public required G9DtSnapshotFileStorage Storage { get; init; }
    public required IReadOnlyList<G9CSnapshotFileTableSource> Tables { get; init; }
}
