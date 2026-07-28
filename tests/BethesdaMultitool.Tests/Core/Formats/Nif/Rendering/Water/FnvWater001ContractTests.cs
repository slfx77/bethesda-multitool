using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class FnvWater001ContractTests
{
    [Fact]
    public void ObliqueDepthReconstructionKeepsIndependentPathAndVerticalLanes()
    {
        var ok = FnvWater001Contract.TryReconstructDepthLanes(
            new Vector3(0f, 0f, 10f),
            new Vector3(10f, 0f, 0f),
            30f,
            10f,
            0f,
            100f,
            out var depth,
            out var scenePoint);

        Assert.True(ok);
        VectorAssert.Equal(new Vector3(30f, 0f, -20f), scenePoint);
        Assert.Equal(0.2828427f, depth.X, 6);
        Assert.Equal(0.2f, depth.Y, 6);
        Assert.NotEqual(depth.X, depth.Y);
    }

    [Fact]
    public void DepthCorrectionAndDistortionUseRecoveredLaneRolesWithoutPreSaturation()
    {
        var raw = new Vector2(1.4f, 0.25f);
        var corrected = FnvWater001Contract.CorrectDepthLanes(raw, 0.5f);
        var delta = FnvWater001Contract.DistortionDelta(
            raw,
            0.375f,
            7.2f,
            new Vector3(0.1f, -0.2f, 0.9746794f));

        // lerp(1,D,fade), then saturate: D.x is demonstrably not clamped before interpolation.
        Assert.Equal(1f, corrected.X);
        Assert.Equal(0.625f, corrected.Y);
        // Distortion uses raw D.y, not corrected D.y or path D.x.
        Assert.Equal(0.0675f, delta.X, 6);
        Assert.Equal(-0.135f, delta.Y, 6);
    }

    [Fact]
    public void ProjectiveSnapshotUvUsesHomogeneousDivideAndD3dYFlip()
    {
        var projection = Matrix4x4.Identity;
        projection.M11 = 2f;
        projection.M22 = 2f;
        projection.M34 = 1f; // System.Numerics row-vector convention: clip.w = z + 1

        var ok = FnvWater001Contract.TryProjectSnapshotUv(
            projection,
            Vector3.Zero,
            new Vector3(0.3f, -0.15f, 0.5f),
            100,
            50,
            out var uv);

        Assert.True(ok);
        Assert.Equal(0.7f, uv.X, 6);
        Assert.Equal(0.6f, uv.Y, 6);
    }

    [Theory]
    [InlineData(0.5f, 30f, 10f, -2f, 0f, true)]
    [InlineData(0.5f, 8f, 10f, -2f, 0f, false)]
    [InlineData(0.5f, 30f, 10f, 2f, 0f, false)]
    [InlineData(0f, 30f, 10f, -2f, 0f, false)]
    [InlineData(0.5f, float.NaN, 10f, -2f, 0f, false)]
    public void DisplacedRefractionTapRejectsForegroundAndAboveWaterContent(
        float sceneNdc,
        float sceneDistance,
        float waterDistance,
        float scenePointZ,
        float planeHeight,
        bool expected)
    {
        Assert.Equal(expected, FnvWater001Contract.IsValidDisplacedRefractionSample(
            sceneNdc,
            sceneDistance,
            waterDistance,
            scenePointZ,
            planeHeight));
    }

    [Fact]
    public void BilinearRefractionFootprintRejectsWhenAnyOfFourDepthTapsIsUnsafe()
    {
        var safe = new FnvWater001DisplacedDepthTap(0.5f, 30f, 10f, -2f);
        var taps = new[] { safe, safe, safe, safe };

        Assert.True(FnvWater001Contract.IsValidDisplacedRefractionFootprint(taps, 0f));

        taps[2] = safe with { SceneDistance = 8f, ScenePointZ = 2f };
        Assert.False(FnvWater001Contract.IsValidDisplacedRefractionFootprint(taps, 0f));
        Assert.False(FnvWater001Contract.IsValidDisplacedRefractionFootprint(taps.AsSpan(..3), 0f));
    }

    [Fact]
    public void RecoveredCompositeTermsKeepRefractionBodyAndReflectionDepthLerpsSeparate()
    {
        var correctedDepth = FnvWater001Contract.CorrectDepthLanes(new Vector2(0.4f, 0.25f), 0.5f);
        var depthT = Math.Clamp((0.25f - 0.1f) / (0.5f - 0.1f), 0f, 1f);
        var aboveFog = FnvWater001Contract.AboveWaterFogWeight(
            correctedDepth.X, -0.25f, 1f,
            0.75f);
        var body = Vector3.Lerp(new Vector3(0.1f, 0.2f, 0.3f),
            new Vector3(0.5f, 0.6f, 0.7f), correctedDepth.Y);
        var litBody = body * 0.8f;
        var transmitted = Vector3.Lerp(new Vector3(0.2f, 0.3f, 0.4f), litBody, depthT * aboveFog);
        var fresnel = 0.025f + (1f - 0.025f) * MathF.Pow(1f - 0.8f, 5f);
        var bodyReflection = Vector3.Lerp(litBody, new Vector3(0.8f, 0.7f, 0.6f),
            correctedDepth.X * fresnel);
        var composite = Vector3.Lerp(transmitted, bodyReflection, correctedDepth.Y);

        Assert.Equal(new Vector2(0.7f, 0.625f), correctedDepth);
        Assert.Equal(0.375f, depthT, 6);
        Assert.Equal(0.57f, aboveFog, 6);
        VectorAssert.Equal(new Vector3(0.2171f, 0.312825f, 0.40855f), transmitted);
        VectorAssert.Equal(new Vector3(0.28921357f, 0.36602426f, 0.44283494f), bodyReflection);
        VectorAssert.Equal(new Vector3(0.26217097f, 0.34607452f, 0.42997807f), composite);
    }

    [Fact]
    public void PreflightMayRequestSnapshotOnlyAfterEveryOtherGatePasses()
    {
        var input = ValidInput() with
        {
            RequireSnapshot = false,
            HasSnapshot = false,
            SnapshotWidth = 0,
            SnapshotHeight = 0
        };

        var result = FnvWater001Contract.Evaluate(input);

        Assert.True(result.Candidate);
        Assert.Equal(FnvWater001FallbackReason.None, result.Reason);
        Assert.Equal("eligible", result.ReasonCode);
    }

    [Theory]
    [MemberData(nameof(InvalidEligibilityCases))]
    public void EveryGlobalEligibilityFailureHasAStableReason(
        object inputValue,
        object expectedValue)
    {
        var input = Assert.IsType<FnvWater001EligibilityInput>(inputValue);
        var expected = Assert.IsType<FnvWater001FallbackReason>(expectedValue);
        var result = FnvWater001Contract.Evaluate(input);

        Assert.False(result.Candidate);
        Assert.Equal(expected, result.Reason);
        Assert.NotEqual("unknown", result.ReasonCode);
    }

    public static IEnumerable<object[]> InvalidEligibilityCases()
    {
        var valid = ValidInput();
        yield return [valid with { Game = BethesdaGame.Fallout3 }, FnvWater001FallbackReason.NotFalloutNewVegas];
        yield return
        [
            valid with { ShaderVariant = WaterShaderVariant.OblivionWater000 },
            FnvWater001FallbackReason.NonClassicWaterShader
        ];
        yield return [valid with { IsLava = true }, FnvWater001FallbackReason.Lava];
        yield return [valid with { IsPerspectiveProjection = false }, FnvWater001FallbackReason.OrthographicProjection];
        yield return [valid with { HasSceneDepth = false }, FnvWater001FallbackReason.SceneDepthUnavailable];
        yield return [valid with { HasSnapshot = false }, FnvWater001FallbackReason.SnapshotUnavailable];
        yield return [valid with { SnapshotWidth = 0 }, FnvWater001FallbackReason.SnapshotDimensionsInvalid];
        yield return [valid with { VisibleCellCount = 0 }, FnvWater001FallbackReason.NoVisibleCellWater];
        yield return [valid with { HasAppearance = false }, FnvWater001FallbackReason.MissingAppearance];
        yield return
            [valid with { HasAuthoredClassicInputs = false }, FnvWater001FallbackReason.MissingAuthoredClassicInputs];
        yield return [valid with { HasWaterTypeContext = false }, FnvWater001FallbackReason.MissingWaterTypeContext];
        yield return
            [valid with { HasEffectiveWaterType = false }, FnvWater001FallbackReason.MissingEffectiveWaterType];
        yield return [valid with { HasMixedWaterTypes = true }, FnvWater001FallbackReason.MixedVisibleWaterTypes];
        yield return [valid with { HasMixedPlaneHeights = true }, FnvWater001FallbackReason.MixedVisiblePlaneHeights];
        yield return [valid with { CameraHeight = 0f }, FnvWater001FallbackReason.CameraNotAbovePlane];
        yield return [valid with { DepthFalloffEnd = 0f }, FnvWater001FallbackReason.InvalidDepthFalloffRange];
        yield return [valid with { UnderwaterFogFar = 0f }, FnvWater001FallbackReason.InvalidUnderwaterFogFar];
        yield return [valid with { UnderwaterFogNear = 6000f }, FnvWater001FallbackReason.InvalidUnderwaterFogRange];
        yield return [valid with { AboveWaterFogAmount = 1.1f }, FnvWater001FallbackReason.InvalidAboveWaterFogAmount];
        yield return
            [valid with { RefractionDistortionAmount = -1f }, FnvWater001FallbackReason.InvalidRefractionDistortion];
    }

    private static FnvWater001EligibilityInput ValidInput()
    {
        return new FnvWater001EligibilityInput(
            BethesdaGame.FalloutNewVegas,
            WaterShaderVariant.FnvWater000,
            false,
            true,
            true,
            true,
            true,
            1920,
            1080,
            9,
            true,
            true,
            true,
            true,
            false,
            false,
            0f,
            128f,
            0f,
            0.01f,
            -2500f,
            5500f,
            0.75f,
            600f,
            0x001009CA);
    }
}