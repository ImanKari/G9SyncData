namespace G9SyncData.Server.SnapshotFiles;

/// <summary>
///     Builds, keeps and streams snapshot files for the planner (ADR-0072). The engine decides WHEN a subscription goes as a file - from
///     the device's offer, the subscription's policy, the snapshot's size and the age of what is already built - and hands this service
///     everything it needs to build one: the device's table shapes and storage forms, and a reader for each table's snapshot rows. Register
///     an implementation with <c>UseSnapshotFiles()</c> (package <c>G9SyncData.Server.SnapshotFiles</c>); without one, every pull is rows.
/// </summary>
public interface IG9SnapshotFileService
{
    /// <summary>The thresholds the planner applies.</summary>
    G9CSnapshotFilePolicy Policy { get; }

    /// <summary>The newest file of <paramref name="identity" /> that is built or being built in this process, or null.</summary>
    G9CSnapshotFileCandidate? FindLatest(string identity);

    /// <summary>
    ///     Asks for <paramref name="build" /> to be produced. Returns false when the service declines - this identity has not been asked for
    ///     often enough yet (<see cref="G9CSnapshotFilePolicy.BuildAfterRequests" />), or it cannot build at all - and the planner sends rows.
    ///     When it accepts, the build starts in the background and never blocks the caller.
    /// </summary>
    bool Prepare(G9CSnapshotFileBuild build);

    /// <summary>
    ///     The file's segments from <paramref name="fromSegment" /> on, building the file first (or waiting for its build) when it is not
    ///     ready - so a pull that lands on another server instance than the plan still gets its file. When the file at hand is not the one
    ///     <paramref name="expectedSha256" /> names (another instance built it from the same rows a moment later), the stream starts over at
    ///     segment 0 and the device starts the file over. Throws <c>G9S-FILE-UNAVAILABLE</c> when the file cannot be produced.
    /// </summary>
    IAsyncEnumerable<G9CSnapshotFileSegment> ReadAsync(G9CSnapshotFileBuild build, int fromSegment, string? expectedSha256, CancellationToken cancellationToken);
}
