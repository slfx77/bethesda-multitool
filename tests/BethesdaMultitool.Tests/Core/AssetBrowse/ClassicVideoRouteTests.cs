using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Browsing;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Pins the one routing decision the asset browser makes for movies; routing never reads a byte.</summary>
public sealed class ClassicVideoRouteTests
{
    /// <summary>Arena's FLC and CEL leaves take the native route.</summary>
    /// <param name="path">The virtual path of the selected leaf.</param>
    [Theory]
    [InlineData("movies\\KING.FLC")]
    [InlineData("movies\\MAGE.CEL")]
    public async Task ArenaFlcAndCelLeavesRouteNative(string path)
    {
        await using var mounted = await MountAsync(GameProfiles.For(BethesdaGame.Arena), path);
        Assert.Equal(ClassicVideoRoute.Native, ClassicVideoRouting.Select(mounted.Snapshot, mounted.Leaf(path)));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>A source with no profile, a loose folder or a plain archive, admits its FLC leaves natively.</summary>
    [Fact]
    public async Task UnclassifiedSourceFlcRoutesNative()
    {
        await using var mounted = await MountAsync(null, "movies\\same.flc");
        Assert.Equal(ClassicVideoRoute.Native, ClassicVideoRouting.Select(mounted.Snapshot, mounted.Leaf("movies\\same.flc")));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Every other movie extension keeps the frame-timer preview, even inside an Arena source.</summary>
    /// <param name="path">The virtual path of the selected leaf.</param>
    [Theory]
    [InlineData("movies\\DAG1.VID")]
    [InlineData("movies\\intro.bik")]
    [InlineData("movies\\cutscene.smk")]
    [InlineData("movies\\iplogo.mve")]
    public async Task VidBinkSmackerAndMveRouteLegacy(string path)
    {
        await using var mounted = await MountAsync(GameProfiles.For(BethesdaGame.Arena), path);
        Assert.Equal(ClassicVideoRoute.Legacy, ClassicVideoRouting.Select(mounted.Snapshot, mounted.Leaf(path)));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Another game's FLC is not an Arena candidate and stays on the legacy route.</summary>
    [Fact]
    public async Task NonArenaProfileFlcRoutesLegacy()
    {
        await using var mounted = await MountAsync(GameProfiles.For(BethesdaGame.Daggerfall), "movies\\same.flc");
        Assert.Equal(ClassicVideoRoute.Legacy, ClassicVideoRouting.Select(mounted.Snapshot, mounted.Leaf("movies\\same.flc")));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Folders and non-movie leaves hide every video surface.</summary>
    [Fact]
    public async Task FoldersAndNonMoviesRouteNone()
    {
        await using var mounted = await MountAsync(GameProfiles.For(BethesdaGame.Arena), "movies\\same.flc", "text\\readme.txt");
        var root = ((BethesdaBrowseSource)mounted.Snapshot.Source).Session.Root;
        Assert.Equal(AssetNodeKind.Folder, root.Kind);
        Assert.Equal(ClassicVideoRoute.None, ClassicVideoRouting.Select(mounted.Snapshot, root));
        var folder = mounted.Leaf("movies\\same.flc").Parent!;
        Assert.Equal(AssetNodeKind.Folder, folder.Kind);
        Assert.Equal(ClassicVideoRoute.None, ClassicVideoRouting.Select(mounted.Snapshot, folder));
        Assert.Equal(ClassicVideoRoute.None, ClassicVideoRouting.Select(mounted.Snapshot, mounted.Leaf("text\\readme.txt")));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Without a source opening nothing is admitted natively; a playable movie still takes the legacy route.</summary>
    [Fact]
    public async Task NullSnapshotNeverRoutesNative()
    {
        await using var mounted = await MountAsync(GameProfiles.For(BethesdaGame.Arena), "movies\\same.flc", "text\\readme.txt");
        Assert.Equal(ClassicVideoRoute.Legacy, ClassicVideoRouting.Select(null, mounted.Leaf("movies\\same.flc")));
        Assert.Equal(ClassicVideoRoute.None, ClassicVideoRouting.Select(null, mounted.Leaf("text\\readme.txt")));
        Assert.Equal(0, mounted.FileSystem.ReadCount);
    }

    /// <summary>Opens one synthetic source through the real Shared source-retirement boundary.</summary>
    /// <param name="profile">The game profile the source declares, or null for an unclassified source.</param>
    /// <param name="paths">The virtual paths the source lists; none is ever read.</param>
    private static async Task<Mounted> MountAsync(GameProfile? profile, params string[] paths)
    {
        var filesystem = new RoutingFileSystem(paths);
        var browser = new BrowserSession();
        var source = new BethesdaBrowseSource(new AssetBrowseSession(filesystem, "synthetic", "synthetic",
            AssetTreeBuilder.Build(filesystem, "synthetic"), profile));
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        return new Mounted(browser, browser.Current!, filesystem);
    }

    /// <summary>Retains the exact source opening for one routing test.</summary>
    private sealed record Mounted(BrowserSession Browser, BrowserSnapshot Snapshot, RoutingFileSystem FileSystem)
        : IAsyncDisposable
    {
        /// <summary>Finds the builder-owned leaf for a virtual path by walking the source's own tree.</summary>
        /// <param name="path">The backslash-separated virtual path of the leaf.</param>
        internal AssetNode Leaf(string path)
        {
            var node = ((BethesdaBrowseSource)Snapshot.Source).Session.Root;
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

    /// <summary>Lists virtual paths without payloads and counts every read attempt, which routing must never make.</summary>
    private sealed class RoutingFileSystem(string[] paths) : IGameFileSystem
    {
        private readonly string[] _paths = [.. paths.Select(VfsPath.Normalize)];
        private int _reads;

        /// <inheritdoc />
        public string Label => "routing";

        /// <summary>Observed payload read attempts, bounded or not.</summary>
        internal int ReadCount => Volatile.Read(ref _reads);

        /// <inheritdoc />
        public bool Exists(string path) => TryStat(path) is not null;

        /// <inheritdoc />
        public GameFileEntry? TryStat(string path)
        {
            var normalized = VfsPath.Normalize(path);
            var match = _paths.FirstOrDefault(candidate => VfsPath.Comparer.Equals(candidate, normalized));
            return match is null ? null : new GameFileEntry(match, 16, Label);
        }

        /// <inheritdoc />
        public byte[]? TryReadAllBytes(string path)
        {
            Interlocked.Increment(ref _reads);
            return null;
        }

        /// <inheritdoc />
        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
        {
            Interlocked.Increment(ref _reads);
            return null;
        }

        /// <inheritdoc />
        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
        {
            var normalizedPrefix = prefix is null ? null : VfsPath.Normalize(prefix);
            return _paths.Where(path => VfsPath.MatchesPrefix(path, normalizedPrefix))
                .Select(path => new GameFileEntry(path, 16, Label));
        }

        /// <inheritdoc />
        public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries)
        {
            var entries = EnumerateFiles(prefix).ToArray();
            return new GameFileEnumerationPage(entries.Take(maximumEntries).ToArray(), entries.Length > maximumEntries);
        }

        /// <inheritdoc />
        public void Dispose()
        {
        }
    }
}
