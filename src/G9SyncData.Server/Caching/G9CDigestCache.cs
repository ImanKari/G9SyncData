using System.Diagnostics.CodeAnalysis;
using System.Diagnostics.Metrics;
using System.Globalization;
using G9SyncData.Server.Diagnostics;

namespace G9SyncData.Server.Caching;

/// <summary>
///     The server-side digest cache (ADR-0024): per VIEW (a subscription's include, for one set of parameter and auth values),
///     the keys and versions the view contained at a known point, with the reconciliation digests derived from them.
///     <para>
///         <b>An accelerator, never an authority.</b> A storage provider validates an entry against the database before using
///         it, brings it forward with the changes made since, and falls back to deriving everything when it cannot prove the
///         entry is still exact. A miss, an eviction or a lost process is therefore only slower, never wrong.
///     </para>
///     <para>
///         <b>Concurrency is check-out / check-in.</b> A reader takes an entry exclusively (<see cref="TryCheckOut" />), brings it
///         forward and returns it (<see cref="CheckIn" />). A concurrent reader of the same view misses and derives; when both
///         check in, the entry valid at the later point (<see cref="G9CDigestCacheEntry.Order" />) wins. Nobody ever reads an
///         entry another reader is changing.
///     </para>
/// </summary>
public interface IG9DigestCache
{
    /// <summary>Takes the entry for <paramref name="identity" /> out of the cache, or returns null (a miss).</summary>
    G9CDigestCacheEntry? TryCheckOut(string identity);

    /// <summary>Puts an entry back (or in for the first time). An entry older than one already present is discarded.</summary>
    void CheckIn(G9CDigestCacheEntry entry);

    /// <summary>Forgets a view (e.g. its entry could not be validated).</summary>
    void Remove(string identity);

    /// <summary>Current size and counters.</summary>
    G9CDigestCacheStatistics GetStatistics();
}

/// <summary>One view's keys, versions and digests at one point of the store's history.</summary>
public sealed class G9CDigestCacheEntry
{
    public G9CDigestCacheEntry(string identity, G9ESyncColumnType keyType)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identity);
        Identity = identity;
        Keys = new G9CDigestKeyMap(keyType);
    }

    /// <summary>What the entry describes: provider-defined, and must change whenever the view's MEANING changes.</summary>
    public string Identity { get; }

    /// <summary>Installation epoch the entry belongs to. An entry of another epoch is never valid.</summary>
    public Guid Epoch { get; set; }

    /// <summary>Provider-specific point the entry is exact at (PostgreSQL: a <c>pg_snapshot</c>).</summary>
    public string ValidAt { get; set; } = string.Empty;

    /// <summary>A monotonic number for <see cref="ValidAt" />, so two entries of one view can be ordered.</summary>
    public long Order { get; set; }

    /// <summary>Row count per bucket.</summary>
    public int[] Counts { get; } = new int[G9CSyncDigest.BucketCount];

    /// <summary>XOR of the row hashes per bucket.</summary>
    public long[] Digests { get; } = new long[G9CSyncDigest.BucketCount];

    /// <summary>Every key in the view with its version.</summary>
    public G9CDigestKeyMap Keys { get; }

    public DateTime ComputedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Approximate managed memory held by the entry.</summary>
    public long EstimatedBytes => 64 + (G9CSyncDigest.BucketCount * 12L) + Keys.EstimatedBytes;

    /// <summary>Adds or moves a key to <paramref name="version" />, keeping counts and digests exact.</summary>
    public void Set(string canonicalKey, long version)
    {
        var bucket = G9CSyncDigest.GetBucket(canonicalKey);
        if (Keys.TryGetValue(canonicalKey, out var previous))
        {
            if (previous == version) return;
            Digests[bucket] ^= G9CSyncDigest.GetRowHash(canonicalKey, previous);
        }
        else
        {
            Counts[bucket]++;
        }

        Digests[bucket] ^= G9CSyncDigest.GetRowHash(canonicalKey, version);
        Keys.Set(canonicalKey, version);
    }

    /// <summary>Removes a key if present, keeping counts and digests exact.</summary>
    public void Remove(string canonicalKey)
    {
        if (!Keys.TryGetValue(canonicalKey, out var previous)) return;
        var bucket = G9CSyncDigest.GetBucket(canonicalKey);
        Digests[bucket] ^= G9CSyncDigest.GetRowHash(canonicalKey, previous);
        Counts[bucket]--;
        Keys.Remove(canonicalKey);
    }
}

/// <summary>
///     Canonical key → version, stored by the key's native type: a GUID key costs ~48 bytes instead of ~130 as a string.
/// </summary>
public sealed class G9CDigestKeyMap
{
    private readonly Dictionary<Guid, long>? _guids;
    private readonly Dictionary<long, long>? _numbers;
    private readonly Dictionary<string, long>? _strings;
    private long _stringChars;

    public G9CDigestKeyMap(G9ESyncColumnType keyType)
    {
        KeyType = keyType;
        switch (keyType)
        {
            case G9ESyncColumnType.Guid:
                _guids = [];
                break;
            case G9ESyncColumnType.Int32 or G9ESyncColumnType.Int64:
                _numbers = [];
                break;
            default:
                _strings = new Dictionary<string, long>(StringComparer.Ordinal);
                break;
        }
    }

    public G9ESyncColumnType KeyType { get; }

    public int Count => _guids?.Count ?? _numbers?.Count ?? _strings!.Count;

    public long EstimatedBytes => _guids is not null ? _guids.Count * 48L
        : _numbers is not null ? _numbers.Count * 40L
        : (_strings!.Count * 60L) + (_stringChars * 2);

    public bool TryGetValue(string canonicalKey, out long version)
    {
        if (_guids is not null) return _guids.TryGetValue(Guid.Parse(canonicalKey), out version);
        if (_numbers is not null) return _numbers.TryGetValue(long.Parse(canonicalKey, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture), out version);
        return _strings!.TryGetValue(canonicalKey, out version);
    }

    public void Set(string canonicalKey, long version)
    {
        if (_guids is not null) _guids[Guid.Parse(canonicalKey)] = version;
        else if (_numbers is not null) _numbers[long.Parse(canonicalKey, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture)] = version;
        else
        {
            if (!_strings!.ContainsKey(canonicalKey)) _stringChars += canonicalKey.Length;
            _strings[canonicalKey] = version;
        }
    }

    public void Remove(string canonicalKey)
    {
        if (_guids is not null) _guids.Remove(Guid.Parse(canonicalKey));
        else if (_numbers is not null) _numbers.Remove(long.Parse(canonicalKey, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture));
        else if (_strings!.Remove(canonicalKey)) _stringChars -= canonicalKey.Length;
    }

    /// <summary>Every key with its version, as canonical text. Order is unspecified.</summary>
    public IEnumerable<(string Key, long Version)> Enumerate()
    {
        if (_guids is not null)
        {
            foreach (var (key, version) in _guids) yield return (G9CSyncKey.FromGuid(key), version);
        }
        else if (_numbers is not null)
        {
            foreach (var (key, version) in _numbers) yield return (G9CSyncKey.FromInt64(key), version);
        }
        else
        {
            foreach (var (key, version) in _strings!) yield return (key, version);
        }
    }
}

/// <summary>Settings of <see cref="G9CMemoryDigestCache" />.</summary>
public sealed class G9CDigestCacheOptions
{
    /// <summary>Memory ceiling. Least recently used entries are evicted above it; an entry larger than it is never stored.</summary>
    public long MaxBytes { get; set; } = 256L * 1024 * 1024;

    /// <summary>An entry not used for this long is dropped at check-out. Bounds how far back a provider must validate.</summary>
    public TimeSpan MaxAge { get; set; } = TimeSpan.FromHours(6);

    /// <summary>
    ///     A provider brings an entry forward only when fewer keys than this changed since it was valid; beyond that, deriving
    ///     afresh is cheaper than probing the changes.
    /// </summary>
    public int MaxIncrementalKeys { get; set; } = 20_000;
}

public readonly record struct G9CDigestCacheStatistics(int Entries, long Bytes, long Hits, long Misses, long Evictions, long Expired, long DiscardedStale);

/// <summary>An in-process LRU <see cref="IG9DigestCache" /> with a byte ceiling. Cold start is simply a run of misses.</summary>
public sealed class G9CMemoryDigestCache : IG9DigestCache
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Slot>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Slot> _lru = new();
    private readonly G9CDigestCacheOptions _options;
    private long _bytes;
    private long _hits;
    private long _misses;
    private long _evictions;
    private long _expired;
    private long _discarded;

    public G9CMemoryDigestCache(G9CDigestCacheOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options;
        G9CDigestCacheTelemetry.Register(this);
    }

    public G9CDigestCacheOptions Options => _options;

    public G9CDigestCacheEntry? TryCheckOut(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            if (!_entries.TryGetValue(identity, out var node))
            {
                _misses++;
                G9CDigestCacheTelemetry.Misses.Add(1);
                return null;
            }

            RemoveNode(node);
            if (DateTime.UtcNow - node.Value.LastUsedUtc > _options.MaxAge)
            {
                _expired++;
                G9CDigestCacheTelemetry.Expired.Add(1);
                _misses++;
                G9CDigestCacheTelemetry.Misses.Add(1);
                return null;
            }

            _hits++;
            G9CDigestCacheTelemetry.Hits.Add(1);
            return node.Value.Entry;
        }
    }

    public void CheckIn(G9CDigestCacheEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var size = entry.EstimatedBytes;
        lock (_gate)
        {
            if (_entries.TryGetValue(entry.Identity, out var existing))
            {
                if (existing.Value.Entry.Order >= entry.Order && !ReferenceEquals(existing.Value.Entry, entry))
                {
                    _discarded++;
                    G9CDigestCacheTelemetry.DiscardedStale.Add(1);
                    return;
                }

                RemoveNode(existing);
            }

            if (size > _options.MaxBytes) return;
            var node = _lru.AddFirst(new Slot(entry, size, DateTime.UtcNow));
            _entries[entry.Identity] = node;
            _bytes += size;

            while (_bytes > _options.MaxBytes && _lru.Last is { } oldest)
            {
                RemoveNode(oldest);
                _evictions++;
                G9CDigestCacheTelemetry.Evictions.Add(1);
            }
        }
    }

    public void Remove(string identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        lock (_gate)
        {
            if (_entries.TryGetValue(identity, out var node)) RemoveNode(node);
        }
    }

    public G9CDigestCacheStatistics GetStatistics()
    {
        lock (_gate) return new G9CDigestCacheStatistics(_entries.Count, _bytes, _hits, _misses, _evictions, _expired, _discarded);
    }

    private void RemoveNode(LinkedListNode<Slot> node)
    {
        _lru.Remove(node);
        _entries.Remove(node.Value.Entry.Identity);
        _bytes -= node.Value.Bytes;
    }

    private sealed record Slot(G9CDigestCacheEntry Entry, long Bytes, DateTime LastUsedUtc);
}

/// <summary>What a storage provider reports about its use of the digest cache (published on the server meter).</summary>
public static class G9CDigestCacheMetrics
{
    /// <summary>A view whose digests were derived from the database; <paramref name="reason" /> is a low-cardinality tag.</summary>
    public static void RecordDerivation(string reason) =>
        G9CDigestCacheTelemetry.Derivations.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>A view served from the cache after applying the changes since it was valid.</summary>
    public static void RecordIncremental(TimeSpan elapsed)
    {
        G9CDigestCacheTelemetry.Incremental.Add(1);
        G9CDigestCacheTelemetry.IncrementalDuration.Record(elapsed.TotalMilliseconds);
    }
}

/// <summary>Digest cache metrics on the server meter (ADR-0019).</summary>
internal static class G9CDigestCacheTelemetry
{
    private static readonly List<WeakReference<G9CMemoryDigestCache>> Caches = [];

    public static readonly Counter<long> Hits =
        G9CSyncServerTelemetry.Meter.CreateCounter<long>("g9sync.digest_cache.hits", "{lookup}", "Digest cache check-outs that found an entry");

    public static readonly Counter<long> Misses =
        G9CSyncServerTelemetry.Meter.CreateCounter<long>("g9sync.digest_cache.misses", "{lookup}", "Digest cache check-outs that found none (or an expired one)");

    public static readonly Counter<long> Evictions =
        G9CSyncServerTelemetry.Meter.CreateCounter<long>("g9sync.digest_cache.evictions", "{entry}", "Entries evicted by the memory ceiling");

    public static readonly Counter<long> Expired =
        G9CSyncServerTelemetry.Meter.CreateCounter<long>("g9sync.digest_cache.expired", "{entry}", "Entries dropped because they were idle past MaxAge");

    public static readonly Counter<long> DiscardedStale =
        G9CSyncServerTelemetry.Meter.CreateCounter<long>("g9sync.digest_cache.discarded_stale", "{entry}", "Check-ins discarded because a newer entry was present");

    /// <summary>Provider fallbacks, tagged <c>reason</c> (miss, epoch, horizon, dependency, too_many_changes, uncacheable).</summary>
    public static readonly Counter<long> Derivations =
        G9CSyncServerTelemetry.Meter.CreateCounter<long>("g9sync.digest_cache.derivations", "{view}", "Views whose digests were derived from the database instead of the cache");

    /// <summary>Entries brought forward with the changes since they were valid.</summary>
    public static readonly Counter<long> Incremental =
        G9CSyncServerTelemetry.Meter.CreateCounter<long>("g9sync.digest_cache.incremental", "{view}", "Views served from the cache after applying the changes since it was valid");

    public static readonly Histogram<double> IncrementalDuration =
        G9CSyncServerTelemetry.Meter.CreateHistogram<double>("g9sync.digest_cache.incremental_duration", "ms", "Time to validate and bring an entry forward");

    [SuppressMessage("Performance", "CA1823", Justification = "Observable instruments are kept alive by the meter.")]
    private static readonly ObservableGauge<long> Bytes =
        G9CSyncServerTelemetry.Meter.CreateObservableGauge("g9sync.digest_cache.bytes", () => Sum(s => s.Bytes), "By", "Memory held by digest cache entries");

    [SuppressMessage("Performance", "CA1823", Justification = "Observable instruments are kept alive by the meter.")]
    private static readonly ObservableGauge<long> Entries =
        G9CSyncServerTelemetry.Meter.CreateObservableGauge("g9sync.digest_cache.entries", () => Sum(s => s.Entries), "{entry}", "Digest cache entries");

    public static void Register(G9CMemoryDigestCache cache)
    {
        lock (Caches) Caches.Add(new WeakReference<G9CMemoryDigestCache>(cache));
    }

    private static long Sum(Func<G9CDigestCacheStatistics, long> selector)
    {
        long total = 0;
        lock (Caches)
        {
            Caches.RemoveAll(reference => !reference.TryGetTarget(out _));
            foreach (var reference in Caches)
            {
                if (reference.TryGetTarget(out var cache)) total += selector(cache.GetStatistics());
            }
        }

        return total;
    }
}
