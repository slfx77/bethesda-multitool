using BethesdaMultitool;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Retail-data regression for the complete WastelandNV LAND/GRAS scatter. A synthetic fixture
///     can pin the placement formula, but only the shipped master can protect the exact authored
///     LTEX-to-GRAS coverage census that feeds the world renderer.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvGrassPlacementRetailTests(
    SampleFileFixture samples,
    ITestOutputHelper output)
{
    private const uint WastelandNvFormId = 0x000DA726;
    private const int ExpectedPlacementCount = 10_255;
    private const int ExpectedCellCount = 78;

    [Fact]
    public void WastelandNv_GreenGrassScatter_MatchesRetailCensusAndTerrainPlanes()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var wasteland = Assert.Single(collection.Worldspaces, world => world.FormId == WastelandNvFormId);
        var landTextures = collection.LandTextures
            .GroupBy(texture => texture.FormId)
            .ToDictionary(group => group.Key, group => group.Last());
        var grasses = collection.Grasses
            .GroupBy(grass => grass.FormId)
            .ToDictionary(group => group.Key, group => group.Last());

        var placementCount = 0;
        var cellsWithGreenGrass = new HashSet<uint>();
        foreach (var cell in wasteland.Cells)
        {
            var terrain = DecodedTerrainCell.Decode(cell);
            if (!terrain.HasTerrain)
            {
                continue;
            }

            var placements = GrassPlacementBuilder.Build(
                cell,
                terrain.Heights,
                landTextures,
                grasses,
                BethesdaGame.FalloutNewVegas,
                WorldRenderCache.ResolveEffectiveWaterHeight(cell, wasteland.DefaultWaterHeight));

            var greenGrass = placements
                .Where(placement =>
                    placement.IsGrass &&
                    placement.ModelPath.Contains("NVGreenGrass", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (greenGrass.Length == 0)
            {
                continue;
            }

            cellsWithGreenGrass.Add(cell.FormId);
            placementCount += greenGrass.Length;
            AssertPlacementHeightsUseFlooredCheckerboardPlanes(cell, terrain.Heights, greenGrass);
        }

        output.WriteLine(
            $"WastelandNV NVGreenGrass retail census: {placementCount:N0} placements across " +
            $"{cellsWithGreenGrass.Count:N0} cells.");
        Assert.Equal(ExpectedPlacementCount, placementCount);
        Assert.Equal(ExpectedCellCount, cellsWithGreenGrass.Count);
    }

    private static void AssertPlacementHeightsUseFlooredCheckerboardPlanes(
        BethesdaMultitool.Core.Formats.Esm.Models.Records.World.CellRecord cell,
        float[] heights,
        IReadOnlyList<RenderableReference> placements)
    {
        var gridX = Assert.IsType<int>(cell.GridX);
        var gridY = Assert.IsType<int>(cell.GridY);
        var cellSize = cell.CellWorldSize > 0f ? cell.CellWorldSize : WorldGridConstants.CellSize;
        var spacing = cellSize / (DecodedTerrainCell.GridSize - 1);
        var originX = gridX * cellSize;
        var originY = gridY * cellSize;

        foreach (var placement in placements)
        {
            var position = placement.WorldMatrix.Translation;
            var sampled = TerrainSurfaceTopology.TrySampleTriangle(
                heights,
                DecodedTerrainCell.GridSize,
                position.X - originX,
                position.Y - originY,
                spacing,
                TerrainTriangleTopology.AlternatingCheckerboard,
                out var terrainHeight,
                out _);

            Assert.True(
                sampled,
                $"GRAS 0x{placement.FormId:X8} in CELL 0x{cell.FormId:X8} fell outside its LAND grid " +
                $"at ({position.X}, {position.Y}, {position.Z}).");
            Assert.Equal(MathF.Floor(terrainHeight), position.Z);
            Assert.Equal(position, placement.BoundsCenter);
        }
    }
}
