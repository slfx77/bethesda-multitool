using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;
using BethesdaMultitool.Core.AssetBrowse;
using BethesdaMultitool.Core.Formats.Travels.Shadowkey;
using BethesdaMultitool.Core.Vfs;
using Slfx77.Multitool.Core.Browsing;
using Slfx77.Multitool.Core.Models;
using Xunit;

namespace BethesdaMultitool.Tests.Core.AssetBrowse;

/// <summary>Exercises the real browser/VFS ownership boundary with tiny synthetic Shadowkey packs.</summary>
public sealed class ShadowkeyPackPreviewSourceTests
{
    private const string PackPath = "nested\\models.huge";
    private const string IndexPath = "nested\\models.idx";
    private const string NamesPath = "nested\\models.txt";

    [Fact]
    public async Task DuplicateLabelsRetainDistinctSlotsAndExactBrowserGeneration()
    {
        await using var mounted = await MountAsync(new MemoryFileSystem(Files([Mesh(), Mesh(xShift: 20)])));
        var source = await OpenAsync(mounted);
        var first = source.CreateSelection(0);
        var second = source.CreateSelection(1);
        Assert.Equal(source.Entries[0].FileName, source.Entries[1].FileName);
        Assert.Equal("nested/models.huge", first.Reference.Path);
        Assert.Equal(first.Reference.Path, second.Reference.Path);
        Assert.Equal("shadowkey-slot:0", first.Reference.OccurrenceId);
        Assert.Equal("shadowkey-slot:1", second.Reference.OccurrenceId);
        Assert.Equal(mounted.Snapshot.Source.Id, second.Reference.SourceId);
        var firstScene = Assert.IsType<ModelDocument>(source.Prepare(first, TestContext.Current.CancellationToken).Scene);
        var secondScene = Assert.IsType<ModelDocument>(source.Prepare(second, TestContext.Current.CancellationToken).Scene);
        Assert.NotEqual(firstScene.SourceIdentity, secondScene.SourceIdentity);
        using var identity = JsonDocument.Parse(secondScene.SourceIdentity!);
        Assert.Equal(mounted.Snapshot.Generation, identity.RootElement.GetProperty("sourceGeneration").GetInt64());
        Assert.Equal(1, identity.RootElement.GetProperty("slot").GetInt32());
        Assert.Equal(new Vector3(20, 0, 0), secondScene.Meshes[0].Primitives[0].Vertices[0].Position);
        Assert.Equal(3, ((BethesdaBrowseSource)mounted.Snapshot.Source).Session.Root.Children[0].Children.Count);
    }

    [Fact]
    public async Task MissingNamesStillBrowsesIndexedSlotsWithoutInventedLabelsOrFiles()
    {
        await using var mounted = await MountAsync(new MemoryFileSystem(Files([Mesh()], withNames: false)));
        var source = await OpenAsync(mounted);
        Assert.Null(source.NamesProvenance);
        Assert.Null(Assert.Single(source.Entries).FileName);
        var result = source.Prepare(source.CreateSelection(0), TestContext.Current.CancellationToken);
        Assert.Equal("models.huge[0]", Assert.IsType<ModelDocument>(result.Scene).Name);
        Assert.Equal("nested/models.huge", result.Selection.Reference.Path);
    }

    [Fact]
    public async Task MissingRequiredIndexStopsBeforeReadingPack()
    {
        var files = Files([Mesh()]);
        files.Remove(IndexPath);
        var filesystem = new MemoryFileSystem(files);
        await using var mounted = await MountAsync(filesystem);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(mounted));
        Assert.Equal([IndexPath], filesystem.ReadPaths);
    }

    [Theory]
    [InlineData(IndexPath)]
    [InlineData(NamesPath)]
    public async Task MalformedCompanionDoesNotPublishAPartialCatalog(string path)
    {
        var files = Files([Mesh()]);
        files[path] = Encoding.UTF8.GetBytes("bad companion");
        await using var mounted = await MountAsync(new MemoryFileSystem(files));
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(mounted));
    }

    [Fact]
    public async Task PresentButUnreadableNamesDoNotBecomeAnonymousFallback()
    {
        var filesystem = new MemoryFileSystem(Files([Mesh()]));
        filesystem.Unreadable.Add(NamesPath);
        await using var mounted = await MountAsync(filesystem);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(mounted));
        Assert.Contains(NamesPath, error.Message);
    }

    [Fact]
    public async Task LayeredReadsRetainActualIndependentCompanionProvenance()
    {
        var files = Files([Mesh()]);
        var unreadable = new MemoryFileSystem(files, "unreadable-override");
        unreadable.Unreadable.UnionWith(files.Keys);
        var layered = new LayeredGameFileSystem(
        [
            unreadable,
            new MemoryFileSystem(new() { [PackPath] = files[PackPath] }, "pack-layer"),
            new MemoryFileSystem(new() { [IndexPath] = files[IndexPath] }, "index-layer"),
            new MemoryFileSystem(new() { [NamesPath] = files[NamesPath] }, "names-layer")
        ]);
        await using var mounted = await MountAsync(layered);
        Assert.Equal("unreadable-override", layered.TryStat(PackPath)!.Source);
        var source = await OpenAsync(mounted);
        Assert.Equal("pack-layer", source.PackProvenance.Source);
        Assert.Equal("index-layer", source.IndexProvenance.Source);
        Assert.Equal("names-layer", source.NamesProvenance!.Source);
        using var identity = JsonDocument.Parse(source.Prepare(source.CreateSelection(0), TestContext.Current.CancellationToken).Scene!.SourceIdentity!);
        Assert.Equal("pack-layer", identity.RootElement.GetProperty("packSource").GetString());
        Assert.Equal("index-layer", identity.RootElement.GetProperty("indexSource").GetString());
        Assert.Equal("names-layer", identity.RootElement.GetProperty("namesSource").GetString());
    }

    [Fact]
    public async Task CatalogOwnsRetainedBytesAndPreparationNeverMutatesVfsBuffers()
    {
        var files = Files([Mesh()]);
        var before = files.ToDictionary(pair => pair.Key, pair => pair.Value.ToArray());
        await using var mounted = await MountAsync(new MemoryFileSystem(files));
        var source = await OpenAsync(mounted);
        Assert.NotNull(source.Prepare(source.CreateSelection(0), TestContext.Current.CancellationToken).Scene);
        foreach (var pair in files)
        {
            Assert.Equal(before[pair.Key], pair.Value);
            Array.Clear(pair.Value);
        }
        // The seam deliberately returned shared arrays. Later caller mutation must not corrupt the catalog.
        var scene = Assert.IsType<ModelDocument>(source.Prepare(source.CreateSelection(0), TestContext.Current.CancellationToken).Scene);
        Assert.Equal(new Vector3(10, 0, 0), scene.Meshes[0].Primitives[0].Vertices[1].Position);
        Assert.Equal("same.bin", scene.Name);
    }

    [Fact]
    public async Task CorruptUnselectedSlotDoesNotBlockCatalogOrOtherRecords()
    {
        await using var mounted = await MountAsync(new MemoryFileSystem(Files([Mesh(), [255, 0]])));
        var source = await OpenAsync(mounted);
        Assert.Equal(2, source.Entries.Count);
        Assert.NotNull(source.Prepare(source.CreateSelection(0), TestContext.Current.CancellationToken).Scene);
        Assert.Throws<InvalidDataException>(() => source.Prepare(source.CreateSelection(1), TestContext.Current.CancellationToken));
        Assert.NotNull(source.Prepare(source.CreateSelection(0), TestContext.Current.CancellationToken).Scene);
    }

    [Fact]
    public async Task PreCanceledOpenReadsNothing()
    {
        var filesystem = new MemoryFileSystem(Files([Mesh()]));
        await using var mounted = await MountAsync(filesystem);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ShadowkeyPackPreviewSource.OpenAsync(mounted.Snapshot, mounted.Node, cancellation.Token));
        Assert.Empty(filesystem.ReadPaths);
    }

    [Fact]
    public async Task CancellationDuringIndexReadStopsBeforeReadingPack()
    {
        var filesystem = new MemoryFileSystem(Files([Mesh()]));
        await using var mounted = await MountAsync(filesystem);
        using var cancellation = new CancellationTokenSource();
        filesystem.AfterRead = _ => cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            ShadowkeyPackPreviewSource.OpenAsync(mounted.Snapshot, mounted.Node, cancellation.Token));
        Assert.Equal([IndexPath], filesystem.ReadPaths);
    }

    [Fact]
    public async Task SourceReplacementRetainsReadLeaseUntilCanceledWorkerReturns()
    {
        var filesystem = new MemoryFileSystem(Files([Mesh()]));
        await using var mounted = await MountAsync(filesystem);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        filesystem.AfterRead = _ =>
        {
            entered.TrySetResult();
            Assert.True(release.Wait(TimeSpan.FromSeconds(15)), "The test did not release its bounded read.");
        };
        var pending = OpenAsync(mounted);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            await mounted.Browser.ReplaceAsync(Wrap(new MemoryFileSystem(Files([Mesh()]))),
                TestContext.Current.CancellationToken);
            Assert.True(mounted.Snapshot.CancellationToken.IsCancellationRequested);
            Assert.Equal(0, filesystem.DisposalCount);
        }
        finally { release.Set(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(1, filesystem.DisposalCount);
        Assert.Equal([IndexPath], filesystem.ReadPaths);
    }

    [Fact]
    public async Task SamePathFromAnotherOpeningCannotReuseOldTreeOrCatalog()
    {
        await using var first = await MountAsync(new MemoryFileSystem(Files([Mesh()])));
        await using var second = await MountAsync(new MemoryFileSystem(Files([Mesh()])));
        var source = await OpenAsync(first);
        var other = await OpenAsync(second);
        await Assert.ThrowsAsync<ArgumentException>(() =>
            ShadowkeyPackPreviewSource.OpenAsync(second.Snapshot, first.Node, TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentException>(() => other.Prepare(source.CreateSelection(0), TestContext.Current.CancellationToken));
        var captured = source.CreateSelection(0);
        await first.Browser.DisposeAsync();
        Assert.ThrowsAny<OperationCanceledException>(() => source.Prepare(captured, TestContext.Current.CancellationToken));
        Assert.ThrowsAny<OperationCanceledException>(() => source.CreateSelection(0));
        Assert.NotNull(other.Prepare(other.CreateSelection(0), TestContext.Current.CancellationToken).Scene);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyAndAnimatedSlotsRetainExplicitDeclines(bool animated)
    {
        var record = animated ? Mesh(frames: 2, animated: true) : Array.Empty<byte>();
        await using var mounted = await MountAsync(new MemoryFileSystem(Files([record])));
        var source = await OpenAsync(mounted);
        var selection = source.CreateSelection(0);
        var result = source.Prepare(selection, TestContext.Current.CancellationToken);
        Assert.Same(selection, result.Selection);
        Assert.Null(result.Scene);
        Assert.Contains(animated ? "unrecorded units" : "slot is empty", result.UnsupportedReason!);
        Assert.Equal(animated ? 2 : 0, result.FrameCount);
    }

    [Fact]
    public async Task SelectedFrameSkinAndExplicitKeyingReachTheAdapterUnchanged()
    {
        await using var mounted = await MountAsync(new MemoryFileSystem(Files([Mesh(frames: 2, skins: 2)])));
        var source = await OpenAsync(mounted);
        var choice = source.CreateSelection(0, frame: 1, skin: 1, magentaIsTransparent: true);
        var preview = source.Prepare(choice, TestContext.Current.CancellationToken);
        Assert.Same(choice, preview.Selection);
        Assert.Equal(2, preview.FrameCount);
        Assert.Equal(2, preview.SkinCount);
        var scene = Assert.IsType<ModelDocument>(preview.Scene);
        Assert.Equal(SceneAlphaMode.Mask, Assert.Single(scene.Materials).AlphaMode);
        Assert.Equal(new Vector3(0, 0, 3), scene.Meshes[0].Primitives[0].Vertices[0].Position);
        Assert.EndsWith(".skin1", Assert.Single(scene.Images).Name);
        using var metadata = JsonDocument.Parse(scene.ExtrasJson!);
        Assert.Equal(1, metadata.RootElement.GetProperty("selectedFrame").GetInt32());
        Assert.Equal(1, metadata.RootElement.GetProperty("selectedSkin").GetInt32());
        Assert.True(metadata.RootElement.GetProperty("magentaIsTransparent").GetBoolean());
        Assert.Equal(SceneAlphaMode.Opaque,
            Assert.Single(source.Prepare(source.CreateSelection(0), TestContext.Current.CancellationToken).Scene!.Materials).AlphaMode);
    }

    [Fact]
    public async Task CanceledPreparationLeavesCatalogUsable()
    {
        await using var mounted = await MountAsync(new MemoryFileSystem(Files([Mesh()])));
        var source = await OpenAsync(mounted);
        var selection = source.CreateSelection(0);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        Assert.ThrowsAny<OperationCanceledException>(() => source.Prepare(selection, cancellation.Token));
        Assert.NotNull(source.Prepare(selection, TestContext.Current.CancellationToken).Scene);
    }

    [Fact]
    public async Task ParallelSelectionsOwnTheirSceneDataWithoutSharedParserCache()
    {
        await using var mounted = await MountAsync(new MemoryFileSystem(Files([Mesh(), Mesh(xShift: 20)])));
        var source = await OpenAsync(mounted);
        var choices = new[] { source.CreateSelection(0), source.CreateSelection(1) };
        var results = await Task.WhenAll(choices.Select(choice =>
            Task.Run(() => source.Prepare(choice, TestContext.Current.CancellationToken), TestContext.Current.CancellationToken)));
        Assert.NotSame(results[0].Scene, results[1].Scene);
        Assert.Equal(Vector3.Zero, results[0].Scene!.Meshes[0].Primitives[0].Vertices[0].Position);
        Assert.Equal(new Vector3(20, 0, 0), results[1].Scene!.Meshes[0].Primitives[0].Vertices[0].Position);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadsAreBoundedAndCannotReturnAnotherVirtualPath(bool wrongPath)
    {
        var filesystem = new MemoryFileSystem(Files([Mesh()]));
        if (wrongPath) { filesystem.ReturnedPath = "another/models.idx"; }
        else { filesystem.DeclaredSizes[PackPath] = ShadowkeyPackPreviewSource.MaximumPackBytes + 1L; }
        await using var mounted = await MountAsync(filesystem);
        await Assert.ThrowsAsync<InvalidDataException>(() => OpenAsync(mounted));
        Assert.Equal(ShadowkeyPackPreviewSource.MaximumIndexBytes, filesystem.Budgets[IndexPath]);
        if (!wrongPath)
        {
            Assert.Equal(ShadowkeyPackPreviewSource.MaximumPackBytes, filesystem.Budgets[PackPath]);
            Assert.DoesNotContain(NamesPath, filesystem.ReadPaths);
        }
    }

    private static Task<ShadowkeyPackPreviewSource> OpenAsync(Mounted mounted) =>
        ShadowkeyPackPreviewSource.OpenAsync(mounted.Snapshot, mounted.Node, TestContext.Current.CancellationToken);

    private static async Task<Mounted> MountAsync(IGameFileSystem filesystem)
    {
        var browser = new BrowserSession();
        var source = Wrap(filesystem);
        await browser.ReplaceAsync(source, TestContext.Current.CancellationToken);
        var node = Assert.Single(source.Session.Root.Children[0].Children,
            candidate => candidate.Name.Equals("models.huge", StringComparison.OrdinalIgnoreCase));
        return new Mounted(browser, browser.Current!, node);
    }

    private static BethesdaBrowseSource Wrap(IGameFileSystem filesystem) =>
        new(new AssetBrowseSession(filesystem, "synthetic", "synthetic", AssetTreeBuilder.Build(filesystem, "synthetic")));

    private sealed record Mounted(BrowserSession Browser, BrowserSnapshot Snapshot, AssetNode Node) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Browser.DisposeAsync();
    }

    // Same documented record layout as ShadowkeyPackTests; each payload is synthetic and under 128 bytes.
    private static byte[] Mesh(int frames = 1, int skins = 1, bool animated = false, int xShift = 0)
    {
        using var bytes = new MemoryStream();
        using var writer = new BinaryWriter(bytes, Encoding.UTF8, leaveOpen: true);
        void U16(int value) => writer.Write((ushort)value);
        foreach (var value in new[] { ShadowkeyMesh.FormatTag, frames, 3, 3, 1, 9, ShadowkeyMesh.HeaderTrailer })
        {
            U16(value);
        }
        for (var frame = 0; frame < frames; frame++)
        {
            U16(xShift); U16(0); U16(frame * 3);
            U16(xShift + 10); U16(0); U16(frame * 3);
            U16(xShift); U16(10); U16(frame * 3);
        }
        foreach (var value in new[] { 0, 0, 256, 0, 0, 256, 0, 1, 2, 0, 1, 2, skins, 1, 1 })
        {
            U16(value);
        }
        for (var skin = 0; skin < skins; skin++) { U16(skin == 0 ? 0x0F0F : 0x0123); }
        U16(animated ? 1 : 0);
        if (animated) { U16(0); U16(frames); U16(7); }
        writer.Flush();
        return bytes.ToArray();
    }

    private static Dictionary<string, byte[]> Files(byte[][] records, bool withNames = true)
    {
        var index = new byte[4 + records.Length * 8];
        BinaryPrimitives.WriteUInt32LittleEndian(index, (uint)records.Length);
        var offset = 0;
        for (var slot = 0; slot < records.Length; slot++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(4 + slot * 8), (uint)offset);
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(8 + slot * 8), (uint)records[slot].Length);
            offset += records[slot].Length;
        }
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase)
        {
            [IndexPath] = index, [PackPath] = records.SelectMany(record => record).ToArray()
        };
        if (withNames)
            files[NamesPath] = Encoding.UTF8.GetBytes(string.Join('\n',
                Enumerable.Range(0, records.Length).Select(slot => $"{slot} 0 0 0 same.bin")));
        return files;
    }

    /// <summary>The existing IGameFileSystem seam, deliberately returning retained arrays to test detachment.</summary>
    private sealed class MemoryFileSystem(Dictionary<string, byte[]> files, string label = "memory") : IGameFileSystem
    {
        private int _disposals;
        public string Label => label;
        public int DisposalCount => Volatile.Read(ref _disposals);
        internal HashSet<string> Unreadable { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Dictionary<string, long> DeclaredSizes { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal List<string> ReadPaths { get; } = new();
        internal Dictionary<string, long> Budgets { get; } = new(StringComparer.OrdinalIgnoreCase);
        internal Action<string>? AfterRead { get; set; }
        internal string? ReturnedPath { get; set; }
        public bool Exists(string path) => files.ContainsKey(VfsPath.Normalize(path));
        public GameFileEntry? TryStat(string path)
        {
            path = VfsPath.Normalize(path);
            return files.TryGetValue(path, out var bytes)
                ? new GameFileEntry(path, DeclaredSizes.GetValueOrDefault(path, bytes.Length), Label) : null;
        }
        public byte[]? TryReadAllBytes(string path) => throw new InvalidOperationException("Unbounded reads are forbidden.");
        public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
        {
            ObjectDisposedException.ThrowIf(DisposalCount != 0, this);
            path = VfsPath.Normalize(path);
            ReadPaths.Add(path);
            Budgets[path] = maximumBytes;
            var entry = TryStat(path);
            var result = entry is not null && entry.Size <= maximumBytes && !Unreadable.Contains(path)
                ? new GameFileReadResult(entry with { Path = ReturnedPath ?? entry.Path }, files[path]) : null;
            AfterRead?.Invoke(path);
            return result;
        }
        public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null) =>
            files.Keys.Where(path => VfsPath.MatchesPrefix(path, prefix is null ? null : VfsPath.Normalize(prefix)))
                .Select(path => TryStat(path)!);
        public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries)
        {
            var page = EnumerateFiles(prefix).Take(maximumEntries + 1).ToArray();
            return new GameFileEnumerationPage(page.Take(maximumEntries).ToArray(), page.Length > maximumEntries);
        }
        public void Dispose() => Interlocked.Increment(ref _disposals);
    }
}
