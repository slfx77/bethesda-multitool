using System.Text;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Resources;

[Collection("Logger")]
public sealed class DiskBlobCacheTests : IDisposable
{
    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "DiskBlobCacheTests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Logger.Instance.Reset();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    [Fact]
    public void Round_trips_a_payload_and_counts_hits_and_misses()
    {
        var cache = new TestBlobCache(_directory, 1024 * 1024);

        Assert.False(cache.TryLoad("key", out _, out _));
        cache.Store("key", "payload");
        Assert.True(cache.TryLoad("key", out var payload, out var isNegative));

        Assert.Equal("payload", payload);
        Assert.False(isNegative);
        Assert.Equal(1, cache.Hits);
        Assert.Equal(1, cache.Misses);
        Assert.Equal(1, cache.Stores);
    }

    [Fact]
    public void Round_trips_a_negative_entry()
    {
        var cache = new TestBlobCache(_directory, 1024 * 1024);
        cache.Store("missing-asset", null);

        Assert.True(cache.TryLoad("missing-asset", out var payload, out var isNegative));
        Assert.Null(payload);
        Assert.True(isNegative);
    }

    [Fact]
    public void Decoder_version_mismatch_invalidates_and_deletes_the_file()
    {
        var writerCache = new TestBlobCache(_directory, 1024 * 1024);
        writerCache.Store("key", "payload");

        var readerCache = new TestBlobCache(_directory, 1024 * 1024, 2);
        Assert.False(readerCache.TryLoad("key", out _, out _));

        // The stale file must be gone so the writer can re-store cleanly.
        Assert.Empty(Directory.EnumerateFiles(_directory, "*.tblob", SearchOption.AllDirectories));
    }

    [Fact]
    public void Corrupt_files_are_deleted_and_reported_as_misses()
    {
        var cache = new TestBlobCache(_directory, 1024 * 1024);
        cache.Store("key", "payload");

        var file = Directory.EnumerateFiles(_directory, "*.tblob", SearchOption.AllDirectories).Single();
        File.WriteAllBytes(file, [1, 2, 3]);

        Assert.False(cache.TryLoad("key", out _, out _));
        Assert.False(File.Exists(file));
    }

    [Fact]
    public async Task Prune_measures_and_enforces_the_byte_cap()
    {
        var cache = new TestBlobCache(_directory, 600);
        for (var i = 0; i < 10; i++)
        {
            cache.Store($"key{i}", new string('x', 64));
            File.SetLastWriteTimeUtc(
                Directory.EnumerateFiles(_directory, "*.tblob", SearchOption.AllDirectories)
                    .OrderByDescending(File.GetLastWriteTimeUtc).First(),
                DateTime.UtcNow.AddMinutes(i));
        }

        cache.SchedulePrune();
        await WaitForAsync(() => cache.GetStats().EstimatedBytes is > 0 and <= 600);

        var stats = cache.GetStats();
        Assert.InRange(stats.EstimatedBytes, 1, 600);
        Assert.True(stats.EntryCount < 10);
        // Oldest files were pruned first; the newest survive.
        Assert.True(cache.TryLoad("key9", out _, out _));
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(50);
        }

        Assert.True(condition());
    }

    private sealed class TestBlobCache(string directory, long maxBytes, int decoderVersion = 1)
        : DiskBlobCache(
            "TestBlobCache", directory, maxBytes,
            Encoding.ASCII.GetBytes("TESTBLB\0"), 1, decoderVersion, ".tblob")
    {
        public bool TryLoad(string key, out string? payload, out bool isNegative)
        {
            return TryLoadCore(key, static reader => ReadString(reader, 1024), out payload, out isNegative);
        }

        public void Store(string key, string? payload)
        {
            StoreCore(key, payload, static (writer, value) => WriteString(writer, value, 1024));
        }
    }
}