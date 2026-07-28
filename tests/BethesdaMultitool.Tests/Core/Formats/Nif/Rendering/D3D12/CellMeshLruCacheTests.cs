using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Parity proof for the CellMeshLruCache → <see cref="LruCache{TKey,TValue}" /> migration:
///     the exact scenarios the bespoke cell-mesh cache was pinned by (bump on hit, evict
///     least-recently-used on insert overflow, dispose on eviction / replace / clear) must hold
///     unchanged for the shared LRU configured the way the renderers configure it.
/// </summary>
public sealed class CellMeshLruCacheTests
{
    private static LruCache<(int gx, int gy), TestEntry> CreateCache(int capacity)
    {
        return new LruCache<(int gx, int gy), TestEntry>("CellMeshLru", ResourceCategory.GpuResident,
            capacity,
            onEvicted: static (_, entry) => entry.Dispose());
    }

    [Fact]
    public void TryGet_OnEmptyCache_ReturnsFalseAndDefault()
    {
        var cache = CreateCache(4);

        var hit = cache.TryGet((0, 0), out var entry);

        Assert.False(hit);
        Assert.Null(entry);
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Insert_ThenTryGet_ReturnsTheEntry()
    {
        var cache = CreateCache(4);
        var inserted = new TestEntry(42);

        cache.Set((1, 2), inserted);

        Assert.True(cache.TryGet((1, 2), out var fetched));
        Assert.Same(inserted, fetched);
        Assert.False(inserted.Disposed);
    }

    [Fact]
    public void Insert_OverCapacity_EvictsLeastRecentlyUsedAndDisposesIt()
    {
        var cache = CreateCache(3);
        var e1 = new TestEntry(1);
        var e2 = new TestEntry(2);
        var e3 = new TestEntry(3);
        var e4 = new TestEntry(4);

        cache.Set((1, 0), e1); // oldest
        cache.Set((2, 0), e2);
        cache.Set((3, 0), e3);
        cache.Set((4, 0), e4); // overflow — evicts (1,0)

        Assert.Equal(3, cache.Count);
        Assert.False(cache.ContainsKey((1, 0)));
        Assert.True(cache.ContainsKey((4, 0)));
        Assert.True(e1.Disposed);
        Assert.False(e2.Disposed);
        Assert.False(e3.Disposed);
        Assert.False(e4.Disposed);
    }

    [Fact]
    public void TryGet_BumpsHitToMostRecentlyUsed_SoItSurvivesNextEviction()
    {
        var cache = CreateCache(3);
        var e1 = new TestEntry(1);
        var e2 = new TestEntry(2);
        var e3 = new TestEntry(3);
        var e4 = new TestEntry(4);

        cache.Set((1, 0), e1);
        cache.Set((2, 0), e2);
        cache.Set((3, 0), e3);

        // Bump (1,0) to most-recent. Now LRU order from oldest to newest is: 2, 3, 1.
        Assert.True(cache.TryGet((1, 0), out _));

        cache.Set((4, 0), e4); // evicts (2,0), not (1,0)

        Assert.True(cache.ContainsKey((1, 0)));
        Assert.False(cache.ContainsKey((2, 0)));
        Assert.False(e1.Disposed);
        Assert.True(e2.Disposed);
    }

    [Fact]
    public void Insert_ReplacingExistingKey_DisposesPreviousEntry()
    {
        var cache = CreateCache(4);
        var original = new TestEntry(1);
        var replacement = new TestEntry(2);

        cache.Set((5, 5), original);
        cache.Set((5, 5), replacement);

        Assert.True(original.Disposed);
        Assert.False(replacement.Disposed);
        Assert.True(cache.TryGet((5, 5), out var fetched));
        Assert.Same(replacement, fetched);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Clear_DisposesAllEntriesAndResetsCount()
    {
        var cache = CreateCache(4);
        var e1 = new TestEntry(1);
        var e2 = new TestEntry(2);
        cache.Set((0, 0), e1);
        cache.Set((1, 1), e2);

        cache.Clear();

        Assert.True(e1.Disposed);
        Assert.True(e2.Disposed);
        Assert.Equal(0, cache.Count);
        Assert.False(cache.ContainsKey((0, 0)));
    }

    [Fact]
    public void Constructor_RejectsNonPositiveCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateCache(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => CreateCache(-1));
    }

    private sealed class TestEntry : IDisposable
    {
        public TestEntry(int id)
        {
            Id = id;
        }

        public int Id { get; }
        public bool Disposed { get; private set; }

        public void Dispose()
        {
            Disposed = true;
        }
    }
}