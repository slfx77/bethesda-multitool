using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Browsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Pins the byte-free surface decision the preview pane makes per selection; routing never reads a file.</summary>
public sealed class AssetPreviewRoutingTests
{
    /// <summary>Folders show nothing, whatever the source or presenter state.</summary>
    [Fact]
    public async Task FoldersRouteNone()
    {
        await using var mounted = await MountAsync(null, "sub\\x.dds");
        var root = mounted.Root;
        Assert.Equal(AssetNodeKind.Folder, root.Kind);

        Assert.Equal(AssetPreviewSurface.None, AssetPreviewRouting.Select(mounted.Snapshot, root, shadowkeyAvailable: true));
        Assert.Equal(AssetPreviewSurface.None, AssetPreviewRouting.Select(mounted.Snapshot, mounted.Leaf("sub\\x.dds").Parent!, false));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Textures and sprites take the image surface; a sky set outranks the sprite it also is.</summary>
    [Theory]
    [InlineData("x.dds", AssetPreviewSurface.Image)]
    [InlineData("y.frm", AssetPreviewSurface.Image)]
    [InlineData("TEXTURE.001", AssetPreviewSurface.Image)]
    [InlineData("SKY00.DAT", AssetPreviewSurface.Sky)]
    internal async Task PicturesRouteImageAndSkySetsRouteSky(string path, AssetPreviewSurface expected)
    {
        await using var mounted = await MountAsync(null, path);
        var node = mounted.Leaf(path);
        Assert.True(AssetThumbnailSource.CanRender(node));

        Assert.Equal(expected, AssetPreviewRouting.Select(mounted.Snapshot, node, shadowkeyAvailable: false));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>An FLC in an unclassified source plays natively; every other movie keeps the legacy surface.</summary>
    [Theory]
    [InlineData("movies\\same.flc", AssetPreviewSurface.NativeVideo)]
    [InlineData("movies\\intro.bik", AssetPreviewSurface.LegacyVideo)]
    [InlineData("movies\\cutscene.smk", AssetPreviewSurface.LegacyVideo)]
    [InlineData("movies\\DAG1.VID", AssetPreviewSurface.LegacyVideo)]
    internal async Task MoviesFollowTheVideoRoute(string path, AssetPreviewSurface expected)
    {
        await using var mounted = await MountAsync(null, path);

        Assert.Equal(expected, AssetPreviewRouting.Select(mounted.Snapshot, mounted.Leaf(path), shadowkeyAvailable: false));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Without a source opening an FLC is not admitted natively and takes the legacy surface, as the video route does.</summary>
    [Fact]
    public async Task FlcWithNullSnapshotRoutesLegacyVideo()
    {
        await using var mounted = await MountAsync(null, "movies\\same.flc");
        var node = mounted.Leaf("movies\\same.flc");

        Assert.Equal(ClassicVideoRoute.Legacy, ClassicVideoRouting.Select(null, node));
        Assert.Equal(AssetPreviewSurface.LegacyVideo, AssetPreviewRouting.Select(null, node, shadowkeyAvailable: false));
        Assert.Equal(AssetPreviewSurface.NativeVideo, AssetPreviewRouting.Select(mounted.Snapshot, node, shadowkeyAvailable: false));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Another game's FLC is not an Arena candidate and stays on the legacy surface.</summary>
    [Fact]
    public async Task DaggerfallFlcRoutesLegacyVideo()
    {
        await using var mounted = await MountAsync(GameProfiles.For(BethesdaGame.Daggerfall), "movies\\same.flc");

        Assert.Equal(AssetPreviewSurface.LegacyVideo,
            AssetPreviewRouting.Select(mounted.Snapshot, mounted.Leaf("movies\\same.flc"), shadowkeyAvailable: false));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Playable sounds take the audio surface; a sequenced music file this build cannot play shows nothing.</summary>
    [Theory]
    [InlineData("sound\\a.wav", AssetPreviewSurface.Audio)]
    [InlineData("sound\\b.voc", AssetPreviewSurface.Audio)]
    [InlineData("sound\\c.acm", AssetPreviewSurface.Audio)]
    [InlineData("sound\\MUSIC.XMI", AssetPreviewSurface.None)]
    internal async Task SoundsRouteAudioOnlyWhenPlayable(string path, AssetPreviewSurface expected)
    {
        await using var mounted = await MountAsync(null, path);
        var node = mounted.Leaf(path);
        Assert.Equal(AssetNodeKind.Audio, node.Kind);

        Assert.Equal(expected, AssetPreviewRouting.Select(mounted.Snapshot, node, shadowkeyAvailable: false));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Shadowkey's pack goes to its presenter only while one is attached; otherwise the probes decide.</summary>
    [Fact]
    public async Task ModelsHugeRoutesToTheShadowkeyPresenterOnlyWhenAvailable()
    {
        await using var mounted = await MountAsync(null, "models.huge");
        var node = mounted.Leaf("models.huge");
        Assert.True(ShadowkeyPackPreviewSource.IsCandidate(node));

        Assert.Equal(AssetPreviewSurface.ShadowkeyPack, AssetPreviewRouting.Select(mounted.Snapshot, node, shadowkeyAvailable: true));
        Assert.Equal(AssetPreviewSurface.ContentProbe, AssetPreviewRouting.Select(mounted.Snapshot, node, shadowkeyAvailable: false));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Meshes, maps and the extension-less Daggerfall ARCH3D records are settled by the probes, not by name.</summary>
    [Theory]
    [InlineData("meshes\\a.nif", AssetNodeKind.Model)]
    [InlineData("MONSTER.3D", AssetNodeKind.Model)]
    [InlineData("CITY.MIF", AssetNodeKind.Map)]
    [InlineData("44005", AssetNodeKind.Raw)]
    internal async Task ModelsMapsAndRawLeavesRouteToTheContentProbe(string path, AssetNodeKind kind)
    {
        await using var mounted = await MountAsync(null, path);
        var node = mounted.Leaf(path);
        Assert.Equal(kind, node.Kind);

        Assert.Equal(AssetPreviewSurface.ContentProbe, AssetPreviewRouting.Select(mounted.Snapshot, node, shadowkeyAvailable: true));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Text, plugins, archives and saves have no preview surface.</summary>
    [Theory]
    [InlineData("readme.txt", AssetNodeKind.Text)]
    [InlineData("FalloutNV.esm", AssetNodeKind.Plugin)]
    [InlineData("Meshes.bsa", AssetNodeKind.Archive)]
    [InlineData("Save1.fos", AssetNodeKind.Save)]
    internal async Task TextPluginsArchivesAndSavesRouteNone(string path, AssetNodeKind kind)
    {
        await using var mounted = await MountAsync(null, path);
        var node = mounted.Leaf(path);
        Assert.Equal(kind, node.Kind);

        Assert.Equal(AssetPreviewSurface.None, AssetPreviewRouting.Select(mounted.Snapshot, node, shadowkeyAvailable: true));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>A null node is rejected before any predicate runs.</summary>
    [Fact]
    public void NullNodeIsRejected()
    {
        Assert.Throws<ArgumentNullException>(() => AssetPreviewRouting.Select(null, null!, shadowkeyAvailable: false));
    }

    /// <summary>Opens one synthetic source through the real Shared source-retirement boundary.</summary>
    /// <param name="profile">The game profile the source declares, or null for an unclassified source.</param>
    /// <param name="paths">The virtual paths the source lists; routing must never read one.</param>
    private static async Task<Mounted> MountAsync(GameProfile? profile, params string[] paths)
    {
        var filesystem = new CountingFileSystem(paths);
        var browser = new BrowserSession();
        var source = new BethesdaBrowseSource(new AssetBrowseSession(filesystem, "routing", "routing",
            AssetTreeBuilder.Build(filesystem, "routing"), profile));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        return new Mounted(browser, browser.Current!, filesystem);
    }

    /// <summary>Retains the exact source opening for one routing test.</summary>
    private sealed record Mounted(BrowserSession Browser, BrowserSnapshot Snapshot, CountingFileSystem FileSystem) : IAsyncDisposable
    {
        /// <summary>The source's own tree root.</summary>
        internal AssetNode Root => ((BethesdaBrowseSource)Snapshot.Source).Session.Root;

        /// <summary>Finds the builder-owned leaf for a virtual path by walking the source's own tree.</summary>
        /// <param name="path">The backslash-separated virtual path of the leaf.</param>
        internal AssetNode Leaf(string path)
        {
            var node = Root;
            foreach (var segment in path.Split('\\'))
            {
                node = Assert.Single(node.Children, child => child.Name.Equals(segment, StringComparison.OrdinalIgnoreCase));
            }

            Assert.NotEqual(AssetNodeKind.Folder, node.Kind);
            return node;
        }

        /// <inheritdoc />
        public ValueTask DisposeAsync() => Browser.DisposeAsync();
    }

    /// <summary>Lists virtual paths and counts every payload read attempt, which routing must never make.</summary>
    private sealed class CountingFileSystem(string[] paths) : IGameFileSystem
    {
        private readonly FakeGameFileSystem _inner = new(paths.Select(path => (path, 16L)).ToArray());
        private int _reads;

        /// <inheritdoc />
        public string Label => _inner.Label;

        /// <summary>Observed payload read attempts, bounded or not.</summary>
        internal int ReadCount => Volatile.Read(ref _reads);

        /// <inheritdoc />
        public bool Exists(string path) => _inner.Exists(path);

        /// <inheritdoc />
        public GameFileEntry? TryStat(string path) => _inner.TryStat(path);

        /// <inheritdoc />
        public byte[]? TryReadAllBytes(string path)
        {
            Interlocked.Increment(ref _reads);
            return _inner.TryReadAllBytes(path);
        }

        /// <inheritdoc />
        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
        {
            Interlocked.Increment(ref _reads);
            return _inner.TryReadAllBytesBounded(path, maximumBytes);
        }

        /// <inheritdoc />
        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null) => _inner.EnumerateFiles(prefix);

        /// <inheritdoc />
        public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries) =>
            _inner.EnumerateFilesBounded(prefix, maximumEntries);

        /// <inheritdoc />
        public void Dispose() => _inner.Dispose();
    }
}
