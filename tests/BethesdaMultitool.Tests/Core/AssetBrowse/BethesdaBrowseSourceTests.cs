using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Assets;
using Slfx77.Multitool.Core.Browsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Checks archive ownership at the shared browser adapter boundary.</summary>
public sealed class BethesdaBrowseSourceTests
{
    /// <summary>A decoder lease retains an old filesystem until work ends after source replacement.</summary>
    [Fact]
    public async Task SourceReplacement_RetainsFilesystemUntilDecoderLeaseEnds()
    {
        var firstFilesystem = new TrackingFilesystem();
        var first = Wrap(firstFilesystem);
        var secondFilesystem = new TrackingFilesystem();
        await using var browser = new BrowserSession();
        await browser.ReplaceAsync(first, TestContext.Current.CancellationToken);
        var snapshot = browser.Current!;
        var decoder = snapshot.AcquireLease();
        await browser.ReplaceAsync(Wrap(secondFilesystem), TestContext.Current.CancellationToken);
        Assert.True(snapshot.CancellationToken.IsCancellationRequested);
        Assert.False(browser.IsCurrent(snapshot));
        Assert.Equal(0, firstFilesystem.DisposalCount);
        await using (var stream = await first.OpenReadAsync(new AssetReference(first.Id, "test.bin"), TestContext.Current.CancellationToken))
            Assert.Equal(7, stream.ReadByte());
        await decoder.DisposeAsync();
        Assert.Equal(1, firstFilesystem.DisposalCount);
        Assert.Equal(0, secondFilesystem.DisposalCount);
    }

    /// <summary>Identical virtual paths from distinct sources cannot be accidentally substituted.</summary>
    [Fact]
    public async Task OpenRead_RejectsReferenceFromAnotherSource()
    {
        await using var source = Wrap(new TrackingFilesystem());
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await source.OpenReadAsync(new AssetReference("another-source", "test.bin"), TestContext.Current.CancellationToken));
    }

    private static BethesdaBrowseSource Wrap(TrackingFilesystem filesystem) =>
        new(new AssetBrowseSession(filesystem, "test", "test", AssetTreeBuilder.Build(filesystem, "test")));

    /// <summary>An in-memory filesystem that exposes precisely when its owning source is released.</summary>
    private sealed class TrackingFilesystem : IGameFileSystem
    {
        public int DisposalCount { get; private set; }
        public string Label => "test";
        public bool Exists(string path) => path == "test.bin";
        public GameFileEntry? TryStat(string path) => Exists(path) ? new GameFileEntry(path, 1, Label) : null;
        public byte[]? TryReadAllBytes(string path)
        {
            ObjectDisposedException.ThrowIf(DisposalCount > 0, this);
            return Exists(path) ? [7] : null;
        }
        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes) =>
            maximumBytes >= 1 && TryStat(path) is { } entry && TryReadAllBytes(path) is { } bytes
                ? new GameFileReadResult(entry, bytes) : null;
        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null) =>
            [new GameFileEntry("test.bin", 1, Label)];
        public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries) =>
            new(EnumerateFiles(prefix).Take(maximumEntries).ToArray(), maximumEntries < 1);
        public void Dispose() => DisposalCount++;
    }
}
