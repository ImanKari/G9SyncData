using G9SyncData.Server.Caching;

namespace G9SyncData.Tests.Server;

/// <summary>
///     The digest cache's bookkeeping (ADR-0024). The provider trusts an entry's digests after incremental updates, so the
///     XOR arithmetic is checked against a recomputation from scratch after thousands of random operations.
/// </summary>
public sealed class DigestCacheTests
{
    [Theory]
    [InlineData(G9ESyncColumnType.Guid)]
    [InlineData(G9ESyncColumnType.Int64)]
    [InlineData(G9ESyncColumnType.String)]
    public void Incremental_sets_and_removes_leave_exactly_the_digests_of_a_fresh_computation(G9ESyncColumnType keyType)
    {
        var random = new Random(20260916);
        var keys = Enumerable.Range(0, 400).Select(i => keyType switch
        {
            G9ESyncColumnType.Guid => G9CSyncKey.FromGuid(new Guid(i, 0, 0, new byte[8])),
            G9ESyncColumnType.Int64 => G9CSyncKey.FromInt64(i * 7919L),
            _ => "key-" + i.ToString("D5", System.Globalization.CultureInfo.InvariantCulture)
        }).ToArray();

        var entry = new G9CDigestCacheEntry("view", keyType);
        var truth = new Dictionary<string, long>(StringComparer.Ordinal);
        for (var step = 0; step < 5000; step++)
        {
            var key = keys[random.Next(keys.Length)];
            if (random.Next(4) == 0)
            {
                entry.Remove(key);
                truth.Remove(key);
            }
            else
            {
                var version = random.Next(1, 6);   // repeats on purpose: setting the same version must be a no-op
                entry.Set(key, version);
                truth[key] = version;
            }
        }

        var counts = new int[G9CSyncDigest.BucketCount];
        var digests = new long[G9CSyncDigest.BucketCount];
        foreach (var (key, version) in truth)
        {
            var bucket = G9CSyncDigest.GetBucket(key);
            counts[bucket]++;
            digests[bucket] ^= G9CSyncDigest.GetRowHash(key, version);
        }

        Assert.Equal(counts, entry.Counts);
        Assert.Equal(digests, entry.Digests);
        Assert.Equal(truth.Count, entry.Keys.Count);
        Assert.Equal(truth.OrderBy(t => t.Key, StringComparer.Ordinal).Select(t => (t.Key, t.Value)),
            entry.Keys.Enumerate().OrderBy(t => t.Key, StringComparer.Ordinal));
    }

    [Fact]
    public void A_checked_out_entry_is_invisible_to_other_readers_until_it_is_checked_in()
    {
        var cache = new G9CMemoryDigestCache(new G9CDigestCacheOptions());
        cache.CheckIn(Entry("a", order: 1, keys: 10));

        var taken = cache.TryCheckOut("a");
        Assert.NotNull(taken);
        Assert.Null(cache.TryCheckOut("a"));   // a concurrent reader misses and derives

        cache.CheckIn(taken);
        Assert.NotNull(cache.TryCheckOut("a"));
        Assert.Equal(1, cache.GetStatistics().Misses);
    }

    [Fact]
    public void Of_two_entries_for_one_view_the_one_valid_at_the_later_point_is_kept()
    {
        var cache = new G9CMemoryDigestCache(new G9CDigestCacheOptions());
        cache.CheckIn(Entry("a", order: 200, keys: 1));
        cache.CheckIn(Entry("a", order: 100, keys: 2));   // derived concurrently from an older snapshot

        Assert.Equal(200, cache.TryCheckOut("a")!.Order);
        Assert.Equal(1, cache.GetStatistics().DiscardedStale);
    }

    [Fact]
    public void The_memory_ceiling_evicts_the_least_recently_used_entries_and_never_stores_an_oversized_one()
    {
        var oneEntry = Entry("probe", 1, 1_000).EstimatedBytes;
        var cache = new G9CMemoryDigestCache(new G9CDigestCacheOptions { MaxBytes = (oneEntry * 3) + (oneEntry / 2) });
        cache.CheckIn(Entry("a", 1, 1_000));
        cache.CheckIn(Entry("b", 1, 1_000));
        cache.CheckIn(Entry("c", 1, 1_000));
        cache.CheckIn(cache.TryCheckOut("a")!);   // a becomes the most recently used
        cache.CheckIn(Entry("d", 1, 1_000));      // evicts b

        Assert.Null(cache.TryCheckOut("b"));
        Assert.NotNull(cache.TryCheckOut("a"));
        Assert.Equal(1, cache.GetStatistics().Evictions);

        cache.CheckIn(Entry("huge", 1, 10_000));
        Assert.Null(cache.TryCheckOut("huge"));
        Assert.True(cache.GetStatistics().Bytes <= cache.Options.MaxBytes);
    }

    [Fact]
    public void An_entry_idle_past_MaxAge_is_dropped_at_check_out()
    {
        var cache = new G9CMemoryDigestCache(new G9CDigestCacheOptions { MaxAge = TimeSpan.FromMilliseconds(1) });
        cache.CheckIn(Entry("a", 1, 5));
        Thread.Sleep(20);

        Assert.Null(cache.TryCheckOut("a"));
        Assert.Equal(1, cache.GetStatistics().Expired);
        Assert.Equal(0, cache.GetStatistics().Entries);
    }

    private static G9CDigestCacheEntry Entry(string identity, long order, int keys)
    {
        var entry = new G9CDigestCacheEntry(identity, G9ESyncColumnType.Int64) { Order = order };
        for (var i = 0; i < keys; i++) entry.Set(G9CSyncKey.FromInt64(i), 1);
        return entry;
    }
}
