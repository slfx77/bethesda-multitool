using FalloutXbox360Utils.Core.Diagnostics;
using FalloutXbox360Utils.Core.Resources;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Resources;

public sealed class ConcurrentLazyCacheTests
{
    private static ConcurrentLazyCache<string, string> CreateCache(
        Func<string, string?> factory,
        Func<string, long>? sizeOf = null) =>
        new("TestLazy", ResourceCategory.CpuCache, factory, sizeOf, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Factory_runs_once_per_key_under_concurrency()
    {
        var calls = 0;
        var cache = CreateCache(key =>
        {
            Interlocked.Increment(ref calls);
            Thread.Sleep(10);
            return key + "!";
        });

        var results = new string?[16];
        Parallel.For(0, results.Length, i => results[i] = cache.GetOrCreate("k"));

        Assert.Equal(1, calls);
        Assert.All(results, static r => Assert.Equal("k!", r));
        Assert.Equal(1, cache.Misses);
        Assert.Equal(15, cache.Hits);
    }

    [Fact]
    public void Key_comparer_is_honored()
    {
        var calls = 0;
        var cache = CreateCache(key =>
        {
            Interlocked.Increment(ref calls);
            return key;
        });

        cache.GetOrCreate("Path/To/Asset");
        cache.GetOrCreate("path/to/asset");
        Assert.Equal(1, calls);
    }

    [Fact]
    public void Null_factory_result_is_cached_as_negative()
    {
        var calls = 0;
        var cache = CreateCache(_ =>
        {
            calls++;
            return null;
        });

        Assert.Null(cache.GetOrCreate("missing"));
        Assert.Null(cache.GetOrCreate("missing"));
        Assert.Equal(1, calls);
        Assert.Equal(1, cache.Count);
    }

    [Fact]
    public void Faulted_factory_entry_is_removed_so_the_next_request_retries()
    {
        var calls = 0;
        var cache = CreateCache(key =>
        {
            calls++;
            if (calls == 1)
            {
                throw new InvalidOperationException("first call fails");
            }

            return key;
        });

        Assert.Throws<InvalidOperationException>(() => cache.GetOrCreate("k"));
        Assert.Equal("k", cache.GetOrCreate("k"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Inject_replaces_and_Evict_removes()
    {
        var cache = CreateCache(static key => key);
        Assert.Equal("k", cache.GetOrCreate("k"));

        cache.Inject("k", "injected");
        Assert.Equal("injected", cache.GetOrCreate("k"));

        Assert.True(cache.Evict("k"));
        Assert.False(cache.Evict("k"));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void Release_drops_realized_positives_but_keeps_negatives()
    {
        var cache = CreateCache(static key => key == "missing" ? null : key);
        cache.GetOrCreate("k");
        cache.GetOrCreate("missing");

        Assert.True(cache.Release("k"));
        Assert.False(cache.Release("missing", keepNegative: true));
        Assert.Equal(1, cache.Count); // the negative survives
    }

    [Fact]
    public void Trim_aggressive_clears_positives_keeps_negatives_and_reports_bytes()
    {
        var cache = CreateCache(
            static key => key == "missing" ? null : key,
            sizeOf: static value => value.Length);
        cache.GetOrCreate("abcd");      // 4 bytes
        cache.GetOrCreate("xy");        // 2 bytes
        cache.GetOrCreate("missing");   // negative

        Assert.Equal(0, cache.Trim(TrimLevel.Gentle));
        Assert.Equal(3, cache.Count);

        Assert.Equal(6, cache.Trim(TrimLevel.Aggressive));
        Assert.Equal(1, cache.Count);
        Assert.Equal(0, cache.GetStats().EstimatedBytes);
    }

    [Fact]
    public void Byte_accounting_follows_creation_and_release()
    {
        var cache = CreateCache(static key => key, sizeOf: static value => value.Length);
        cache.GetOrCreate("abcd");
        Assert.Equal(4, cache.GetStats().EstimatedBytes);

        cache.Release("abcd");
        Assert.Equal(0, cache.GetStats().EstimatedBytes);
    }

    [Fact]
    public void RecordExternalHit_increments_hits()
    {
        var cache = CreateCache(static key => key);
        cache.RecordExternalHit();
        Assert.Equal(1, cache.Hits);
    }
}
