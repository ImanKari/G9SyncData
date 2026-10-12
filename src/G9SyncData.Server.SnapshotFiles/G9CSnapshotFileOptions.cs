namespace G9SyncData.Server.SnapshotFiles;

/// <summary>Where snapshot files live, when they are built and reused, and how they are cut and compressed (ADR-0072).</summary>
public sealed class G9CSnapshotFileOptions
{
    /// <summary>
    ///     The directory files are built in and served from. Default: <c>g9sync-snapshot-files</c> under the temp directory. Give a server a
    ///     volume of its own; several instances may share one (files are named by their content and never overwritten).
    /// </summary>
    public string Directory { get; set; } = Path.Combine(Path.GetTempPath(), "g9sync-snapshot-files");

    /// <summary>A subscription's snapshot goes as a file only when it is at least this many rows. Default 20,000.</summary>
    public long MinRows { get; set; } = 20_000;

    /// <summary>A built file is reused while the rows changed since its snapshot are at most this share of its rows. Default 5 %.</summary>
    public double MaxDeltaRatio { get; set; } = 0.05;

    /// <summary>A file older than this is not offered again. Default 24 hours.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Plans that must ask for an identity before its file is built (see <see cref="G9CSnapshotFilePolicy.BuildAfterRequests" />). Default 1.</summary>
    public int BuildAfterRequests { get; set; } = 1;

    /// <summary>How long a file replaced by a newer one is kept for downloads already streaming it. Default 30 minutes.</summary>
    public TimeSpan SupersededGrace { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>The cache's size limit; the least recently used files are removed beyond it. Default 20 GiB.</summary>
    public long MaxCacheBytes { get; set; } = 20L * 1024 * 1024 * 1024;

    /// <summary>No file is built while the directory's volume has less free space than this. Default 2 GiB.</summary>
    public long MinFreeBytes { get; set; } = 2L * 1024 * 1024 * 1024;

    /// <summary>Builds that may run at once. Default 2.</summary>
    public int MaxConcurrentBuilds { get; set; } = 2;

    /// <summary>
    ///     Raw bytes per segment, a multiple of the 4096-byte page. A segment is compressed on its own, written by the device to its place
    ///     in the file, and is the unit a download resumes at. Default 1 MiB.
    /// </summary>
    public int SegmentBytes { get; set; } = 1024 * 1024;

    /// <summary>Brotli quality of the segments (0-11). Default 5: measured on SQLite pages, most of q11's ratio at a fraction of its cost.</summary>
    public int CompressionQuality { get; set; } = 5;

    /// <summary>Brotli window (10-24). Default 22.</summary>
    public int CompressionWindow { get; set; } = 22;

    /// <summary>Segments compressed at once. Default: half the processors.</summary>
    public int CompressionParallelism { get; set; } = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Run SQLite's <c>quick_check</c> on every built file before it is offered. Default true.</summary>
    public bool VerifyBuiltFiles { get; set; } = true;

    /// <summary>The largest decoded frame the builder accepts from the store. Default 64 MiB.</summary>
    public int MaxDecodedFrameBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>How often expired, superseded and excess files are removed. Default 10 minutes.</summary>
    public TimeSpan CleanupInterval { get; set; } = TimeSpan.FromMinutes(10);
}
