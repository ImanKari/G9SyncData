namespace G9SyncData.Server.SnapshotFiles;

/// <summary>When a subscription goes as a file.</summary>
public sealed class G9CSnapshotFilePolicy
{
    /// <summary>A subscription's snapshot goes as a file only when it is at least this many rows (a device may ask for more). Default 20,000.</summary>
    public long MinRows { get; init; } = 20_000;

    /// <summary>
    ///     A built file is reused while the rows changed since its snapshot are at most this share of its rows - the delta parts that bring
    ///     it up to date stay small. Beyond it, a new file is built at the current snapshot. Default 5 %.
    /// </summary>
    public double MaxDeltaRatio { get; init; } = 0.05;

    /// <summary>A file older than this is not offered again; a new one is built. Default 24 hours.</summary>
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromHours(24);

    /// <summary>
    ///     How many plans must ask for a file of the same identity before one is built; until then they get rows. 1 (default) builds at
    ///     the first request, right for subscriptions many devices share (a site); raise it where most identities are seen once.
    /// </summary>
    public int BuildAfterRequests { get; init; } = 1;
}
