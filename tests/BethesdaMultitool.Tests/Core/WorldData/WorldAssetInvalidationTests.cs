using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WorldAssetInvalidationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_source_path_never_reuses_another_selected_record_snapshot(bool captured)
    {
        using var files = new Files();
        files.Texture("textures/first.dds", 20, 30, 40);
        files.Texture("textures/second.dds", 80, 90, 100);
        var first = files.World("textures/first.dds", captured);
        var second = files.World("textures/second.dds", captured);
        var firstPalette = Assert.IsType<LandscapeTexturePalette>(LandscapeTexturePalette.GetOrCreate(first));
        Assert.Equal(((byte)20, (byte)30, (byte)40), firstPalette.Sample(1, 0, 0, 0));
        var secondPalette = Assert.IsType<LandscapeTexturePalette>(LandscapeTexturePalette.GetOrCreate(second));
        Assert.NotSame(firstPalette, secondPalette);
        Assert.Equal(((byte)80, (byte)90, (byte)100), secondPalette.Sample(1, 0, 0, 0));
        Assert.Same(firstPalette, LandscapeTexturePalette.GetOrCreate(first));
        Assert.Equal(captured, second.IsMemoryDump);
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("donor-order")]
    [InlineData("renames")]
    public void Warm_palette_is_retired_when_asset_generation_changes(string change)
    {
        using var files = new Files();
        var donorA = files.Directory("donor-a");
        var donorB = files.Directory("donor-b");
        files.Texture("donor-a/textures/tile.dds", 10, 20, 30);
        files.Texture("donor-b/textures/tile.dds", 60, 70, 80);
        var data = files.World("textures/tile.dds");
        data.AssetDataDirectories = [donorA, donorB];
        var old = Assert.IsType<LandscapeTexturePalette>(LandscapeTexturePalette.GetOrCreate(data));
        Assert.Equal(((byte)10, (byte)20, (byte)30), old.Sample(1, 0, 0, 0));
        var revision = data.AssetSnapshot;
        if (change == "bytes")
        {
            files.Texture("donor-a/textures/tile.dds", 60, 70, 80);
            data.RefreshAssetSources();
        }
        else if (change == "donor-order") data.AssetDataDirectories = [donorB, donorA];
        else data.MeshPathRenames = new Dictionary<string, string> { ["meshes/a.nif"] = "meshes/b.nif" };
        var current = Assert.IsType<LandscapeTexturePalette>(LandscapeTexturePalette.GetOrCreate(data));
        Assert.NotSame(revision, data.AssetSnapshot);
        Assert.NotSame(old, current);
        Assert.Null(old.Sample(1, 0, 0, 0)); // a retired worker cannot reload from its old source session
        var expected = change == "renames" ? ((byte)10, (byte)20, (byte)30) : ((byte)60, (byte)70, (byte)80);
        Assert.Equal(expected, current.Sample(1, 0, 0, 0));
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public void Queued_refreshes_only_reach_the_current_resident_binding(bool retire, int expected)
    {
        using var files = new Files();
        var data = files.World("textures/tile.dds");
        var queued = new List<Action>();
        var refreshed = 0;
        using var binding = new WorldAssetBinding(data, action => { queued.Add(action); return true; },
            () => refreshed++);
        data.RefreshAssetSources();
        data.RefreshAssetSources();
        if (retire) binding.Dispose();
        foreach (var action in queued) action();
        Assert.Equal(expected, refreshed);
    }

    [Theory]
    [InlineData("textures/new.dds", true)]
    [InlineData("Data/meshes/new.nif", true)]
    [InlineData("Data/Textures.bsa", true)]
    [InlineData("geometries/new.mesh", true)]
    [InlineData("export/preview.png", false)]
    [InlineData("runtime-guest-engine.log", false)]
    public void Watcher_filters_dependencies_from_adjacent_outputs(string relative, bool expected)
    {
        var root = Path.GetFullPath("asset-root");
        Assert.Equal(expected, WorldAssetChangeMonitor.IsAssetPath(root, Path.Combine(root, relative)));
    }

    [Fact]
    public async Task Live_loose_texture_change_advances_the_bound_revision()
    {
        using var files = new Files();
        files.Texture("textures/tile.dds", 10, 20, 30);
        var data = files.World("textures/tile.dds");
        var old = data.AssetSnapshot;
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        data.AssetSourcesChanged += (_, _) => changed.TrySetResult();
        using var watch = data.WatchAssetChanges();
        files.Texture("textures/tile.dds", 60, 70, 80);
        await changed.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.NotSame(old, data.AssetSnapshot);
        Assert.Empty(data.UnwatchedAssetRoots);
    }

    [Fact]
    public void Unavailable_watch_root_preserves_explicit_refresh()
    {
        using var files = new Files();
        var data = files.World("textures/tile.dds");
        data.AssetDataDirectories = [Path.Combine(files.Root, "missing")];
        using var watch = data.WatchAssetChanges();
        Assert.Single(data.UnwatchedAssetRoots);
        var old = data.AssetSnapshot;
        data.RefreshAssetSources();
        Assert.NotSame(old, data.AssetSnapshot);
    }

    private sealed class Files : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "bmt-world-assets-" + Guid.NewGuid().ToString("N"));
        private readonly List<WorldViewData> _worlds = [];
        internal Files() => System.IO.Directory.CreateDirectory(Root);
        internal string Directory(string relative)
        {
            var path = Path.Combine(Root, relative);
            System.IO.Directory.CreateDirectory(path);
            return path;
        }

        internal WorldViewData World(string texture, bool captured = false)
        {
            var data = new WorldViewData
            {
                SourceFilePath = Path.Combine(Root, "selected.esm"),
                Worldspaces = [], InteriorCells = [], BoundsIndex = [], CategoryIndex = [],
                Resolver = FormIdResolver.Empty, MapMarkers = [], MarkersByWorldspace = [],
                AllCells = [], CellByFormId = [], PlacedRefs = PlacedRefIndex.Empty,
                UnlinkedExteriorCells = [], UnlinkedMapMarkers = [], IsMemoryDump = captured,
                LandTexturesByFormId = new Dictionary<uint, LandscapeTextureRecord>
                {
                    [1] = new() { FormId = 1, TextureSetFormId = 2 }
                },
                TextureSetsByFormId = new Dictionary<uint, TextureSetRecord>
                {
                    [2] = new() { FormId = 2, DiffuseTexture = texture }
                }
            };
            _worlds.Add(data);
            return data;
        }

        internal void Texture(string relative, byte r, byte g, byte b)
        {
            var path = Path.Combine(Root, relative);
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var bytes = new byte[132];
            "DDS "u8.CopyTo(bytes);
            Word(4, 124); Word(8, 0x100F); Word(12, 1); Word(16, 1); Word(20, 4); Word(28, 1);
            Word(76, 32); Word(80, 0x41); Word(88, 32);
            Word(92, 0xFF); Word(96, 0xFF00); Word(100, 0xFF0000); Word(104, 0xFF000000); Word(108, 0x1000);
            bytes[128] = r; bytes[129] = g; bytes[130] = b; bytes[131] = 255;
            File.WriteAllBytes(path, bytes);
            void Word(int offset, uint value) => BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
        }

        public void Dispose()
        {
            foreach (var data in _worlds) LandscapeTexturePalette.Release(data);
            System.IO.Directory.Delete(Root, recursive: true);
        }
    }
}
