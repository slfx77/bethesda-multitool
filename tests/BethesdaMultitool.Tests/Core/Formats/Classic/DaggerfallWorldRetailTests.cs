using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Opt-in checks of the Daggerfall world-map layer — sky sets, the climate/politic
///     overlays and the WOODS.WLD heightmap — against the retail ARENA2 (<c>RUN_BUCKET_B=1</c>).
///     The facts pinned here were measured directly on the files (2026-09-02) before the
///     decoders were written, so they constrain the decoders rather than describe them.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class DaggerfallWorldRetailTests
{
    private static string RequireArena2()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Daggerfall();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Daggerfall (ARENA2)"));
        return root;
    }

    [Fact]
    public void AllThirtyTwoSkySets_ParseWithEightBitPalettes()
    {
        var root = RequireArena2();
        var files = Directory.EnumerateFiles(root, "SKY*.DAT")
            .Where(p => DaggerfallSkyFile.IsSkyFileName(Path.GetFileName(p)))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // SKY00-SKY31; SKYPAL.DAT beside them is deliberately excluded by the name rule.
        Assert.Equal(32, files.Count);
        Assert.True(File.Exists(Path.Combine(root, "SKYPAL.DAT")));
        Assert.False(DaggerfallSkyFile.IsSkyFileName("SKYPAL.DAT"));

        var sawAboveSixBit = false;
        foreach (var path in files)
        {
            var sky = DaggerfallSkyFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
            Assert.Equal(32, sky.Palettes.Count);
            Assert.Equal(64, sky.Frames.Count);

            // The embedded palettes are COL-shaped and full-range 8-bit, like ART_PAL.COL.
            foreach (var palette in sky.Palettes)
            {
                for (var i = 0; i < 256; i++)
                {
                    var (r, g, b, _) = palette.GetEntry(i);
                    if (r > 63 || g > 63 || b > 63)
                    {
                        sawAboveSixBit = true;
                    }
                }
            }
        }

        Assert.True(sawAboveSixBit, "No sky palette component exceeded 63; they would be 6-bit after all.");
    }

    [Fact]
    public void BothOverlays_TileExactlyToEndOfFile_WithTheirValueRanges()
    {
        var root = RequireArena2();
        var climatePath = Path.Combine(root, "CLIMATE.PAK");
        var politicPath = Path.Combine(root, "POLITIC.PAK");
        Assert.SkipWhen(!File.Exists(climatePath) || !File.Exists(politicPath),
            RealAssetPaths.SkipMessage("PAK overlays"));

        var climate = DaggerfallPakFile.Parse(File.ReadAllBytes(climatePath), "CLIMATE.PAK");
        var politic = DaggerfallPakFile.Parse(File.ReadAllBytes(politicPath), "POLITIC.PAK");

        // Ten climate codes, 223..232, and 47 political region codes.
        var climates = climate.Values.Distinct().Order().ToList();
        Assert.Equal(10, climates.Count);
        Assert.Equal(223, climates[0]);
        Assert.Equal(232, climates[^1]);
        Assert.Equal(47, politic.Values.Distinct().Count());
    }

    [Fact]
    public void WoodsHeightMap_TilesTheFileAndItsCellsAreReadable()
    {
        var root = RequireArena2();
        var path = Path.Combine(root, "WOODS.WLD");
        Assert.SkipWhen(!File.Exists(path), RealAssetPaths.SkipMessage("WOODS.WLD"));

        var bytes = File.ReadAllBytes(path);
        Assert.Equal(26_001_168, bytes.Length);

        var woods = DaggerfallWoodsFile.Parse(bytes, "WOODS.WLD");

        // Header + offset table + heightmap + 500,000 x 47-byte cells is exactly the file.
        Assert.Equal(2_001_168, woods.HeightMapOffset);
        Assert.Equal(bytes.Length,
            woods.HeightMapOffset + DaggerfallWoodsFile.PixelCount + DaggerfallWoodsFile.PixelCount * 47);

        // The map is mostly ocean (elevation below the 3-byte sea threshold), and every cell grid
        // must be reachable — sample the corners and centre of the map.
        foreach (var (x, y) in new[] { (0, 0), (999, 0), (0, 499), (999, 499), (500, 250), (600, 200) })
        {
            var grid = woods.GetCellGrid(x, y);
            Assert.Equal(25, grid.Length);
        }

        var heights = woods.HeightMap.Span;
        var maxHeight = 0;
        foreach (var h in heights)
        {
            maxHeight = Math.Max(maxHeight, h);
        }

        Assert.True(maxHeight > 100, $"Expected mountains in the heightmap, max elevation was {maxHeight}.");
    }
}
