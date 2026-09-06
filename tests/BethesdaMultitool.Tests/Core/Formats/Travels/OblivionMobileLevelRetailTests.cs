using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Bsa.Index;
using BethesdaMultitool.Core.Formats.Travels.OblivionMobile;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Travels;

/// <summary>
///     Real-asset coverage for the Oblivion Mobile level pair against the retail JAR (v1.0.10,
///     © 2006 Vir2L Studios / Bethesda Softworks). The JAR is plain PKZIP, so it opens through the
///     shared <see cref="ArchiveReader" /> like any other container.
///     <para>
///         The numbers pinned here were measured from the fixture on 2026-09-05 and are exact: the
///         JAR is a fixed file, so a drift in any of them is a decoder regression, not content
///         variance. Every claim below reproduced independently of the spec document.
///     </para>
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class OblivionMobileLevelRetailTests
{
    /// <summary>
    ///     The per-level census: stem, W, H, layer count, blocked cells, distinct tile ids and
    ///     placed tiles. Layer counts are 2 for the three 1x1 scripted-room placeholders, 3 for
    ///     l14_1, 4 for l08_1 / l10_1 / l13_clrl and 5 for the other ten.
    /// </summary>
    private static readonly (string Stem, int Width, int Height, int Layers, int Blocked, int Ids, int Placed)[]
        Census =
    [
        ("l01_1", 44, 59, 5, 420, 26, 2435),
        ("l01_r", 1, 1, 2, 0, 1, 1),
        ("l02_1", 33, 27, 5, 210, 18, 612),
        ("l03_1", 51, 53, 5, 375, 31, 2174),
        ("l04_1", 40, 39, 5, 280, 23, 1629),
        ("l05_1", 35, 35, 5, 414, 17, 1290),
        ("l06_1", 52, 53, 5, 350, 29, 2180),
        ("l06_a", 1, 1, 2, 0, 1, 1),
        ("l06_b", 1, 1, 2, 0, 1, 1),
        ("l07_1", 48, 34, 5, 399, 17, 1216),
        ("l08_1", 53, 50, 4, 1040, 12, 2077),
        ("l09_1", 57, 52, 5, 986, 26, 3132),
        ("l10_1", 42, 32, 4, 318, 17, 712),
        ("l11_1", 46, 40, 5, 705, 24, 2397),
        ("l12_1", 43, 52, 5, 873, 17, 2367),
        ("l13_clrl", 17, 22, 4, 61, 6, 402),
        ("l14_1", 15, 15, 3, 55, 6, 202),
    ];

    /// <summary>
    ///     Map to atlas. This is <b>not</b> derivable from either file — it was recovered from the
    ///     <c>.scr</c> scripts, where a length-prefixed <c>.jtm</c> path is always immediately
    ///     followed by the <c>.cml</c> to draw it with (28/28 occurrences, 16 maps, 0 conflicts).
    ///     Hence l04_1 drawing with l01's atlas, l06_1 with l03's and l10_1 with l02's.
    ///     <c>l01_r.jtm</c> is an orphan no script names and is deliberately absent here.
    /// </summary>
    private static readonly (string Jtm, string Cml)[] Pairing =
    [
        ("l01_1", "l01_1.cml"),
        ("l02_1", "l02_l2.cml"),
        ("l03_1", "l03_l3.cml"),
        ("l04_1", "l01_1.cml"),
        ("l05_1", "l05_l5.cml"),
        ("l06_1", "l03_l3.cml"),
        ("l06_a", "l11_l11.cml"),
        ("l06_b", "l02_l2.cml"),
        ("l07_1", "l05_l5.cml"),
        ("l08_1", "l08_l8.cml"),
        ("l09_1", "l09_l9.cml"),
        ("l10_1", "l02_l2.cml"),
        ("l11_1", "l11_l11.cml"),
        ("l12_1", "l12_l12.cml"),
        ("l13_clrl", "l13_clrl.cml"),
        ("l14_1", "l14_l14.cml"),
    ];

    private static ArchiveReader OpenJar()
    {
        var jar = RealAssetPaths.Travels.OblivionMobileJar();
        Assert.SkipWhen(
            jar is null,
            "The Oblivion Mobile JAR is not staged (Sample/Full_Builds/oblivion-repaired.jar).");
        return ArchiveReader.Open(jar!);
    }

    private static byte[] Read(ArchiveReader reader, string name)
    {
        var bytes = reader.ReadFile(name);
        Assert.SkipWhen(bytes is null, $"'{name}' is absent from the staged JAR.");
        return bytes!;
    }

    private static IReadOnlyList<string> NamesWithExtension(ArchiveReader reader, string extension)
    {
        return
        [
            .. reader.EnumerateFilePaths()
                .Where(p => p.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase),
        ];
    }

    [Fact]
    public void AllSeventeenTileMapsTileExactlyToEof()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        var names = NamesWithExtension(reader, ".jtm");
        Assert.Equal(17, names.Count);

        var totalCells = 0;
        var totalLayers = 0;
        var totalPlaced = 0;
        foreach (var name in names)
        {
            // Parse throws if the RLE stream does not land exactly on a layer boundary at EOF,
            // so reaching here at all is the "tiles exactly" claim.
            var map = OblivionMobileTileMap.Parse(Read(reader, name), name);
            totalCells += map.CellCount;
            totalLayers += map.LayerCount;
            totalPlaced += map.PlacedTileCount;
        }

        Assert.Equal(24_999, totalCells);
        Assert.Equal(71, totalLayers);
        Assert.Equal(22_828, totalPlaced);
    }

    [Fact]
    public void EveryTileMapMatchesItsMeasuredCensus()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        foreach (var (stem, width, height, layers, blocked, ids, placed) in Census)
        {
            var name = stem + ".jtm";
            var map = OblivionMobileTileMap.Parse(Read(reader, name), name);

            Assert.Equal(width, map.Width);
            Assert.Equal(height, map.Height);
            Assert.Equal(width * height, map.CellCount);
            Assert.Equal(layers, map.LayerCount);
            Assert.Equal(blocked, map.BlockedCellCount);
            Assert.Equal(ids, map.DistinctTileIds.Count);
            Assert.Equal(placed, map.PlacedTileCount);
        }
    }

    [Fact]
    public void TheThreeOneByOneMapsAreScriptedRoomPlaceholders()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        var expected = new (string Stem, byte Tile)[] { ("l01_r", 8), ("l06_a", 72), ("l06_b", 103) };
        foreach (var (stem, tile) in expected)
        {
            var name = stem + ".jtm";
            var bytes = Read(reader, name);
            Assert.Equal(4, bytes.Length);

            var map = OblivionMobileTileMap.Parse(bytes, name);
            Assert.Equal(1, map.Width);
            Assert.Equal(1, map.Height);
            Assert.Equal(2, map.LayerCount);
            Assert.Equal(0, map.Cell(0, 0, 0));
            Assert.Equal(tile, map.Cell(1, 0, 0));
        }
    }

    [Fact]
    public void PassabilityValuesAreAlwaysASubsetOfZeroToFive()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        var names = NamesWithExtension(reader, ".jtm");
        Assert.Equal(17, names.Count);

        foreach (var name in names)
        {
            var map = OblivionMobileTileMap.Parse(Read(reader, name), name);
            foreach (var value in map.Passability)
            {
                Assert.True(
                    value <= OblivionMobileTileMap.MaximumPassability,
                    $"{name} has passability {value}, outside the measured 0..5 set.");
            }
        }
    }

    [Fact]
    public void EveryTileMapIsWithinTheSixKilobyteResourceBuffer()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        var largest = 0;
        foreach (var name in NamesWithExtension(reader, ".jtm").Concat(NamesWithExtension(reader, ".cml")))
        {
            var bytes = Read(reader, name);
            Assert.True(bytes.Length <= 6144, $"{name} is {bytes.Length} bytes, past the engine's 6,144-byte buffer.");
            largest = Math.Max(largest, bytes.Length);
        }

        Assert.Equal(4210, largest);
    }

    [Fact]
    public void AllTwentyOneAtlasesParseToTheMeasuredCensus()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        var names = NamesWithExtension(reader, ".cml");
        Assert.Equal(21, names.Count);

        var entries = 0;
        var sprites = 0;
        var frames = 0;
        var wholeImages = 0;
        var tileIds = 0;
        foreach (var name in names)
        {
            // Parse rejects a duplicate id, so a clean parse is the "ids unique per file" claim;
            // the id count below is the independent cross-check on it.
            var atlas = OblivionMobileAtlas.Parse(Read(reader, name), name);
            Assert.Equal(string.Empty, atlas.Prefix);

            var whole = atlas.Sheets.Count(s => s.IsWholeImage);
            Assert.Equal(atlas.SpriteCount + whole, atlas.TileIds.Count);

            entries += atlas.Sheets.Count;
            sprites += atlas.SpriteCount;
            frames += atlas.FrameCount;
            wholeImages += whole;
            tileIds += atlas.TileIds.Count;
        }

        Assert.Equal(48, entries);
        Assert.Equal(513, sprites);
        Assert.Equal(612, frames);
        Assert.Equal(8, wholeImages);
        Assert.Equal(521, tileIds);
    }

    [Fact]
    public void EveryAtlasEntryHasAnAbsoluteNameAndAnEmptyPairTable()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        foreach (var name in NamesWithExtension(reader, ".cml"))
        {
            var atlas = OblivionMobileAtlas.Parse(Read(reader, name), name);
            foreach (var sheet in atlas.Sheets)
            {
                Assert.StartsWith("/", sheet.Path, StringComparison.Ordinal);
                Assert.Empty(sheet.Pairs);
            }
        }
    }

    [Fact]
    public void EveryFrameCarriesItsWidthAndHeightAndEverySpriteHeaderItsLoopFlag()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        foreach (var name in NamesWithExtension(reader, ".cml"))
        {
            var atlas = OblivionMobileAtlas.Parse(Read(reader, name), name);
            foreach (var sheet in atlas.Sheets)
            {
                foreach (var sprite in sheet.Sprites)
                {
                    Assert.NotNull(sprite.Attributes.Loop);
                    foreach (var frame in sprite.Frames)
                    {
                        Assert.NotEqual(0, frame.Width);
                        Assert.NotEqual(0, frame.Height);
                    }
                }
            }
        }
    }

    [Fact]
    public void LevelAtlasesAreSingleFrameAndNeverMirrored()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        var levelAtlases = Pairing.Select(p => p.Cml).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        Assert.Equal(10, levelAtlases.Count);

        foreach (var name in levelAtlases)
        {
            var atlas = OblivionMobileAtlas.Parse(Read(reader, name), name);
            Assert.StartsWith("/ts_lvl", atlas.Sheets[0].Path, StringComparison.Ordinal);
            Assert.Contains(atlas.Sheets, s => s.Path.Equals("/ts5.png", StringComparison.Ordinal));

            foreach (var sprite in atlas.Sheets.SelectMany(s => s.Sprites))
            {
                Assert.Single(sprite.Frames);
                Assert.False(sprite.Frames[0].Mirror, $"{name} sprite {sprite.Id} is mirrored.");
            }
        }
    }

    [Fact]
    public void EveryUsedTileIdResolvesInThePairedAtlas()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        using var reader = OpenJar();
        var atlases = new Dictionary<string, OblivionMobileAtlas>(StringComparer.OrdinalIgnoreCase);
        var highIds = new SortedSet<byte>();
        var mapsWithHighIds = 0;

        foreach (var (stem, cml) in Pairing)
        {
            var mapName = stem + ".jtm";
            var map = OblivionMobileTileMap.Parse(Read(reader, mapName), mapName);
            if (!atlases.TryGetValue(cml, out var atlas))
            {
                atlas = OblivionMobileAtlas.Parse(Read(reader, cml), cml);
                atlases[cml] = atlas;
            }

            var high = 0;
            foreach (var id in map.DistinctTileIds)
            {
                Assert.True(
                    atlas.TryGetTile(id, out var frame, out var sheetPath),
                    $"{mapName} uses tile id {id}, which {cml} does not define.");
                Assert.NotEqual(0, frame.Width);
                Assert.NotEmpty(sheetPath);

                if (id >= 128)
                {
                    highIds.Add(id);
                    high++;
                }
            }

            if (high > 0)
            {
                mapsWithHighIds++;
            }
        }

        // Ids are unsigned bytes: 11 distinct ids at or above 128 are in use across 8 maps, so a
        // signed-byte read would have made these negative and unresolvable.
        Assert.Equal(11, highIds.Count);
        Assert.Equal(8, mapsWithHighIds);
        Assert.Equal(new byte[] { 128, 129, 130, 131, 134, 135, 136, 137, 138, 139, 140 }, highIds);
    }

    [Fact]
    public void TheOrphanMapsOnlyTileExistsInTheHypothesisedAtlas()
    {
        BucketBTestGuard.SkipUnlessEnabled();

        // H1 in the spec is a hypothesis: no script names l01_r.jtm, so its atlas is inferred from
        // its single id (8) being a floor tile in l01_1.cml. Pin the fact, not the inference.
        using var reader = OpenJar();
        var map = OblivionMobileTileMap.Parse(Read(reader, "l01_r.jtm"), "l01_r.jtm");
        var atlas = OblivionMobileAtlas.Parse(Read(reader, "l01_1.cml"), "l01_1.cml");

        Assert.Equal(new byte[] { 8 }, map.DistinctTileIds);
        Assert.True(atlas.TryGetTile(8, out _, out _));
    }
}
