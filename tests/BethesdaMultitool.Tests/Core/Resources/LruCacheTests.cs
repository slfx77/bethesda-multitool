using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Resources;

public sealed class LruCacheTests
{
    private static LruCache<string, string> CreateCache(
        int? maxEntries = null,
        long? maxBytes = null,
        Func<string, string, long>? sizeOf = null,
        Action<string, string>? onEvicted = null)
    {
        return new LruCache<string, string>("TestLru", ResourceCategory.CpuCache, maxEntries, maxBytes, sizeOf,
            onEvicted);
    }

    [Fact]
    public void Evicts_least_recently_used_when_over_entry_cap()
    {
        var evicted = new List<string>();
        var cache = CreateCache(2, onEvicted: (key, _) => evicted.Add(key));

        cache.Set("a", "1");
        cache.Set("b", "2");
        Assert.True(cache.TryGet("a", out _)); // bump "a" to MRU
        cache.Set("c", "3"); // evicts "b", the LRU

        Assert.Equal(["b"], evicted);
        Assert.True(cache.ContainsKey("a"));
        Assert.True(cache.ContainsKey("c"));
    }

    [Fact]
    public void ContainsKey_does_not_bump_recency()
    {
        var cache = CreateCache(2);
        cache.Set("a", "1");
        cache.Set("b", "2");
        Assert.True(cache.ContainsKey("a")); // no bump — "a" stays LRU
        cache.Set("c", "3");

        Assert.False(cache.ContainsKey("a"));
        Assert.True(cache.ContainsKey("b"));
    }

    [Fact]
    public void TryPeek_returns_value_without_bumping_recency_or_counting()
    {
        var cache = CreateCache(2);
        cache.Set("a", "1");
        cache.Set("b", "2");

        Assert.True(cache.TryPeek("a", out var peeked)); // no bump — "a" stays LRU
        Assert.Equal("1", peeked);
        Assert.False(cache.TryPeek("missing", out _));

        var stats = cache.GetStats();
        Assert.Equal(0, stats.Hits);
        Assert.Equal(0, stats.Misses);

        cache.Set("c", "3"); // evicts "a": the peek must not have protected it
        Assert.False(cache.ContainsKey("a"));
        Assert.True(cache.ContainsKey("b"));
    }

    [Fact]
    public void Replacing_a_key_evicts_the_old_value_through_the_hook()
    {
        var evicted = new List<string>();
        var cache = CreateCache(4, onEvicted: (_, value) => evicted.Add(value));

        cache.Set("a", "old");
        cache.Set("a", "new");

        Assert.Equal(["old"], evicted);
        Assert.True(cache.TryGet("a", out var value));
        Assert.Equal("new", value);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Remove_hands_ownership_back_without_invoking_the_eviction_hook()
    {
        var evicted = new List<string>();
        var cache = CreateCache(4, onEvicted: (_, value) => evicted.Add(value));

        cache.Set("a", "1");
        Assert.True(cache.Remove("a"));
        Assert.False(cache.Remove("a"));
        Assert.Empty(evicted);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Remove_then_re_add_keeps_ordering_consistent()
    {
        // Pins the invariant behind the disappearing-cells bug: a removed key must be
        // fully detached from the recency list so its re-add cannot corrupt eviction order.
        var cache = CreateCache(2);
        cache.Set("a", "1");
        cache.Set("b", "2");
        Assert.True(cache.Remove("a"));
        cache.Set("a", "1again");
        cache.Set("c", "3"); // evicts "b" (LRU); "a" was re-added as MRU

        Assert.True(cache.ContainsKey("a"));
        Assert.False(cache.ContainsKey("b"));
        Assert.True(cache.ContainsKey("c"));
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void Byte_budget_evicts_until_under_cap_and_tracks_estimated_bytes()
    {
        var cache = CreateCache(maxBytes: 100, sizeOf: static (_, value) => long.Parse(value));

        cache.Set("a", "40");
        cache.Set("b", "40");
        Assert.Equal(80, cache.EstimatedBytes);

        cache.Set("c", "40"); // 120 > 100 → evict "a"
        Assert.Equal(80, cache.EstimatedBytes);
        Assert.False(cache.ContainsKey("a"));
    }

    [Fact]
    public void An_oversized_entry_survives_its_own_set_and_resides_alone()
    {
        var evicted = new List<string>();
        var cache = CreateCache(
            maxBytes: 100,
            sizeOf: static (_, value) => long.Parse(value),
            onEvicted: (key, _) => evicted.Add(key));

        cache.Set("small", "10");
        cache.Set("big", "500"); // evicts "small" but never the just-inserted "big"

        Assert.Equal(["small"], evicted);
        Assert.Equal(1, cache.Count);
        Assert.True(cache.ContainsKey("big"));
        Assert.Equal(500, cache.EstimatedBytes);
    }

    [Fact]
    public void TrimToBytes_and_TrimToCount_report_released_amounts()
    {
        var cache = CreateCache(sizeOf: static (_, value) => long.Parse(value));
        cache.Set("a", "10");
        cache.Set("b", "20");
        cache.Set("c", "30");

        Assert.Equal(10, cache.TrimToBytes(50)); // evicts "a" (LRU, 10 bytes)
        Assert.Equal(2, cache.Count);
        Assert.Equal(1, cache.TrimToCount(1)); // evicts "b"
        Assert.True(cache.ContainsKey("c"));
    }

    [Fact]
    public void Clear_and_Dispose_evict_everything_through_the_hook()
    {
        var evicted = new List<string>();
        var cache = CreateCache(onEvicted: (key, _) => evicted.Add(key));
        cache.Set("a", "1");
        cache.Set("b", "2");

        cache.Clear();
        Assert.Equal(0, cache.Count);
        Assert.Equal(0, cache.EstimatedBytes);
        Assert.Equal(2, evicted.Count);
    }

    [Fact]
    public void Stats_report_hits_misses_and_evictions()
    {
        var cache = CreateCache(1);
        cache.Set("a", "1");
        Assert.True(cache.TryGet("a", out _));
        Assert.False(cache.TryGet("missing", out _));
        cache.Set("b", "2"); // evicts "a"

        var stats = cache.GetStats();
        Assert.Equal(1, stats.Hits);
        Assert.Equal(1, stats.Misses);
        Assert.Equal(1, stats.Evictions);
        Assert.Equal(1, stats.EntryCount);
        Assert.Equal(0.5, stats.HitRate);
    }

    [Fact]
    public void UpdateSize_adjusts_the_byte_total_without_evicting()
    {
        // The resident-mesh pattern: a node is inserted empty and populated after an async decode,
        // so sizeOf at Set time can only ever see 0. UpdateSize must be able to push the cache OVER
        // its budget without firing onEvicted — evicting here would dispose the very value the
        // caller is midway through publishing.
        var evicted = new List<string>();
        var cache = CreateCache(maxBytes: 100, sizeOf: static (_, value) => long.Parse(value),
            onEvicted: (key, _) => evicted.Add(key));

        cache.Set("a", "10");
        Assert.Equal(10, cache.EstimatedBytes);

        Assert.True(cache.UpdateSize("a", 4096)); // far over the 100-byte budget
        Assert.Equal(4096, cache.EstimatedBytes);
        Assert.Empty(evicted);
        Assert.True(cache.ContainsKey("a"));
    }

    [Fact]
    public void UpdateSize_returns_false_for_an_absent_key()
    {
        var cache = CreateCache();
        Assert.False(cache.UpdateSize("never-inserted", 512));
        Assert.Equal(0, cache.EstimatedBytes);
    }

    [Fact]
    public void TrimToBytes_with_a_pin_predicate_skips_pinned_entries()
    {
        var evicted = new List<string>();
        var cache = CreateCache(sizeOf: static (_, value) => long.Parse(value),
            onEvicted: (key, _) => evicted.Add(key));
        cache.Set("pinned", "10");
        cache.Set("loose", "20");
        cache.Set("alsoLoose", "30"); // 60 resident; recency tail -> head: pinned, loose, alsoLoose

        // Target 45 is reachable by evicting exactly one entry, so this pins BOTH behaviours:
        // the pinned LRU tail is passed over, and the walk stops as soon as the target is met
        // rather than draining every unpinned entry.
        var released = cache.TrimToBytes(45, (key, _) => key == "pinned");

        Assert.Equal(20, released);
        Assert.Equal(["loose"], evicted);
        Assert.True(cache.ContainsKey("pinned"));
        Assert.True(cache.ContainsKey("alsoLoose"));
    }

    [Fact]
    public void TrimToBytes_terminates_when_every_entry_is_pinned()
    {
        // A condition-loop formulation ("while over target") spins forever here because the tail is
        // never removable. The single tail->head pass is what guarantees termination.
        var cache = CreateCache(sizeOf: static (_, value) => long.Parse(value));
        cache.Set("a", "100");
        cache.Set("b", "100");

        var released = cache.TrimToBytes(1, static (_, _) => true);

        Assert.Equal(0, released); // falling short is a legitimate outcome, not a retry signal
        Assert.Equal(200, cache.EstimatedBytes);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void SetMaxBytes_evicts_down_to_the_new_budget_and_raising_evicts_nothing()
    {
        var evicted = new List<string>();
        var cache = CreateCache(sizeOf: static (_, value) => long.Parse(value),
            onEvicted: (key, _) => evicted.Add(key));
        cache.Set("a", "10");
        cache.Set("b", "20");
        cache.Set("c", "30");

        cache.SetMaxBytes(50); // 60 resident -> evicts "a", the LRU
        Assert.Equal(["a"], evicted);
        Assert.Equal(50, cache.EstimatedBytes);

        cache.SetMaxBytes(4096); // raising is eviction-free
        Assert.Equal(["a"], evicted);
        Assert.Equal(2, cache.Count);
    }

    [Fact]
    public void SetMaxBytes_rejects_a_non_positive_budget()
    {
        var cache = CreateCache();
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SetMaxBytes(0));
    }

    [Fact]
    public void Evict_removes_one_specific_entry_through_the_hook()
    {
        // Policy-ordered eviction (the terrain sweep evicts farthest-first, not LRU-first) needs to
        // drop a CHOSEN key through onEvicted — Remove hands ownership to the caller instead and
        // would leak the GPU buffers.
        var evicted = new List<string>();
        var cache = CreateCache(sizeOf: static (_, value) => long.Parse(value),
            onEvicted: (key, _) => evicted.Add(key));
        cache.Set("lru", "10");
        cache.Set("middle", "20");
        cache.Set("mru", "30");

        var released = cache.Evict("mru"); // the MOST recently used — LRU order would never pick it

        Assert.Equal(30, released);
        Assert.Equal(["mru"], evicted);
        Assert.Equal(30, cache.EstimatedBytes);
        Assert.Equal(2, cache.Count);
        Assert.Equal(0, cache.Evict("never-inserted"));
    }

    [Fact]
    public void SnapshotKeys_is_a_copy_safe_to_evict_while_iterating()
    {
        var cache = CreateCache();
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");

        var keys = cache.SnapshotKeys();
        foreach (var key in keys)
        {
            cache.Evict(key); // would throw mid-enumeration against the live dictionary
        }

        Assert.Equal(3, keys.Length);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void WithSegment_is_reported_in_stats()
    {
        // A GpuResident row whose bytes are UPLOAD-heap must say so, or a VRAM total silently
        // absorbs system RAM.
        var cache = CreateCache().WithSegment(GpuMemorySegment.NonLocal);
        Assert.Equal(GpuMemorySegment.NonLocal, cache.GetStats().Segment);
        Assert.Equal(GpuMemorySegment.Unspecified, CreateCache().GetStats().Segment);
    }

    [Fact]
    public void Registers_and_unregisters_with_the_registry()
    {
        var registry = new ResourceRegistry();
        var cache = CreateCache().RegisterWith(registry, "test-instance");
        Assert.Equal("TestLru[test-instance]", Assert.Single(registry.GetSnapshot()).DisplayName);

        cache.Dispose();
        Assert.Empty(registry.GetSnapshot());
    }

    [Fact]
    public void Disposable_values_pattern_disposes_on_eviction()
    {
        // The CellMeshLruCache replacement pattern: onEvicted disposes GPU buffer holders.
        var disposed = new List<(int gx, int gy)>();
        var cache = new LruCache<(int gx, int gy), FakeMesh>(
            "CellMeshes", ResourceCategory.GpuResident,
            1,
            onEvicted: (key, mesh) =>
            {
                disposed.Add(key);
                mesh.Dispose();
            });

        var first = new FakeMesh();
        cache.Set((0, 0), first);
        cache.Set((1, 1), new FakeMesh());

        Assert.Equal([(0, 0)], disposed);
        Assert.True(first.IsDisposed);
    }

    [Fact]
    public void SetMaxEntries_raising_evicts_nothing()
    {
        var evicted = new List<string>();
        var cache = CreateCache(2, onEvicted: (key, _) => evicted.Add(key));
        cache.Set("a", "1");
        cache.Set("b", "2");

        cache.SetMaxEntries(10);

        Assert.Empty(evicted);
        Assert.True(cache.ContainsKey("a"));
        Assert.True(cache.ContainsKey("b"));
    }

    [Fact]
    public void SetMaxEntries_lowering_evicts_lru_first_through_the_hook()
    {
        var evicted = new List<string>();
        var cache = CreateCache(4, onEvicted: (key, _) => evicted.Add(key));
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");
        cache.Set("d", "4");

        cache.SetMaxEntries(2);

        Assert.Equal(["a", "b"], evicted); // LRU-tail-first
        Assert.True(cache.ContainsKey("c"));
        Assert.True(cache.ContainsKey("d"));
    }

    [Fact]
    public void SetMaxEntries_lowering_below_count_evicts_each_excess_keeping_mru()
    {
        var evicted = new List<string>();
        var cache = CreateCache(5, onEvicted: (key, _) => evicted.Add(key));
        foreach (var k in new[] { "a", "b", "c", "d", "e" })
        {
            cache.Set(k, k);
        }

        cache.SetMaxEntries(1);

        Assert.Equal(["a", "b", "c", "d"], evicted);
        Assert.True(cache.ContainsKey("e")); // only the MRU survives
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void SetMaxEntries_uses_lru_order_bumped_by_TryGet()
    {
        var evicted = new List<string>();
        var cache = CreateCache(3, onEvicted: (key, _) => evicted.Add(key));
        cache.Set("a", "1");
        cache.Set("b", "2");
        cache.Set("c", "3");
        Assert.True(cache.TryGet("a", out _)); // bump "a" to MRU

        cache.SetMaxEntries(1);

        Assert.Equal(["b", "c"], evicted);
        Assert.True(cache.ContainsKey("a")); // survives — resize respects recency, not insertion order
    }

    [Fact]
    public void SetMaxEntries_rejects_non_positive()
    {
        var cache = CreateCache(2);
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SetMaxEntries(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => cache.SetMaxEntries(-5));
    }

    private sealed class FakeMesh : IDisposable
    {
        public bool IsDisposed { get; private set; }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}