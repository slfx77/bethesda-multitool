using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Diagnostics;
using Slfx77.Multitool.Core.Caching;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Uses real Shared cache counters and the app registry to verify diagnostics without a second cache.</summary>
public sealed class ThumbnailCacheObservationTests
{
    /// <summary>Real positive and negative hits map to the unchanged CPU resource identity and trim policy.</summary>
    [Fact]
    public void RegistryReflectsRealCacheAndPressureReleasesActualBytes()
    {
        var registry = new ResourceRegistry();
        var observed = new ThumbnailCacheObservation(registry);
        var cache = NewCache();
        var identity = new object();
        observed.Attach(identity, () => cache.Statistics, cache.TrimToBytes);
        Assert.False(cache.TryGet(1, out _));
        cache.Set(1, new byte[9]);
        cache.Set(2, null);
        Assert.True(cache.TryGet(1, out _));
        Assert.True(cache.TryGet(2, out var missing));
        Assert.Null(missing);
        var row = Assert.Single(registry.GetSnapshot());
        Assert.Equal("AssetBrowserThumbnails", row.DisplayName);
        Assert.Equal(ResourceCategory.CpuCache, row.Category);
        Assert.Equal(10, row.Stats.EstimatedBytes);
        Assert.Equal(2, row.Stats.EntryCount);
        Assert.Equal(2, row.Stats.Hits);
        Assert.Equal(1, row.Stats.Misses);
        Assert.Equal(20, observed.TrimPriority);
        Assert.Equal(TrimAffinity.AnyThread, observed.TrimAffinity);
        Assert.Equal(9, observed.Trim(TrimLevel.Gentle));
        Assert.Equal(1, observed.GetStats().EstimatedBytes);
        Assert.Equal(1, observed.Trim(TrimLevel.Aggressive));
        Assert.Equal(0, observed.GetStats().EntryCount);
        observed.CompleteRetirement(identity);
        observed.CompleteBrowserRetirement();
    }

    /// <summary>Source changes keep one live row and cumulative real counters, including evictions and negative hits.</summary>
    [Fact]
    public void SuccessfulSourcesBecomeCumulativeScalarHistory()
    {
        var registry = new ResourceRegistry();
        var observed = new ThumbnailCacheObservation(registry);
        var first = NewCache();
        var firstIdentity = new object();
        observed.Attach(firstIdentity, () => first.Statistics, first.TrimToBytes);
        first.Set(1, null);
        Assert.True(first.TryGet(1, out _));
        first.Clear();
        var firstFinal = first.Statistics;
        observed.CompleteRetirement(firstIdentity);
        var second = NewCache();
        var secondIdentity = new object();
        observed.Attach(secondIdentity, () => second.Statistics, second.TrimToBytes);
        Assert.False(second.TryGet(4, out _));
        second.Set(4, new byte[7]);
        observed.CompleteRetirement(firstIdentity);
        var row = Assert.Single(registry.GetSnapshot());
        Assert.Equal("AssetBrowserThumbnails", row.DisplayName);
        Assert.Equal(7, row.Stats.EstimatedBytes);
        Assert.Equal(firstFinal.Hits + second.Statistics.Hits, row.Stats.Hits);
        Assert.Equal(firstFinal.Misses + second.Statistics.Misses, row.Stats.Misses);
        Assert.Equal(firstFinal.Evictions + second.Statistics.Evictions, row.Stats.Evictions);
        second.Clear();
        observed.CompleteRetirement(secondIdentity);
        Assert.Equal(0, observed.Trim(TrimLevel.Aggressive));
        observed.CompleteBrowserRetirement();
    }

    /// <summary>Failed or incomplete retirement cannot erase diagnostics, replace its source, or unregister the row.</summary>
    [Fact]
    public void UndrainedSourceRemainsObservableAndBlocksReplacement()
    {
        var registry = new ResourceRegistry();
        var observed = new ThumbnailCacheObservation(registry);
        var cache = NewCache();
        var identity = new object();
        observed.Attach(identity, () => cache.Statistics, cache.TrimToBytes);
        cache.Set(1, new byte[8]);
        Assert.Throws<InvalidOperationException>(() => observed.CompleteRetirement(identity));
        Assert.Throws<InvalidOperationException>(() => observed.Attach(new object(), () => default, _ => 0));
        Assert.Throws<InvalidOperationException>(observed.CompleteBrowserRetirement);
        Assert.Equal(8, Assert.Single(registry.GetSnapshot()).Stats.EstimatedBytes);
        Assert.Equal(8, observed.Trim(TrimLevel.Aggressive));
        Assert.Throws<InvalidOperationException>(observed.CompleteBrowserRetirement);
        observed.CompleteRetirement(identity);
        observed.CompleteBrowserRetirement();
    }

    /// <summary>A query captured before retirement sees final counters once, while later queries see scalar history once.</summary>
    [Fact]
    public async Task QueryAndRetirementCannotDoubleCountHistory()
    {
        var registry = new ResourceRegistry();
        var observed = new ThumbnailCacheObservation(registry);
        var cache = NewCache();
        cache.Set(1, null);
        Assert.True(cache.TryGet(1, out _));
        cache.Clear();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blockNext = 1;
        var identity = new object();
        observed.Attach(identity, () =>
        {
            if (Interlocked.Exchange(ref blockNext, 0) == 1)
            {
                entered.Set();
                release.Wait(TestContext.Current.CancellationToken);
            }
            return cache.Statistics;
        }, cache.TrimToBytes);
        var query = Task.Run(observed.GetStats, TestContext.Current.CancellationToken);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
            observed.CompleteRetirement(identity);
            Assert.Equal(1, observed.GetStats().Hits);
        }
        finally { release.Set(); }
        Assert.Equal(1, (await query).Hits);
        observed.CompleteBrowserRetirement();
    }

    /// <summary>Unregistration captures final history and repeat retirement cannot invent additional lookups or trims.</summary>
    [Fact]
    public void FinalRetirementPreservesActualCountersAndRejectsNewSources()
    {
        var registry = new ResourceRegistry();
        var observed = new ThumbnailCacheObservation(registry);
        var cache = NewCache();
        var identity = new object();
        observed.Attach(identity, () => cache.Statistics, cache.TrimToBytes);
        Assert.False(cache.TryGet(1, out _));
        observed.CompleteRetirement(identity);
        observed.CompleteBrowserRetirement();
        observed.CompleteBrowserRetirement();
        Assert.Empty(registry.GetSnapshot());
        var retired = Assert.Single(registry.GetRetiredSnapshot());
        Assert.Equal("AssetBrowserThumbnails", retired.DisplayName);
        Assert.Equal(1, retired.Stats.Misses);
        Assert.Equal(0, retired.Stats.EstimatedBytes);
        Assert.Equal(1, observed.GetStats().Misses);
        Assert.Equal(0, observed.Trim(TrimLevel.Aggressive));
        Assert.Throws<ObjectDisposedException>(() => observed.Attach(new object(), () => default, _ => 0));
    }

    /// <summary>Uses the same synchronized cache implementation as the Shared loader, with small visible byte charges.</summary>
    private static BoundedLruCache<int, byte[]?> NewCache() => new(8, 64, static bytes => bytes?.LongLength ?? 1);
}
