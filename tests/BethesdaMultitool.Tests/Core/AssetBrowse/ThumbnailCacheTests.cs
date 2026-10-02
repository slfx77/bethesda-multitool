using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Diagnostics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Verifies the application's shared cache adapter preserves source identity and diagnostic behavior.</summary>
public sealed class ThumbnailCacheTests
{
    /// <summary>Reuses a negative result only within its original source generation.</summary>
    [Fact]
    public void NegativePreviewDoesNotDecodeAgainAndNewSourceGenerationDoes()
    {
        using var cache = new ThumbnailCache();
        var first = new ThumbnailKey(Guid.NewGuid(), "textures/shared.dds", 96);
        var calls = 0;
        RgbaThumbnail? Render() { calls++; return null; }
        Assert.Null(cache.GetOrAdd(first, Render));
        Assert.Null(cache.GetOrAdd(first, Render));
        Assert.Equal(1, calls);
        Assert.True(cache.Contains(first));
        Assert.Null(cache.GetOrAdd(first with { SourceId = Guid.NewGuid() }, Render));
        Assert.Equal(2, calls);
    }

    /// <summary>Reports live cache counters and releases rebuildable memory through the existing diagnostics contract.</summary>
    [Fact]
    public void DiagnosticsAndMemoryPressureRemainAvailableThroughSharedCache()
    {
        var registry = new ResourceRegistry();
        using var cache = new ThumbnailCache(maxEntries: 4, maxBytes: 16).RegisterWith(registry);
        var first = new ThumbnailKey(Guid.NewGuid(), "textures/first.dds", 1);
        cache.GetOrAdd(first, () => new RgbaThumbnail(1, 1, [1, 2, 3, 255]));
        cache.GetOrAdd(first, () => throw new InvalidOperationException("Already cached"));
        cache.GetOrAdd(first with { VirtualPath = "textures/second.dds" }, () => new RgbaThumbnail(1, 1, [4, 5, 6, 255]));
        var row = Assert.Single(registry.GetSnapshot());
        Assert.Equal("AssetBrowserThumbnails", row.DisplayName);
        Assert.Equal(ResourceCategory.CpuCache, row.Category);
        Assert.Equal(8, row.Stats.EstimatedBytes);
        Assert.Equal(1, row.Stats.Hits);
        Assert.Equal(2, row.Stats.Misses);
        Assert.Equal(4, cache.Trim(TrimLevel.Gentle));
        Assert.Equal(4, cache.Trim(TrimLevel.Aggressive));
        Assert.Equal(0, cache.Count);
        cache.Dispose();
        Assert.Empty(registry.GetSnapshot());
    }
}
