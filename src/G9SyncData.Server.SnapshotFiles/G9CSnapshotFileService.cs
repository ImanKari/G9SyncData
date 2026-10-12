using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using G9SyncData.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace G9SyncData.Server.SnapshotFiles;

/// <summary>
///     The snapshot-file cache of one server instance (ADR-0072): builds a file once per identity and snapshot, keeps it while it is fresh,
///     and streams its segments to every device that asks. A file is found again after a restart (its record and container stay on disk), a
///     build is shared by every plan that asks for the same file while it runs, and nothing a device is downloading is deleted under it.
/// </summary>
public sealed partial class G9CSnapshotFileService : IG9SnapshotFileService, IDisposable
{
    private readonly G9CSnapshotFileOptions _options;
    private readonly ILogger<G9CSnapshotFileService> _logger;
    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _requests = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _builds;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer _cleanup;
    private int _cleaning;
    private int _disposed;

    public G9CSnapshotFileService(IOptions<G9CSnapshotFileOptions> options, ILogger<G9CSnapshotFileService> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger;
        if (_options.SegmentBytes < 4096 || _options.SegmentBytes % 4096 != 0)
        {
            throw new ArgumentException($"{nameof(G9CSnapshotFileOptions.SegmentBytes)} must be a positive multiple of 4096; {_options.SegmentBytes} was configured.");
        }

        // The builder runs the client store's SQLite code here; the bundle this package references provides the native library.
        SQLitePCL.Batteries_V2.Init();
        Policy = new G9CSnapshotFilePolicy
        {
            MinRows = _options.MinRows, MaxDeltaRatio = _options.MaxDeltaRatio, MaxAge = _options.MaxAge, BuildAfterRequests = Math.Max(1, _options.BuildAfterRequests)
        };
        _builds = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentBuilds));
        Directory.CreateDirectory(_options.Directory);
        LoadCatalog();
        _cleanup = new Timer(_ => Cleanup(), null, _options.CleanupInterval, _options.CleanupInterval);
    }

    public G9CSnapshotFilePolicy Policy { get; }

    /// <summary>Files this instance knows (ready, building or failed), for diagnostics.</summary>
    public IReadOnlyList<G9CSnapshotFileCandidate> Files =>
        _entries.Values.Where(e => !e.Failed).Select(e => e.ToCandidate()).OrderByDescending(c => c.SnapshotUtc).ToList();

    public G9CSnapshotFileCandidate? FindLatest(string identity)
    {
        ArgumentException.ThrowIfNullOrEmpty(identity);
        Entry? best = null;
        foreach (var entry in _entries.Values)
        {
            if (entry.Failed || !string.Equals(entry.Identity, identity, StringComparison.Ordinal)) continue;
            if (best is null || entry.SnapshotUtc > best.SnapshotUtc) best = entry;
        }

        return best?.ToCandidate();
    }

    public bool Prepare(G9CSnapshotFileBuild build)
    {
        ArgumentNullException.ThrowIfNull(build);
        if (_entries.TryGetValue(build.FileId, out var existing) && !existing.Failed) return true;

        // Built on reuse rather than first sight when asked to: an identity seen once costs a counter, not a file.
        var requests = _requests.AddOrUpdate(build.Identity, 1, (_, count) => count + 1);
        if (requests < Policy.BuildAfterRequests) return false;

        try
        {
            var free = new DriveInfo(Path.GetFullPath(_options.Directory)).AvailableFreeSpace;
            if (free < _options.MinFreeBytes)
            {
                LogNoRoom(_logger, build.SubscriptionKey, free);
                return false;
            }
        }
        catch (Exception exception) when (exception is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Unknown free space never blocks a build: the build itself fails cleanly when the disk is full.
        }

        // Started now, in the background: by the time the device asks for the first segment the build is under way, or done.
        _ = GetOrStart(build).Ready;
        return true;
    }

    public async IAsyncEnumerable<G9CSnapshotFileSegment> ReadAsync(G9CSnapshotFileBuild build, int fromSegment, string? expectedSha256,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(build);
        var entry = GetOrStart(build);
        ReadyFile file;
        try
        {
            file = await entry.Ready.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw Unavailable(build.FileId, exception);
        }

        var record = file.Record;
        if (fromSegment < 0 || fromSegment >= record.Segments.Count
            || (expectedSha256 is not null && !string.Equals(expectedSha256, record.Manifest.Sha256, StringComparison.Ordinal)))
        {
            // Not the file this download started on (another instance built it from the same rows): the device starts the file over.
            fromSegment = 0;
        }

        Interlocked.Increment(ref entry.Readers);
        entry.Touch();
        try
        {
            Microsoft.Win32.SafeHandles.SafeFileHandle handle;
            try
            {
                handle = File.OpenHandle(file.ContainerPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Removed from disk behind this process: forget it, so the next plan builds a new one.
                _entries.TryRemove(entry.FileId, out _);
                throw Unavailable(build.FileId, exception);
            }

            using (handle)
            {
                for (var index = fromSegment; index < record.Segments.Count; index++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var segment = record.Segments[index];
                    var bytes = new byte[segment.Length];
                    var read = 0;
                    while (read < bytes.Length)
                    {
                        var n = await RandomAccess.ReadAsync(handle, bytes.AsMemory(read), segment.Offset + read, cancellationToken).ConfigureAwait(false);
                        if (n == 0) throw Unavailable(build.FileId, new EndOfStreamException("The segment container is shorter than its record."));
                        read += n;
                    }

                    yield return new G9CSnapshotFileSegment(
                        new G9DtFileSegment
                        {
                            FileId = record.FileId, Index = index, Offset = (long)index * record.Manifest.SegmentBytes, Length = segment.RawLength,
                            Sha256 = segment.Sha256
                        },
                        record.Manifest, bytes, segment.Rows);
                }
            }
        }
        finally
        {
            Interlocked.Decrement(ref entry.Readers);
            entry.Touch();
        }
    }

    /// <summary>Stops the cleanup timer and cancels builds in progress. Idempotent: DI disposes the service under both of its registrations.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _cleanup.Dispose();
        _lifetime.Cancel();
        _lifetime.Dispose();
    }

    private Entry GetOrStart(G9CSnapshotFileBuild build)
    {
        while (true)
        {
            var entry = _entries.GetOrAdd(build.FileId, _ => new Entry(build, this));
            if (!entry.Failed) return entry;

            // A failed build is retried by the next request that needs the file, not served as failed forever.
            _entries.TryRemove(new KeyValuePair<string, Entry>(build.FileId, entry));
        }
    }

    private static G9CSyncException Unavailable(string fileId, Exception cause) =>
        new(G9SyncErrorCodes.SnapshotFileUnavailable, $"Snapshot file {fileId} could not be produced: {cause.Message}", cause) { Retryable = false };

    // ================================================================ catalog on disk

    private string RecordPath(string fileId) => Path.Combine(_options.Directory, fileId + ".json");

    /// <summary>
    ///     Reads the files a previous run of this process (or another instance sharing the directory) left: a record names a complete
    ///     container. Leftovers of builds that never finished are deleted.
    /// </summary>
    private void LoadCatalog()
    {
        foreach (var leftover in Directory.EnumerateFiles(_options.Directory, "*.building*").Concat(Directory.EnumerateFiles(_options.Directory, "*.tmp")))
        {
            TryDelete(leftover);
        }

        foreach (var path in Directory.EnumerateFiles(_options.Directory, "*.json"))
        {
            try
            {
                var record = JsonSerializer.Deserialize(File.ReadAllText(path), G9CSnapshotFileJsonContext.Default.G9CSnapshotFileRecord);
                if (record is null) continue;
                var container = Path.Combine(_options.Directory, record.Container);
                if (!File.Exists(container))
                {
                    TryDelete(path);
                    continue;
                }

                _entries.TryAdd(record.FileId, new Entry(record, container));
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                LogCatalogEntrySkipped(_logger, path, exception.Message);
            }
        }

        if (!_entries.IsEmpty) LogCatalogLoaded(_logger, _entries.Count, _options.Directory);
    }

    // ================================================================ cleanup

    /// <summary>
    ///     Removes what will not be served again: failed builds, files past their age, files a newer one replaced (after a grace period for
    ///     downloads still streaming them), then the least recently used files beyond the size limit. A file being read is never removed.
    /// </summary>
    internal void Cleanup()
    {
        if (Interlocked.Exchange(ref _cleaning, 1) == 1) return;
        try
        {
            var now = DateTime.UtcNow;
            var ready = _entries.Values.Where(e => e.Record is not null).ToList();
            var newest = ready.GroupBy(e => e.Identity, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.MaxBy(e => e.SnapshotUtc)!, StringComparer.Ordinal);

            foreach (var entry in _entries.Values.Where(e => e.Failed && now - e.LastUsedUtc > TimeSpan.FromMinutes(1)).ToList())
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(entry.FileId, entry));
            }

            foreach (var entry in ready)
            {
                if (Volatile.Read(ref entry.Readers) > 0) continue;
                var expired = now - entry.SnapshotUtc > _options.MaxAge + _options.SupersededGrace;
                var superseded = !ReferenceEquals(newest[entry.Identity], entry) && now - newest[entry.Identity].ReadyUtc > _options.SupersededGrace;
                if (expired || superseded) Remove(entry, expired ? "expired" : "superseded");
            }

            var kept = _entries.Values.Where(e => e.Record is not null).OrderByDescending(e => e.LastUsedUtc).ToList();
            long total = 0;
            foreach (var entry in kept)
            {
                total += entry.Bytes;
                if (total > _options.MaxCacheBytes && Volatile.Read(ref entry.Readers) == 0) Remove(entry, "over the size limit");
            }

            // Containers no record names any more (a record replaced by another instance's build of the same file).
            var named = new HashSet<string>(_entries.Values.Where(e => e.ContainerPath is not null).Select(e => Path.GetFileName(e.ContainerPath!)), StringComparer.OrdinalIgnoreCase);
            foreach (var container in Directory.EnumerateFiles(_options.Directory, "*.g9seg"))
            {
                if (!named.Contains(Path.GetFileName(container)) && now - File.GetLastWriteTimeUtc(container) > _options.SupersededGrace) TryDelete(container);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogCleanupFailed(_logger, exception.Message);
        }
        finally
        {
            Volatile.Write(ref _cleaning, 0);
        }
    }

    private void Remove(Entry entry, string reason)
    {
        if (!_entries.TryRemove(new KeyValuePair<string, Entry>(entry.FileId, entry))) return;

        // The record goes first: a crash between the two leaves an orphan container (swept later), never a record without its file.
        if (File.Exists(RecordPath(entry.FileId)))
        {
            try
            {
                var onDisk = JsonSerializer.Deserialize(File.ReadAllText(RecordPath(entry.FileId)), G9CSnapshotFileJsonContext.Default.G9CSnapshotFileRecord);
                if (onDisk is not null && string.Equals(onDisk.Container, entry.Record?.Container, StringComparison.OrdinalIgnoreCase)) TryDelete(RecordPath(entry.FileId));
            }
            catch (JsonException)
            {
                TryDelete(RecordPath(entry.FileId));
            }
        }

        if (entry.ContainerPath is not null) TryDelete(entry.ContainerPath);
        LogRemoved(_logger, entry.FileId, reason);
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            LogDeleteFailed(_logger, path, exception.Message);
        }
    }

    // ================================================================ entries

    private sealed class Entry
    {
        private readonly Lazy<Task<ReadyFile>> _ready;
        private long _lastUsedTicks;
        public int Readers;

        /// <summary>A file to build.</summary>
        public Entry(G9CSnapshotFileBuild build, G9CSnapshotFileService service)
        {
            FileId = build.FileId;
            Identity = build.Identity;
            Snapshot = build.Snapshot;
            Epoch = build.Epoch;
            SnapshotUtc = build.SnapshotUtc;
            Touch();
            _ready = new Lazy<Task<ReadyFile>>(() => Task.Run(async () =>
            {
                try
                {
                    var file = await service.BuildAsync(build).ConfigureAwait(false);
                    Record = file.Record;
                    ContainerPath = file.ContainerPath;
                    ReadyUtc = DateTime.UtcNow;
                    service.Cleanup();
                    return file;
                }
                catch
                {
                    Failed = true;
                    throw;
                }
            }), LazyThreadSafetyMode.ExecutionAndPublication);
        }

        /// <summary>A file already on disk.</summary>
        public Entry(G9CSnapshotFileRecord record, string containerPath)
        {
            FileId = record.FileId;
            Identity = record.Identity;
            Snapshot = record.Snapshot;
            Epoch = record.Epoch;
            SnapshotUtc = record.SnapshotUtc;
            Record = record;
            ContainerPath = containerPath;
            ReadyUtc = record.BuiltUtc;
            Touch();
            var file = new ReadyFile(record, containerPath);
            _ready = new Lazy<Task<ReadyFile>>(Task.FromResult(file));
        }

        public string FileId { get; }
        public string Identity { get; }
        public string Snapshot { get; }
        public Guid Epoch { get; }
        public DateTime SnapshotUtc { get; }
        public volatile bool Failed;
        public G9CSnapshotFileRecord? Record { get; private set; }
        public string? ContainerPath { get; private set; }
        public DateTime ReadyUtc { get; private set; }
        public Task<ReadyFile> Ready => _ready.Value;
        public long Bytes => Record?.CompressedBytes ?? 0;
        public DateTime LastUsedUtc => new(Interlocked.Read(ref _lastUsedTicks), DateTimeKind.Utc);

        public void Touch() => Interlocked.Exchange(ref _lastUsedTicks, DateTime.UtcNow.Ticks);

        public G9CSnapshotFileCandidate ToCandidate()
        {
            var record = Record;
            return new G9CSnapshotFileCandidate(FileId, Snapshot, Epoch, SnapshotUtc, record is not null, record?.Rows ?? 0, record?.Manifest.Bytes ?? 0,
                record?.CompressedBytes ?? 0, record?.RowsByTable ?? new Dictionary<int, long>());
        }
    }

    internal sealed record ReadyFile(G9CSnapshotFileRecord Record, string ContainerPath);

    [LoggerMessage(EventId = 21200, Level = LogLevel.Information, Message = "Snapshot files: {Count} built file(s) found in {Directory}")]
    private static partial void LogCatalogLoaded(ILogger logger, int count, string directory);

    [LoggerMessage(EventId = 21201, Level = LogLevel.Warning, Message = "Snapshot files: skipped the unreadable record {Path} ({Message})")]
    private static partial void LogCatalogEntrySkipped(ILogger logger, string path, string message);

    [LoggerMessage(EventId = 21202, Level = LogLevel.Warning, Message = "Snapshot files: not building one for {Subscription}, only {FreeBytes} bytes free")]
    private static partial void LogNoRoom(ILogger logger, string subscription, long freeBytes);

    [LoggerMessage(EventId = 21206, Level = LogLevel.Information, Message = "Snapshot file {FileId} removed ({Reason})")]
    private static partial void LogRemoved(ILogger logger, string fileId, string reason);

    [LoggerMessage(EventId = 21207, Level = LogLevel.Warning, Message = "Snapshot files: could not delete {Path} ({Message}); retried at the next cleanup")]
    private static partial void LogDeleteFailed(ILogger logger, string path, string message);

    [LoggerMessage(EventId = 21208, Level = LogLevel.Warning, Message = "Snapshot files: cleanup failed ({Message}); retried at the next interval")]
    private static partial void LogCleanupFailed(ILogger logger, string message);
}
