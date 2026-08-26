using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Resources;

/// <summary>
///     The byte bound that replaced two caches which only ever grew.
///     <para>
///         Eviction here is safe only because the values are immutable and rebuildable, so the tests
///         that matter are about the bound actually binding, about the cache never destroying the
///         entry it was just asked to hold, and about a cached <c>null</c> staying a cache HIT — the
///         two real caches store "this cell has no texture data" as a null value, and treating that
///         as a miss would rebuild it on every single frame.
///     </para>
/// </summary>
public sealed class BoundedDerivedCacheTests
{
    /// <summary>Reference-identity keys, matching how the production caches key on CellRecord.</summary>
    private sealed class Key(string name)
    {
        public override string ToString() => name;
    }

    private static BoundedDerivedCache<Key, string> Create(long maxBytes) => new(maxBytes);

    [Fact]
    public void A_value_survives_until_the_ceiling_is_reached()
    {
        var cache = Create(1000);
        var a = new Key("a");

        Assert.True(cache.TryAdd(a, "value", 100, out var evicted));
        Assert.Equal(0, evicted);
        Assert.True(cache.TryGet(a, out var found));
        Assert.Equal("value", found);
        Assert.Equal(100, cache.Bytes);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Adding_past_the_ceiling_evicts_oldest_first()
    {
        var cache = Create(250);
        var a = new Key("a");
        var b = new Key("b");
        var c = new Key("c");

        cache.TryAdd(a, "a", 100, out _);
        cache.TryAdd(b, "b", 100, out _);
        cache.TryAdd(c, "c", 100, out var evicted);

        Assert.Equal(100, evicted);
        Assert.False(cache.TryGet(a, out _)); // oldest went first
        Assert.True(cache.TryGet(b, out _));
        Assert.True(cache.TryGet(c, out _));
        Assert.Equal(200, cache.Bytes);
        Assert.Equal(1, cache.EvictionCount);
    }

    [Fact]
    public void The_bytes_total_tracks_what_is_actually_held()
    {
        // The production caches subtract this from the resource registry, so a drift here would make
        // the registry report memory that had already been dropped.
        var cache = Create(500);
        for (var i = 0; i < 20; i++)
        {
            cache.TryAdd(new Key($"k{i}"), "v", 100, out _);
        }

        Assert.True(cache.Bytes <= 500, $"held {cache.Bytes} B against a 500 B ceiling");
        Assert.Equal(cache.Count * 100L, cache.Bytes);
    }

    [Fact]
    public void An_entry_larger_than_the_whole_budget_is_kept_rather_than_evicting_everything()
    {
        // The spin/self-destruct case. Without the guard, an oversized value would evict every other
        // entry trying to make room for something that can never fit — throwing the cache away and
        // still being over budget.
        var cache = Create(250);
        var small = new Key("small");
        var huge = new Key("huge");

        cache.TryAdd(small, "small", 100, out _);
        Assert.True(cache.TryAdd(huge, "huge", 10_000, out _));

        Assert.True(cache.TryGet(huge, out var found), "the entry just installed was evicted");
        Assert.Equal("huge", found);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void A_cached_null_is_a_hit_not_a_miss()
    {
        // Load-bearing: both production caches store null for "this cell has no data". If that read
        // as a miss, the builder would run again on every lookup for every such cell, forever.
        var cache = Create(1000);
        var key = new Key("empty");

        Assert.True(cache.TryAdd(key, null, 0, out _));

        Assert.True(cache.TryGet(key, out var value));
        Assert.Null(value);
    }

    [Fact]
    public void A_losing_race_adds_nothing_and_evicts_nothing()
    {
        var cache = Create(1000);
        var key = new Key("k");

        Assert.True(cache.TryAdd(key, "first", 100, out _));
        Assert.False(cache.TryAdd(key, "second", 100, out var evicted));

        Assert.Equal(0, evicted);
        Assert.Equal(100, cache.Bytes);
        Assert.True(cache.TryGet(key, out var value));
        Assert.Equal("first", value);
    }

    [Fact]
    public void A_non_positive_ceiling_means_unbounded()
    {
        // The escape hatch, and the old behaviour. Asserted so nobody has to guess what 0 does.
        var cache = Create(0);
        for (var i = 0; i < 1000; i++)
        {
            cache.TryAdd(new Key($"k{i}"), "v", 1_000_000, out _);
        }

        Assert.Equal(1000, cache.Count);
        Assert.Equal(0, cache.EvictionCount);
    }

    [Fact]
    public void Concurrent_adds_leave_the_byte_total_consistent_with_the_entry_count()
    {
        // The caches are written from background cell-build tasks while the render thread reads.
        var cache = Create(100_000);
        var keys = Enumerable.Range(0, 4000).Select(i => new Key($"k{i}")).ToArray();

        Parallel.ForEach(keys, key => cache.TryAdd(key, "v", 100, out _));

        Assert.True(cache.Bytes <= 100_000, $"held {cache.Bytes} B over the ceiling");
        Assert.Equal(cache.Count * 100L, cache.Bytes);
        Assert.True(cache.Count > 0);
    }
}
