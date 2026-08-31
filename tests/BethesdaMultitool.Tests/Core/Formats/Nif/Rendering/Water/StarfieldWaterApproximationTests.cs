using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class StarfieldWaterApproximationTests
{
    [Fact]
    public void FromWaterRecord_PreservesExactTypedPayloadAndIgnoresAnamOpacity()
    {
        var visual = BuildVisual();
        var lowOpacity = BuildWater(1, visual);
        var highOpacity = BuildWater(99, visual);

        var low = Assert.IsType<StarfieldWaterApproximation>(
            StarfieldWaterApproximation.FromWaterRecord(lowOpacity));
        var high = Assert.IsType<StarfieldWaterApproximation>(
            StarfieldWaterApproximation.FromWaterRecord(highOpacity));

        Assert.Same(visual, low.VisualData);
        Assert.Same(visual, high.VisualData);
        Assert.Equal(low.ProjectFrameUniforms().Surface, high.ProjectFrameUniforms().Surface);
        Assert.Equal(low.ProjectFrameUniforms().DepthFlow, high.ProjectFrameUniforms().DepthFlow);
    }

    [Fact]
    public void ProjectFrameUniforms_PreservesEveryDirectShaderInputAndOptionalVectorPresence()
    {
        var material = Assert.IsType<StarfieldWaterApproximation>(
            StarfieldWaterApproximation.FromWaterRecord(BuildWater(73, BuildVisual())));
        var actual = material.ProjectFrameUniforms();

        Assert.Equal(new Vector4(0.17f, 0.42f, 0.25f, 0.75f), actual.Surface);
        Assert.Equal(new Vector4(12f, 3.5f, 0.6f, 8f), actual.DepthFlow);
        Assert.Equal(new Vector4(101f, 10f, 1.5f, 0.2f), actual.Layer1);
        Assert.Equal(new Vector4(202f, 20f, 2.5f, 0.3f), actual.Layer2);
        Assert.Equal(new Vector4(303f, 30f, 3.5f, 0.4f), actual.Layer3);
        Assert.Equal(0.11f, actual.LayerFalloffsFlags.X);
        Assert.Equal(0.22f, actual.LayerFalloffsFlags.Y);
        Assert.Equal(0.33f, actual.LayerFalloffsFlags.Z);
        Assert.Equal(
            (uint)(StarfieldWaterFlags.EnableFlowmap | StarfieldWaterFlags.BlendNormals),
            (uint)BitConverter.SingleToInt32Bits(actual.LayerFalloffsFlags.W));
        Assert.Equal(new Vector4(4f, 5f, 6f, 1f), actual.LinearVelocity);
        Assert.Equal(Vector4.Zero, actual.AngularVelocity);
        Assert.Equal(new Vector4(9f, 10f, 11f, 12f), actual.Displacement0);
        Assert.Equal(new Vector4(13f, 0.8f, 14f, 15f), actual.Displacement1);
        Assert.Equal(new Vector4(0.1f, 0.2f, 0.3f, 0f), actual.Absorption);
        Assert.Equal(new Vector4(0.4f, 0.5f, 0.7f, 0.6f), actual.Concentrations);
        Assert.Equal(new Vector4(16f / 255f, 32f / 255f, 64f / 255f, 128f / 255f),
            actual.UnderwaterColor);
    }

    [Fact]
    public void GlobalTextureSlots_AreExplicitlyLabelledAsInferredRetailAssets()
    {
        Assert.Equal(3, StarfieldWaterApproximation.InferredGlobalTexturePaths.Count);
        Assert.Equal(3, StarfieldWaterApproximation.InferredGlobalTextureRoles.Count);
        Assert.Equal(@"textures\water\defaultwater_normal.dds",
            StarfieldWaterApproximation.InferredGlobalTexturePaths[0]);
        Assert.Equal(@"textures\water\defaultwatertile_normal.dds",
            StarfieldWaterApproximation.InferredGlobalTexturePaths[1]);
        Assert.Equal(@"textures\water\defaultflow_normal.dds",
            StarfieldWaterApproximation.InferredGlobalTexturePaths[2]);
        Assert.All(StarfieldWaterApproximation.InferredGlobalTextureRoles,
            role => Assert.Contains("inferred-slot", role, StringComparison.Ordinal));
    }

    [Fact]
    public void FromWaterRecord_FailsClosedWithoutExactTypedPayload()
    {
        Assert.Null(StarfieldWaterApproximation.FromWaterRecord(null));
        Assert.Null(StarfieldWaterApproximation.FromWaterRecord(new WaterRecord()));
        Assert.Null(StarfieldWaterApproximation.FromWaterRecord(new WaterRecord
        {
            VisualProperties = new Dictionary<string, object?>
            {
                ["StarfieldVisualData"] = "not typed"
            }
        }));
    }

    private static WaterRecord BuildWater(byte opacity, StarfieldWaterVisualData visual) => new()
    {
        Opacity = opacity,
        VisualProperties = new Dictionary<string, object?>
        {
            ["StarfieldVisualData"] = visual
        }
    };

    private static StarfieldWaterVisualData BuildVisual() => new()
    {
        Flags = StarfieldWaterFlags.EnableFlowmap | StarfieldWaterFlags.BlendNormals,
        LinearVelocity = (4f, 5f, 6f),
        AngularVelocity = null,
        Dnam = new StarfieldWaterDnam
        {
            DepthAmount = 12f,
            AbsorptionRanges = (0.1f, 0.2f, 0.3f),
            PhytoplanktonConcentration = 0.4f,
            SedimentConcentration = 0.5f,
            YellowMatterConcentration = 0.7f,
            Oceanness = 0.6f,
            UnderwaterColor = (16, 32, 64, 128),
            UnderwaterFogAmount = 0.8f,
            UnderwaterFogNear = 14f,
            UnderwaterFogFar = 15f,
            NormalMagnitude = 0.42f,
            ShallowNormalFalloff = 0.25f,
            DeepNormalFalloff = 0.75f,
            SurfaceEffectFalloff = 8f,
            DisplacementForce = 9f,
            DisplacementVelocity = 10f,
            DisplacementFalloff = 11f,
            DisplacementDampener = 12f,
            DisplacementStartingSize = 13f,
            Layer1 = new StarfieldWaterNoiseLayer
            {
                UvScale = 101f, WindDirection = 10f, WindSpeed = 1.5f,
                AmplitudeScale = 0.2f, NoiseFalloff = 0.11f
            },
            Layer2 = new StarfieldWaterNoiseLayer
            {
                UvScale = 202f, WindDirection = 20f, WindSpeed = 2.5f,
                AmplitudeScale = 0.3f, NoiseFalloff = 0.22f
            },
            Layer3 = new StarfieldWaterNoiseLayer
            {
                UvScale = 303f, WindDirection = 30f, WindSpeed = 3.5f,
                AmplitudeScale = 0.4f, NoiseFalloff = 0.33f
            },
            FlowmapScale = 3.5f,
            Roughness = 0.17f
        }
    };
}
