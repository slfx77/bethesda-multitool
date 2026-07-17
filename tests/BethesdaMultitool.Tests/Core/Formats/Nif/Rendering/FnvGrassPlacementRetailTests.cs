using System.Numerics;
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
    private const uint SteepFixtureCellFormId = 0x000DAE62;
    private const uint SteepFixtureGrassFormId = 0x0016ACCC;

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
        SteepPlacementSample? steepest = null;
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
            var cellSteepest = AssertPlacementHeightsUseFlooredCheckerboardPlanes(
                cell,
                terrain.Heights,
                greenGrass);
            if (cellSteepest is { } sample &&
                (steepest is null || sample.Normal.Z < steepest.Value.Normal.Z))
            {
                steepest = sample;
            }
        }

        output.WriteLine(
            $"WastelandNV NVGreenGrass retail census: {placementCount:N0} placements across " +
            $"{cellsWithGreenGrass.Count:N0} cells.");
        Assert.Equal(ExpectedPlacementCount, placementCount);
        Assert.Equal(ExpectedCellCount, cellsWithGreenGrass.Count);
        Assert.True(steepest.HasValue);
        AssertSteepRetailTransform(steepest.Value, grasses);
    }

    private static SteepPlacementSample? AssertPlacementHeightsUseFlooredCheckerboardPlanes(
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
        SteepPlacementSample? steepest = null;

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
                out var terrainNormal);

            Assert.True(
                sampled,
                $"GRAS 0x{placement.FormId:X8} in CELL 0x{cell.FormId:X8} fell outside its LAND grid " +
                $"at ({position.X}, {position.Y}, {position.Z}).");
            Assert.Equal(MathF.Floor(terrainHeight), position.Z);
            Assert.Equal(position, placement.BoundsCenter);
            if (steepest is null || terrainNormal.Z < steepest.Value.Normal.Z)
            {
                steepest = new SteepPlacementSample(cell.FormId, gridX, gridY, placement, terrainNormal);
            }
        }

        return steepest;
    }

    private static void AssertSteepRetailTransform(
        SteepPlacementSample sample,
        IReadOnlyDictionary<uint, BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc.GrassRecord> grasses)
    {
        Assert.Equal(SteepFixtureCellFormId, sample.CellFormId);
        Assert.Equal(-19, sample.GridX);
        Assert.Equal(12, sample.GridY);
        Assert.Equal(SteepFixtureGrassFormId, sample.Placement.FormId);
        Assert.Equal("landscape\\grass\\NVGreenGrass03.NIF", sample.Placement.ModelPath, ignoreCase: true);
        Assert.Equal(new Vector3(-74_576f, 51_445f, 5_245f), sample.Placement.WorldMatrix.Translation);
        Assert.Equal(-0.0402259f, sample.Normal.X, 6);
        Assert.Equal(-0.76429206f, sample.Normal.Y, 6);
        Assert.Equal(0.6436144f, sample.Normal.Z, 6);
        Assert.Equal(49.93813f, MathF.Acos(sample.Normal.Z) * (180f / MathF.PI), 4);

        var grass = Assert.IsType<BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc.GrassRecord>(
            grasses[SteepFixtureGrassFormId]);
        var data = Assert.IsType<BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc.GrassData>(grass.Data);
        Assert.Equal(0x06, data.Flags);
        Assert.Equal(0.42f, data.HeightRange);

        // This emitted candidate's first post-position random draw packs +23. GRAS bit 1 maps
        // ScaleMask to XYZ, while bit 2 selects the GRASS2002/3 fit basis, so all three local axes
        // receive the same 1 + 0.01 * 23 scale before the recovered B/T/N transform.
        const float heightScale = 1.23f;
        var decodedNormal = Vector3.Min(sample.Normal, new Vector3(0.94f));
        var absolute = Vector3.Abs(decodedNormal);
        var tangent = absolute.Y >= absolute.X && absolute.Z >= absolute.X
            ? new Vector3(0f, -decodedNormal.Z, decodedNormal.Y)
            : new Vector3(-decodedNormal.Z, 0f, decodedNormal.X);
        tangent = Vector3.Normalize(tangent);
        var bitangent = Vector3.Cross(tangent, decodedNormal);

        AssertAxis(sample.Placement.WorldMatrix, 1, bitangent * heightScale);
        AssertAxis(sample.Placement.WorldMatrix, 2, tangent * heightScale);
        AssertAxis(sample.Placement.WorldMatrix, 3, decodedNormal * heightScale);
    }

    private static void AssertAxis(Matrix4x4 matrix, int row, Vector3 expected)
    {
        var actual = row switch
        {
            1 => new Vector3(matrix.M11, matrix.M12, matrix.M13),
            2 => new Vector3(matrix.M21, matrix.M22, matrix.M23),
            3 => new Vector3(matrix.M31, matrix.M32, matrix.M33),
            _ => throw new ArgumentOutOfRangeException(nameof(row)),
        };
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }

    private readonly record struct SteepPlacementSample(
        uint CellFormId,
        int GridX,
        int GridY,
        RenderableReference Placement,
        Vector3 Normal);
}
