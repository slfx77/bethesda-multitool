using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Core.Imaging;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of the Redguard install (<c>RUN_BUCKET_B=1</c>). <c>WORLD.INI</c> is the
///     master registry the whole game hangs off, so these pin its shape and — the finding that
///     matters for every later reader — exactly which of its references are shipped.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RedguardRetailTests
{
    private static string RequireDataRoot()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Redguard();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Redguard"));
        return root!;
    }

    [Fact]
    public void WorldIniHasTheRetailShape()
    {
        var registry = RedguardWorldIni.Load(RequireDataRoot());

        Assert.Equal(29, registry.Worlds.Count);

        // The indices are NOT contiguous: 9, 10 and 16 are absent and 99 exists, so a reader that
        // keyed by position rather than by the bracket value would silently misattribute worlds.
        Assert.Equal(
            [0, 1, 2, 3, 4, 5, 6, 7, 8, 11, 12, 13, 14, 15, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 99],
            registry.Worlds.Select(w => w.Index));

        // Every world declares a map and a palette; only the 7 outdoor ones carry terrain and sky.
        Assert.All(registry.Worlds, world => Assert.NotNull(world.MapPath));
        Assert.All(registry.Worlds, world => Assert.NotNull(world.PalettePath));
        Assert.Equal(7, registry.Worlds.Count(w => w.TerrainPath is not null));
        Assert.Equal(7, registry.Worlds.Count(w => w.SkyPath is not null));

        Assert.NotNull(registry.StartWorld);
        Assert.Equal(0, registry.StartWorld!.Index);
    }

    [Fact]
    public void EveryShippedAssetClassResolvesAndTheTwoAbsentOnesNeverDo()
    {
        // The split is by KIND, not by world: .GXA/.RGM/.COL/.WLD all resolve, while every .NOO
        // node map and .BSI sprite is absent from the install AND from the .ROB archives. A caller
        // must treat those two as names only, never as files it can open.
        var root = RequireDataRoot();
        var registry = RedguardWorldIni.Load(root);

        var declared = registry.Worlds
            .SelectMany(w => new[] { w.MapPath, w.TerrainPath, w.PalettePath, w.SkyPath })
            .Where(path => path is not null)
            .Select(path => path!)
            .ToList();

        // The four typed accessors cover 29 maps + 29 palettes + 7 terrains + 7 skies.
        Assert.Equal(72, declared.Count);

        // The rest of the resolvable references are the 29 world_flash_filename .gxa entries, which
        // have no typed accessor — 101 shipped references in total.
        declared.AddRange(registry.Worlds
            .Select(w => w.Values.TryGetValue("world_flash_filename", out var flash) ? flash : null)
            .Where(path => path is not null)
            .Select(path => path!));
        Assert.Equal(101, declared.Count);

        Assert.All(declared, path => Assert.True(
            File.Exists(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar))),
            $"declared asset '{path}' is not shipped"));

        var nodeMaps = registry.Worlds.SelectMany(w => w.NodeMaps).ToList();
        Assert.Equal(119, nodeMaps.Count);
        Assert.All(nodeMaps, path => Assert.False(
            File.Exists(Path.Combine(root, path.Replace('\\', Path.DirectorySeparatorChar))),
            $"node map '{path}' unexpectedly exists — the absence rule has changed"));
    }

    [Fact]
    public void EveryPaletteIsTheEightBitColTheSharedReaderAlreadyHandles()
    {
        // Measured 2026-09-04: all 18 .COL files are 776 bytes, magic 0xB123, version 0, and every
        // component reaches 255 — so Redguard needs NO palette decoder of its own, and none of them
        // is the 6-bit VGA form that would need promoting. `art_pal.col` even shares Daggerfall's
        // name, which is the XnGine lineage showing through again.
        var root = RequireDataRoot();
        var art = Path.Combine(root, "3dart");
        Assert.SkipWhen(!Directory.Exists(art), RealAssetPaths.SkipMessage("Redguard 3dart"));

        var files = Directory.GetFiles(art, "*.COL");
        Assert.Equal(18, files.Length);

        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            Assert.Equal(Palette.ColFileLength, bytes.Length);
            Assert.Equal(Palette.DaggerfallColMagic, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(4)));

            // The shared reader accepts it and yields a full 256-entry palette.
            var palette = Palette.LoadArenaCol(bytes);
            Assert.Equal(Palette.EntryCount * 4, palette.Rgba.Length);

            // 8-bit, not 6-bit: a promoted 6-bit palette could never exceed 252.
            Assert.Contains(bytes.AsSpan(8).ToArray(), component => component > 252);
        }
    }

    [Fact]
    public async Task AnalyzerSynthesizesAWorldRecordPerWorld()
    {
        var root = RequireDataRoot();

        var result = await ClassicGameAnalyzer.LoadAsync(root, TestContext.Current.CancellationToken);

        var worlds = result.Records.GenericRecords
            .Where(r => r.RecordType == RedguardRecordSource.WorldRecordType)
            .ToList();

        Assert.Equal(29, worlds.Count);
        Assert.Equal(worlds.Count, worlds.Select(r => r.FormId).Distinct().Count());

        var island = worlds.First(r => r.EditorId == "ISLAND");
        Assert.Equal(@"MAPS\ISLAND.rgm", island.Fields["Map"]);
        Assert.Equal(true, island.Fields["MapPresent"]);
        Assert.Equal(@"MAPS\ISLAND.WLD", island.Fields["Terrain"]);
        Assert.Equal(2, island.Fields["RedbookTrack"]);

        // Unmodelled keys survive: 70 indexed keys exist and only a handful are typed.
        Assert.Equal("-63000, 30000, -10000, 28", island.Fields["world_sun"]);
    }
}
