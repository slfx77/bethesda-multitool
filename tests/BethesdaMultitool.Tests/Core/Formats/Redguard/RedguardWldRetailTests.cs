using BethesdaMultitool.Core.Formats.Redguard;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Redguard;

/// <summary>
///     Opt-in checks (<c>RUN_BUCKET_B=1</c>) of the four retail <c>maps\*.WLD</c> terrains against
///     the semantics read off RG.EXE (2026-09-08). Each oracle here is external to the reader: the
///     sibling <c>.RGM</c>'s AI nav nodes for the height layer, <c>surface.ini</c>'s scape classes
///     for the texture layer, and the byte-level facts the four files share.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class RedguardWldRetailTests
{
    private static string RequireMapsDir()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var root = RealAssetPaths.Classics.Redguard();
        Assert.SkipWhen(root is null, RealAssetPaths.SkipMessage("Redguard"));
        var maps = Path.Combine(root, "maps");
        Assert.SkipWhen(!Directory.Exists(maps), RealAssetPaths.SkipMessage("Redguard maps"));
        return maps;
    }

    private static RedguardWldFile LoadWld(string maps, string stem)
    {
        var path = Path.Combine(maps, stem + ".WLD");
        return RedguardWldFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    private static RedguardRgmFile LoadRgm(string maps, string stem)
    {
        var path = Path.Combine(maps, stem + ".RGM");
        return RedguardRgmFile.Parse(File.ReadAllBytes(path), Path.GetFileName(path));
    }

    [Fact]
    public void AllFourFilesTileAsFourStoredTilesOfTextureSet302()
    {
        var maps = RequireMapsDir();
        var stems = Directory.GetFiles(maps, "*.WLD")
            .Select(f => Path.GetFileNameWithoutExtension(f).ToUpperInvariant())
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        Assert.Equal(["EXTPALAC", "HIDEOUT", "ISLAND", "NECRISLE"], stems);

        foreach (var stem in stems)
        {
            var wld = LoadWld(maps, stem);
            Assert.Equal([16u, 2u, 2u, 0u, 160u, 1u, 22u, 263416u], wld.Header);
            Assert.Equal([1184, 66742, 132300, 197858], wld.Tiles.Select(t => t.Offset));
            Assert.All(wld.Tiles, t => Assert.True(t.IsStored));
            Assert.All(wld.Tiles, t => Assert.Equal(302, t.TextureSet));
            Assert.All(wld.Tiles, t => Assert.Equal(0, t.LevelIndex));
            Assert.Equal([2152u, 568u, 1308u, 10u], wld.Tiles.Select(t => t.Word0));
            Assert.Equal([25, 30, 80, 127], wld.LevelTable.Take(4));
            Assert.All(wld.LevelTable.Skip(4), b => Assert.Equal(0, b));
            Assert.Equal([0x43C028u, 0xFFFFFFFFu, 0x43735u], wld.Trailer);

            // Layers 1 and 3 — the scatter flats and the runtime-unread fourth quarter — are
            // empty in every shipped world; only height and surface carry data.
            Assert.All(wld.ScatterLayer.Indices, b => Assert.Equal(0, b));
            Assert.All(wld.UnusedLayer.Indices, b => Assert.Equal(0, b));
            Assert.Contains(wld.HeightLayer.Indices, b => (b & RedguardWldFile.HeightIndexMask) != 0);
            Assert.Contains(wld.SurfaceLayer.Indices, b => b != 0);
        }
    }

    [Fact]
    public void ExtpalacIsIslandWithTwoSurfaceCellsRepainted()
    {
        var maps = RequireMapsDir();
        var island = LoadWld(maps, "ISLAND");
        var extpalac = LoadWld(maps, "EXTPALAC");

        Assert.Equal(island.HeightLayer.Indices, extpalac.HeightLayer.Indices);

        var differing = new List<(int X, int Z)>();
        for (var z = 0; z < RedguardWldFile.MapSize; z++)
        {
            for (var x = 0; x < RedguardWldFile.MapSize; x++)
            {
                if (island.SurfaceLayer.Indices[z * 256 + x] != extpalac.SurfaceLayer.Indices[z * 256 + x])
                {
                    differing.Add((x, z));
                }
            }
        }

        // Two diagonally adjacent cells on the palace side: texture 4 -> 3 unrotated, and 4 -> 39
        // (rock) turned twice. WORLD.INI never names EXTPALAC.WLD — its world 30 plays on ISLAND.WLD.
        Assert.Equal([(144, 135), (143, 136)], differing);
        Assert.Equal((4, 0), (island.SurfaceTextureAt(144, 135), island.SurfaceRotationAt(144, 135)));
        Assert.Equal((3, 0), (extpalac.SurfaceTextureAt(144, 135), extpalac.SurfaceRotationAt(144, 135)));
        Assert.Equal((39, 2), (extpalac.SurfaceTextureAt(143, 136), extpalac.SurfaceRotationAt(143, 136)));
    }

    [Theory]
    [InlineData("ISLAND", "ISLAND", 1424, 1300)]
    [InlineData("NECRISLE", "NECRISLE", 561, 500)]
    [InlineData("EXTPALAC", "ISLAND", 95, 65)]
    public void NavGraphNodesStandOnTheHeightLayer(string mapStem, string terrainStem, int expectedNodes,
        int minWithinQuarterCell)
    {
        // The AI nav nodes (WDNM) sit on the walkable ground, so their y must reproduce from the
        // height layer through the game's table, negated, at the game's own world-to-cell mapping.
        // Measured 2026-09-08: ISLAND median |residual| 2.0 units, r = 0.996, 1,337/1,424 within
        // 64 units; a shuffled height layer manages 89. Any other layer, mapping or sign fails this.
        var maps = RequireMapsDir();
        var wld = LoadWld(maps, terrainStem);
        var rgm = LoadRgm(maps, mapStem);

        var residuals = new List<double>();
        foreach (var node in rgm.NavigationMaps.SelectMany(m => m.Nodes))
        {
            var predicted = wld.SampleWorldHeight(node.X, node.Z);
            if (double.IsNaN(predicted))
            {
                continue;
            }

            residuals.Add(Math.Abs(node.Y - predicted));
        }

        Assert.Equal(expectedNodes, residuals.Count);
        residuals.Sort();
        Assert.True(residuals[residuals.Count / 2] <= 8, $"median residual {residuals[residuals.Count / 2]}");
        var within = residuals.Count(r => r <= RedguardWldFile.WorldUnitsPerCell / 4.0);
        Assert.True(within >= minWithinQuarterCell, $"{within} of {residuals.Count} nodes within a quarter cell");
    }

    [Fact]
    public void ShuffledHeightLayerDoesNotFitTheNavNodes()
    {
        // The control that makes the oracle above mean something: the same nodes against a
        // permuted height layer land nowhere near the ground.
        var maps = RequireMapsDir();
        var wld = LoadWld(maps, "ISLAND");
        var rgm = LoadRgm(maps, "ISLAND");
        var random = new Random(1);
        var permuted = wld.HeightLayer.Indices.OrderBy(_ => random.Next()).ToArray();

        var within = 0;
        var total = 0;
        foreach (var node in rgm.NavigationMaps.SelectMany(m => m.Nodes))
        {
            if (!RedguardWldFile.TryMapWorldToCell(node.X, node.Z, out var cx, out var cz))
            {
                continue;
            }

            total++;
            var predicted = -RedguardWldFile.HeightTable[permuted[cz * 256 + cx] & RedguardWldFile.HeightIndexMask];
            if (Math.Abs(node.Y - predicted) <= RedguardWldFile.WorldUnitsPerCell / 4.0)
            {
                within++;
            }
        }

        Assert.Equal(1424, total);
        Assert.True(within < 200, $"{within} nodes within a quarter cell of a shuffled layer");
    }

    [Fact]
    public void SurfaceTexturesFollowSurfaceIniClassesAgainstHeight()
    {
        // surface.ini classes TEXTURE.302's records: 0/5/30/31 DEEPWATER, 6/7/32/52 WATER,
        // 1/10/11/19/21-26 SAND, 4/13/39/3/8/9/40-42/46/47/53/54/59-62 ROCK. If layer 2's low six
        // bits are those records and layer 0 is height, deep water must sit at the bottom of the
        // height range and rock well above sand. Measured on ISLAND: medians 0 / 0 / 720 / 920.
        var maps = RequireMapsDir();
        var wld = LoadWld(maps, "ISLAND");
        int[] deep = [0, 5, 30, 31];
        int[] sand = [1, 10, 11, 19, 21, 22, 23, 24, 25, 26];
        int[] rock = [4, 13, 39, 3, 8, 9, 40, 41, 42, 46, 47, 53, 54, 59, 60, 61, 62];

        var used = new HashSet<int>();
        var deepHeights = new List<int>();
        var sandHeights = new List<int>();
        var rockHeights = new List<int>();
        for (var z = 0; z < RedguardWldFile.MapSize; z++)
        {
            for (var x = 0; x < RedguardWldFile.MapSize; x++)
            {
                var texture = wld.SurfaceTextureAt(x, z);
                used.Add(texture);
                var height = -wld.WorldHeightAt(x, z);
                if (deep.Contains(texture))
                {
                    deepHeights.Add(height);
                }
                else if (sand.Contains(texture))
                {
                    sandHeights.Add(height);
                }
                else if (rock.Contains(texture))
                {
                    rockHeights.Add(height);
                }
            }
        }

        Assert.Equal(63, used.Count);
        Assert.True(used.Max() <= 63);
        Assert.True(deepHeights.Count > 30000 && sandHeights.Count > 4000 && rockHeights.Count > 5000);
        Assert.Equal(0, Median(deepHeights));
        Assert.InRange(Median(sandHeights), 400, 1000);
        Assert.True(Median(rockHeights) > Median(sandHeights), "rock should sit above sand");
    }

    [Fact]
    public void StoredQuadSplitBitIsASupersetOfTheLoadersRule()
    {
        // The loader recomputes bit 7 after every tile load. The stored bit is stale editor
        // state: every quad the rule flags is stored flagged, but the file flags thousands more
        // (mostly flat sea quads). Measured 2026-09-08 on ISLAND's 255 x 255 interior quads:
        // 7,170 by rule, 9,822 stored (7,195 / 10,201 over the full grid with the loader's overrun).
        var maps = RequireMapsDir();
        var wld = LoadWld(maps, "ISLAND");

        var byRule = 0;
        var stored = 0;
        var ruleButNotStored = 0;
        for (var z = 0; z < RedguardWldFile.MapSize - 1; z++)
        {
            for (var x = 0; x < RedguardWldFile.MapSize - 1; x++)
            {
                var rule = wld.ComputedQuadSplitAt(x, z);
                var flag = wld.StoredQuadSplitAt(x, z);
                byRule += rule ? 1 : 0;
                stored += flag ? 1 : 0;
                ruleButNotStored += rule && !flag ? 1 : 0;
            }
        }

        Assert.Equal(0, ruleButNotStored);
        Assert.InRange(byRule, 7000, 7300);
        Assert.InRange(stored, 9700, 9950);
    }

    private static int Median(List<int> values)
    {
        values.Sort();
        return values[values.Count / 2];
    }
}