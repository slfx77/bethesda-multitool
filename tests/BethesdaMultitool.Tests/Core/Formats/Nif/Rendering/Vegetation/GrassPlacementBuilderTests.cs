using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

public sealed class GrassPlacementBuilderTests
{
    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas, 80f)]
    [InlineData(BethesdaGame.Skyrim, 20f)]
    [InlineData(BethesdaGame.Fallout4, 20f)]
    public void Profile_UsesRecoveredShippingDefaults(BethesdaGame game, float expectedMinimumSize)
    {
        var profile = GrassScatterProfile.ForGame(game);

        Assert.True(profile.Supported);
        Assert.Equal(expectedMinimumSize, profile.MinGrassSize);
        Assert.Equal(2, profile.EvalRadius);
        Assert.Equal(3, profile.MaxGrassEntriesPerTexture);
        Assert.Equal(0f, profile.TexturePercentageThreshold);
        Assert.Equal(
            game == BethesdaGame.FalloutNewVegas
                ? GrassPositionQuantization.FloorWorldUnits
                : GrassPositionQuantization.HalfRelativeToTwelveCellBlock,
            profile.PositionQuantization);
        Assert.Equal(
            game == BethesdaGame.FalloutNewVegas
                ? TerrainTriangleTopology.AlternatingCheckerboard
                : null,
            profile.TerrainTopology);
        Assert.Equal(game == BethesdaGame.FalloutNewVegas, profile.FloorSampledHeight);
    }

    [Fact]
    public void Profile_OblivionUsesShippedIniDefaultsWithConservativePlacement()
    {
        // Oblivion_default.ini [Grass] (see GrassScatterProfile.ForGame comment): iMinGrassSize=80,
        // iGrassDensityEvalSize=2, iMaxGrassTypesPerTexure=2 (inclusive → three entries),
        // fGrassStartFadeDistance=2000 + fGrassEndDistance=3000 (END distance, not a range).
        var profile = GrassScatterProfile.ForGame(BethesdaGame.Oblivion);

        Assert.True(profile.Supported);
        Assert.Equal(80f, profile.MinGrassSize);
        Assert.Equal(2, profile.EvalRadius);
        Assert.Equal(3, profile.MaxGrassEntriesPerTexture);
        Assert.Equal(0f, profile.TexturePercentageThreshold);
        // Quantization/topology/height-floor stay conservative until a TES4 CreateGrass decompile
        // proves the FNV ancestry values.
        Assert.Equal(GrassPositionQuantization.None, profile.PositionQuantization);
        Assert.Null(profile.TerrainTopology);
        Assert.False(profile.FloorSampledHeight);
        Assert.Equal(2000f, profile.DistanceEnvelope.FadeStart);
        Assert.Equal(1000f, profile.DistanceEnvelope.FadeRange);
        Assert.Equal(3000f, profile.DistanceEnvelope.HardEnd);
    }

    [Fact]
    public void Build_OblivionUsesGenericPlacementTransformWithoutFnvArtifacts()
    {
        var heights = Enumerable.Repeat(123.456f, 33 * 33).ToArray();
        var fixture = CreateFixture(heights, 100, 512f);

        var placements = Build(fixture, BethesdaGame.Oblivion);

        Assert.NotEmpty(placements);
        Assert.All(placements, p =>
        {
            // No FNV wind contract and no FNV floor/checkerboard artifacts: the sampled flat
            // height survives un-floored.
            Assert.Equal(0f, p.GrassWaveMultiplier);
            Assert.Equal(123.456f, p.WorldMatrix.Translation.Z, 3);
        });
    }

    [Fact]
    public void Build_InclusiveEngineLimitConsumesThreeGrassLinks()
    {
        var fixture = CreateFixture(CreateFlatHeights(), 100, 512f);
        var grass2 = fixture.Grass with { FormId = fixture.Grass.FormId + 1 };
        var grass3 = fixture.Grass with { FormId = fixture.Grass.FormId + 2 };
        var grass4 = fixture.Grass with { FormId = fixture.Grass.FormId + 3 };
        var landTexture = fixture.LandTexture with
        {
            GrassFormIds = [fixture.Grass.FormId, grass2.FormId, grass3.FormId, grass4.FormId]
        };

        var placements = GrassPlacementBuilder.Build(
            fixture.Cell,
            fixture.Heights,
            new Dictionary<uint, LandscapeTextureRecord> { [landTexture.FormId] = landTexture },
            new Dictionary<uint, GrassRecord>
            {
                [fixture.Grass.FormId] = fixture.Grass,
                [grass2.FormId] = grass2,
                [grass3.FormId] = grass3,
                [grass4.FormId] = grass4
            },
            BethesdaGame.FalloutNewVegas,
            null);

        Assert.Equal(64 * 3, placements.Count);
        Assert.Contains(placements, p => p.FormId == fixture.Grass.FormId);
        Assert.Contains(placements, p => p.FormId == grass2.FormId);
        Assert.Contains(placements, p => p.FormId == grass3.FormId);
        Assert.DoesNotContain(placements, p => p.FormId == grass4.FormId);
    }

    [Fact]
    public void Build_FullDensityBaseTexture_ProducesOneCandidatePerEvaluationChunk()
    {
        var fixture = CreateFixture(CreateFlatHeights(), 100, 512f);

        var placements = Build(fixture, BethesdaGame.FalloutNewVegas);

        // Four 2048-unit quadrants, each evaluated at q=2,6,10,14 on both axes: 4*4*4 = 64.
        Assert.Equal(64, placements.Count);
        Assert.All(placements, p =>
        {
            Assert.True(p.IsGrass);
            Assert.Equal(10f, p.GrassWaveMultiplier);
            Assert.Equal(fixture.Grass.FormId, p.FormId);
            Assert.Equal(fixture.Grass.ModelPath, p.ModelPath);
            Assert.InRange(p.WorldMatrix.Translation.X, 0f, 4096f);
            Assert.InRange(p.WorldMatrix.Translation.Y, 0f, 4096f);
            Assert.Equal(MathF.Floor(p.WorldMatrix.Translation.X), p.WorldMatrix.Translation.X);
            Assert.Equal(MathF.Floor(p.WorldMatrix.Translation.Y), p.WorldMatrix.Translation.Y);
            Assert.Equal(0f, p.WorldMatrix.Translation.Z, 5);
        });
    }

    [Fact]
    public void Build_SkyrimRoundTripsPositionsThroughBlockRelativeHalfPrecision()
    {
        var heights = Enumerable.Repeat(123.456f, 33 * 33).ToArray();
        var fixture = CreateFixture(heights, 100, 512f);

        var placements = Build(fixture, BethesdaGame.Skyrim);

        Assert.NotEmpty(placements);
        Assert.All(placements, p =>
        {
            Assert.Equal(0f, p.GrassWaveMultiplier);
            Assert.Equal((float)(Half)p.WorldMatrix.Translation.X, p.WorldMatrix.Translation.X);
            Assert.Equal((float)(Half)p.WorldMatrix.Translation.Y, p.WorldMatrix.Translation.Y);
            Assert.Equal((float)(Half)123.456f, p.WorldMatrix.Translation.Z);
        });
    }

    [Fact]
    public void Build_FnvUsesCheckerboardTrianglePlaneAndFloorsReturnedHeight()
    {
        var heights = new float[33 * 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y * 33 + x] = ((x + y) & 1) == 0 ? 0f : 80.75f;
            }
        }

        var fixture = CreateFixture(
            heights,
            100,
            512f,
            maxSlope: 90,
            flags: 0x06);

        var placements = Build(fixture, BethesdaGame.FalloutNewVegas);

        Assert.Equal(64, placements.Count);
        var fractionalSamples = 0;
        foreach (var placement in placements)
        {
            var position = placement.WorldMatrix.Translation;
            Assert.True(TerrainSurfaceTopology.TrySampleTriangle(
                heights,
                33,
                position.X,
                position.Y,
                128f,
                TerrainTriangleTopology.AlternatingCheckerboard,
                out var sampledHeight,
                out var sampledNormal));

            if (MathF.Abs(sampledHeight - MathF.Floor(sampledHeight)) > 1e-4f)
                fractionalSamples++;
            Assert.Equal(MathF.Floor(sampledHeight), position.Z);
            Assert.Equal(position, placement.BoundsCenter);

            var placedUp = Vector3.Normalize(new Vector3(
                placement.WorldMatrix.M31,
                placement.WorldMatrix.M32,
                placement.WorldMatrix.M33));
            Assert.InRange(Vector3.Distance(sampledNormal, placedUp), 0f, 1e-5f);
        }

        Assert.True(fractionalSamples > 0);
    }

    [Theory]
    [InlineData(0f, 0.42f, 0.58f)]
    [InlineData(0.4f, 0.42f, 0.91f)]
    [InlineData(0.6f, 0.42f, 1.08f)]
    [InlineData(1f, 0.42f, 1.42f)]
    public void ComputeFnvHeightScale_ReplaysPackedIntegerHeightComponent(
        float random01,
        float heightRange,
        float expected)
    {
        var actual = GrassPlacementBuilder.ComputeFnvHeightScale(random01, heightRange);

        Assert.Equal(expected, actual, 5);
    }

    [Fact]
    public void ComposeFnvWorldMatrix_ReplaysPackedNormalBasisAndTieSelection()
    {
        var sloped = GrassPlacementBuilder.ComposeFnvWorldMatrix(
            new Vector3(10f, 20f, 30f),
            new Vector3(0.8f, 0f, 0.6f),
            true,
            1.25f,
            false,
            new Vector3(0.1f, 0.2f, 0.97f),
            0.5f);

        // |X| is not the smallest component, so GRASS2002 chooses N cross Y for T.
        AssertAxis(sloped, 1, new Vector3(0f, 1f, 0f));
        AssertAxis(sloped, 2, new Vector3(-0.6f, 0f, 0.8f));
        AssertAxis(sloped, 3, new Vector3(1f, 0f, 0.75f));
        Assert.Equal(new Vector3(10f, 20f, 30f), sloped.Translation);

        var flat = GrassPlacementBuilder.ComposeFnvWorldMatrix(
            Vector3.Zero,
            Vector3.UnitZ,
            true,
            1.2f,
            true,
            Vector3.UnitZ,
            0f);

        // abs(Y) >= abs(X) is true on the 0 == 0 tie. The packed normal's upper clamp decodes
        // retail +Z as 0.94, and the resulting flat basis is the shader's deterministic 180° turn.
        AssertAxis(flat, 1, new Vector3(-0.94f * 1.2f, 0f, 0f));
        AssertAxis(flat, 2, new Vector3(0f, -1.2f, 0f));
        AssertAxis(flat, 3, new Vector3(0f, 0f, 0.94f * 1.2f));
    }

    [Fact]
    public void ComposeFnvWorldMatrix_CarriesTheLightingPayloadInTheMatrixWLanes()
    {
        var world = GrassPlacementBuilder.ComposeFnvWorldMatrix(
            new Vector3(5f, 6f, 7f),
            Vector3.UnitZ,
            false,
            1f,
            true,
            new Vector3(0.25f, -0.5f, 0.99f),
            0.625f);

        // xyz = the lighting normal under the engine's packing clamp (min(N, 0.94), component-wise
        // and deliberately not renormalized); w = the baked light. The basis and translation are
        // untouched by the payload.
        Assert.Equal(0.25f, world.M14, 5);
        Assert.Equal(-0.5f, world.M24, 5);
        Assert.Equal(0.94f, world.M34, 5);
        Assert.Equal(0.625f, world.M44, 5);
        Assert.Equal(new Vector3(5f, 6f, 7f), world.Translation);
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(2f)]
    [InlineData(-1f)]
    public void ComposeFnvWorldMatrix_ClampsTheBakedLightSoTheMatrixStaysFinite(float bakedLight)
    {
        var world = GrassPlacementBuilder.ComposeFnvWorldMatrix(
            Vector3.Zero,
            Vector3.UnitZ,
            false,
            1f,
            true,
            Vector3.UnitZ,
            bakedLight);

        // GeometryDrawValidator12 rejects a non-finite instance matrix outright, and the shader's
        // frac() would alias an out-of-range value into the wrong brightness.
        Assert.True(float.IsFinite(world.M44));
        Assert.InRange(world.M44, 0f, FnvGrassLighting.BakedLightMaximum);
    }

    [Fact]
    public void Build_FnvConsumesHeightDrawWithoutYawAndMapsUniformScaleFlag()
    {
        var fixture = CreateFixture(
            CreateFlatHeights(),
            100,
            512f,
            flags: 0,
            heightRange: 0.42f);
        var uniformFixture = fixture with
        {
            Grass = fixture.Grass with
            {
                Data = fixture.Grass.Data! with { Flags = 0x02 }
            }
        };

        var verticalOnly = Build(fixture, BethesdaGame.FalloutNewVegas);
        var uniform = Build(uniformFixture, BethesdaGame.FalloutNewVegas);

        Assert.Equal(64, verticalOnly.Count);
        Assert.Equal(verticalOnly.Count, uniform.Count);
        Assert.Equal(verticalOnly[0].WorldMatrix.Translation, uniform[0].WorldMatrix.Translation);

        // For the first deterministic candidate the fourth draw (after density/X/Y) packs -39,
        // yielding 1 + 0.01 * -39 = 0.61. Consuming an invented yaw first would instead use the
        // fifth draw and produce 1.12, so this assertion pins the FNV draw count as well as flooring.
        AssertAxis(verticalOnly[0].WorldMatrix, 1, Vector3.UnitX);
        AssertAxis(verticalOnly[0].WorldMatrix, 2, Vector3.UnitY);
        AssertAxis(verticalOnly[0].WorldMatrix, 3, new Vector3(0f, 0f, 0.61f));
        AssertAxis(uniform[0].WorldMatrix, 1, new Vector3(0.61f, 0f, 0f));
        AssertAxis(uniform[0].WorldMatrix, 2, new Vector3(0f, 0.61f, 0f));
        AssertAxis(uniform[0].WorldMatrix, 3, new Vector3(0f, 0f, 0.61f));

        Assert.All(verticalOnly, placement =>
        {
            Assert.Equal(0f, placement.WorldMatrix.M12);
            Assert.Equal(0f, placement.WorldMatrix.M21);
        });
    }

    [Fact]
    public void QuantizePosition_NegativeCellsUseRecoveredTruncatingTwelveCellBlockOrigin()
    {
        var x = -4001.25f;
        var y = -52000.75f;

        GrassPositionQuantizer.Quantize(
            ref x,
            ref y,
            -1,
            -13,
            4096f,
            GrassPositionQuantization.HalfRelativeToTwelveCellBlock);

        // Signed engine division truncates: -1 / 12 -> 0, while -13 / 12 -> -1.
        Assert.Equal((float)(Half)(-4001.25f), x);
        Assert.Equal(-49152f + (float)(Half)(-52000.75f + 49152f), y);
    }

    [Fact]
    public void Build_RecoveredYawSamplingUsesPositiveSquareRootHalfCircle()
    {
        var fixture = CreateFixture(CreateFlatHeights(), 100, 512f);

        var placements = Build(fixture, BethesdaGame.Skyrim);

        Assert.NotEmpty(placements);
        // CreateGrass samples cos in [-1,1] and derives sin as the positive sqrt(1-cos^2).
        Assert.All(placements, placement => Assert.True(placement.WorldMatrix.M11 >= 0f));
        Assert.Contains(placements, placement => placement.WorldMatrix.M12 < 0f);
        Assert.Contains(placements, placement => placement.WorldMatrix.M12 > 0f);
    }

    [Fact]
    public void Build_IsDeterministicForTheSameCellAndRecords()
    {
        var fixture = CreateFixture(CreateFlatHeights(), 47, 256f);

        var first = Build(fixture, BethesdaGame.Skyrim);
        var second = Build(fixture, BethesdaGame.Skyrim);

        Assert.NotEmpty(first);
        Assert.Equal(first.Count, second.Count);
        for (var i = 0; i < first.Count; i++)
        {
            Assert.Equal(first[i].WorldMatrix, second[i].WorldMatrix);
            Assert.Equal(first[i].MeshId, second[i].MeshId);
        }
    }

    [Fact]
    public void Build_ZeroDensitySuppressesEveryCandidate()
    {
        var fixture = CreateFixture(CreateFlatHeights(), 0, 512f);

        Assert.Empty(Build(fixture, BethesdaGame.Skyrim));
    }

    [Fact]
    public void Build_AppliesRecoveredSlopeGate()
    {
        var heights = CreatePlanarHeights(1f);
        var tooSteep = CreateFixture(heights, 100, 512f, maxSlope: 30);
        var accepted = CreateFixture(heights, 100, 512f, maxSlope: 60);

        Assert.Empty(Build(tooSteep, BethesdaGame.Skyrim));
        Assert.Equal(64, Build(accepted, BethesdaGame.Skyrim).Count);
    }

    [Fact]
    public void Build_FitToSlopeAlignsTheLocalUpAxisWithTerrainNormal()
    {
        var fixture = CreateFixture(
            CreatePlanarHeights(1f),
            100,
            512f,
            maxSlope: 90,
            flags: 0x04);

        var placement = Assert.Single(Build(fixture, BethesdaGame.Skyrim).Take(1));
        var up = Vector3.Normalize(new Vector3(
            placement.WorldMatrix.M31,
            placement.WorldMatrix.M32,
            placement.WorldMatrix.M33));

        Assert.Equal(-MathF.Sqrt(0.5f), up.X, 5);
        Assert.Equal(0f, up.Y, 5);
        Assert.Equal(MathF.Sqrt(0.5f), up.Z, 5);
    }

    [Fact]
    public void Build_AppliesWaterStateBeforeEmittingInstances()
    {
        var belowOnly = CreateFixture(
            CreateFlatHeights(),
            100,
            512f,
            waterAmount: 20,
            waterState: 2);
        var aboveOnly = CreateFixture(
            CreateFlatHeights(),
            100,
            512f,
            waterAmount: 20,
            waterState: 1);

        Assert.Equal(64, Build(belowOnly, BethesdaGame.Fallout4, 100f).Count);
        Assert.Empty(Build(aboveOnly, BethesdaGame.Fallout4, 100f));
    }

    [Fact]
    public void Build_UnsupportedGameDoesNotScatterGrass()
    {
        var fixture = CreateFixture(CreateFlatHeights(), 100, 512f);

        Assert.Empty(Build(fixture, BethesdaGame.Morrowind));
    }

    private static IReadOnlyList<RenderableReference> Build(
        Fixture fixture,
        BethesdaGame game,
        float? waterHeight = null)
    {
        return GrassPlacementBuilder.Build(
            fixture.Cell,
            fixture.Heights,
            new Dictionary<uint, LandscapeTextureRecord> { [fixture.LandTexture.FormId] = fixture.LandTexture },
            new Dictionary<uint, GrassRecord> { [fixture.Grass.FormId] = fixture.Grass },
            game,
            waterHeight);
    }

    private static Fixture CreateFixture(
        float[] heights,
        byte density,
        float positionRange,
        byte minSlope = 0,
        byte maxSlope = 90,
        byte flags = 0,
        ushort waterAmount = 0,
        uint waterState = 0,
        float heightRange = 0f)
    {
        const uint ltexFormId = 0x01000001;
        const uint grassFormId = 0x01000002;
        var layers = Enumerable.Range(0, 4)
            .Select(q => new LandTextureLayer
            {
                Kind = LandTextureLayerKind.Base,
                TextureFormId = ltexFormId,
                Quadrant = (byte)q
            })
            .ToList();
        var cell = new CellRecord
        {
            FormId = 0x01000003,
            GridX = 0,
            GridY = 0,
            CellWorldSize = 4096f,
            LandVisualData = new LandVisualData { TextureLayers = layers }
        };
        var landTexture = new LandscapeTextureRecord
        {
            FormId = ltexFormId,
            GrassFormIds = [grassFormId]
        };
        var grass = new GrassRecord
        {
            FormId = grassFormId,
            ModelPath = "meshes/landscape/grass/testgrass.nif",
            ModelBound = 32f,
            Data = new GrassData
            {
                Density = density,
                MinSlope = minSlope,
                MaxSlope = maxSlope,
                UnitsFromWaterAmount = waterAmount,
                UnitsFromWaterType = waterState,
                PositionRange = positionRange,
                HeightRange = heightRange,
                ColorRange = 0.5f,
                WavePeriod = 10f,
                Flags = flags
            }
        };
        return new Fixture(cell, heights, landTexture, grass);
    }

    private static float[] CreateFlatHeights()
    {
        return new float[33 * 33];
    }

    private static float[] CreatePlanarHeights(float dzdx)
    {
        var heights = new float[33 * 33];
        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                heights[y * 33 + x] = x * 128f * dzdx;
            }
        }

        return heights;
    }

    private static void AssertAxis(Matrix4x4 matrix, int row, Vector3 expected)
    {
        var actual = row switch
        {
            1 => new Vector3(matrix.M11, matrix.M12, matrix.M13),
            2 => new Vector3(matrix.M21, matrix.M22, matrix.M23),
            3 => new Vector3(matrix.M31, matrix.M32, matrix.M33),
            _ => throw new ArgumentOutOfRangeException(nameof(row))
        };
        Assert.Equal(expected.X, actual.X, 5);
        Assert.Equal(expected.Y, actual.Y, 5);
        Assert.Equal(expected.Z, actual.Z, 5);
    }

    private sealed record Fixture(
        CellRecord Cell,
        float[] Heights,
        LandscapeTextureRecord LandTexture,
        GrassRecord Grass);
}