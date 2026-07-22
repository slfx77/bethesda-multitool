using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class FnvActiveAdtBasePolicyTests
{
    [Theory]
    [InlineData((int)FnvClassicBasicShaderMode.Sls1009, false)]
    [InlineData((int)FnvClassicBasicShaderMode.Sls1013VertexColor, true)]
    public void EligibilityAndFlags_SelectOnlyTheBoundedActiveAdtRoute(
        int classifierModeValue,
        bool expectedVertexColor)
    {
        var classifierMode = (FnvClassicBasicShaderMode)classifierModeValue;
        var eligibility = Eligible(classifierMode);

        Assert.Equal(193, FnvActiveAdtBasePolicy.ActiveAdtPassId);
        Assert.True(FnvActiveAdtBasePolicy.IsEligible(eligibility));

        const uint unrelated = 0x45u;
        var flags = FnvActiveAdtBasePolicy.ApplyRuntimeFlags(eligibility, unrelated);
        Assert.Equal(unrelated, flags & unrelated);
        Assert.NotEqual(0u, flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtFlag);
        Assert.Equal(
            expectedVertexColor,
            (flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtVertexColorFlag) != 0);
    }

    [Theory]
    [InlineData("other-game")]
    [InlineData("lighting-off")]
    [InlineData("one-local-light")]
    [InlineData("negative-local-count")]
    [InlineData("projected-sun-shadow")]
    [InlineData("fog-enabled")]
    [InlineData("alpha-blend")]
    [InlineData("alpha-test")]
    [InlineData("alpha-below-one")]
    [InlineData("alpha-above-one")]
    [InlineData("alpha-nan")]
    [InlineData("alpha-controller")]
    [InlineData("classifier-none")]
    [InlineData("classifier-undefined")]
    public void Eligibility_FailsClosedWhenAnyRouteInvariantIsMissing(string rejectedInvariant)
    {
        var eligibility = Eligible() with
        {
            Game = rejectedInvariant == "other-game"
                ? BethesdaGame.Fallout3
                : BethesdaGame.FalloutNewVegas,
            LightingEnabled = rejectedInvariant != "lighting-off",
            PlacedLightCount = rejectedInvariant switch
            {
                "one-local-light" => 1,
                "negative-local-count" => -1,
                _ => 0
            },
            HasProjectedSunShadow = rejectedInvariant == "projected-sun-shadow",
            FogEnabled = rejectedInvariant == "fog-enabled",
            HasAlphaBlend = rejectedInvariant == "alpha-blend",
            HasAlphaTest = rejectedInvariant == "alpha-test",
            MaterialAlpha = rejectedInvariant switch
            {
                "alpha-below-one" => 0.999f,
                "alpha-above-one" => 1.001f,
                "alpha-nan" => float.NaN,
                _ => 1f
            },
            HasMaterialAlphaController = rejectedInvariant == "alpha-controller",
            ClassifierMode = rejectedInvariant switch
            {
                "classifier-none" => FnvClassicBasicShaderMode.None,
                "classifier-undefined" => (FnvClassicBasicShaderMode)byte.MaxValue,
                _ => FnvClassicBasicShaderMode.Sls1009
            }
        };

        Assert.False(FnvActiveAdtBasePolicy.IsEligible(eligibility));

        const uint unrelated = 0x45u;
        const uint stalePolicyFlags =
            FnvActiveAdtBasePolicy.RuntimeActiveAdtFlag |
            FnvActiveAdtBasePolicy.RuntimeActiveAdtVertexColorFlag;
        Assert.Equal(
            unrelated,
            FnvActiveAdtBasePolicy.ApplyRuntimeFlags(eligibility, unrelated | stalePolicyFlags));
    }

    [Fact]
    public void Sls2000Oracle_NormalizesDecodedNormalAndPostTbnLightWithoutBumpScale()
    {
        var result = FnvActiveAdtBasePolicy.EvaluateSls2000(
            FnvClassicBasicShaderMode.Sls1009,
            normalMapSample: new Vector3(1f, 0.5f, 0.5f),
            tangent: new Vector3(2f, 0f, 0f),
            bitangent: new Vector3(0f, 3f, 0f),
            vertexNormal: new Vector3(0f, 0f, 4f),
            lightData: new Vector3(1f, 2f, 0f),
            ambientRgb: Vector3.Zero,
            sunRgb: Vector3.One,
            baseRgb: Vector3.One,
            vertexRgb: new Vector3(0.1f));

        var inverseRootTen = 1f / MathF.Sqrt(10f);
        VectorAssert.Equal(Vector3.UnitX, result.NormalizedDecodedNormal, 1e-6f);
        VectorAssert.Equal(new Vector3(inverseRootTen, 3f * inverseRootTen, 0f),
            result.NormalizedTangentSpaceLight, 1e-6f);
        Assert.Equal(inverseRootTen, result.RawSignedDot, 6);
        VectorAssert.Equal(new Vector3(inverseRootTen), result.Shade, 1e-6f);
        VectorAssert.Equal(result.Shade, result.Rgb, 1e-6f);

        // A decoded normal with twice the signed magnitude has the same result: SLS2000 normalizes it
        // directly and has no separate bump-scale term.
        var halfMagnitude = FnvActiveAdtBasePolicy.EvaluateSls2000(
            FnvClassicBasicShaderMode.Sls1009,
            normalMapSample: new Vector3(0.75f, 0.5f, 0.5f),
            tangent: new Vector3(2f, 0f, 0f),
            bitangent: new Vector3(0f, 3f, 0f),
            vertexNormal: new Vector3(0f, 0f, 4f),
            lightData: new Vector3(1f, 2f, 0f),
            ambientRgb: Vector3.Zero,
            sunRgb: Vector3.One,
            baseRgb: Vector3.One,
            vertexRgb: Vector3.One);
        VectorAssert.Equal(result.NormalizedDecodedNormal, halfMagnitude.NormalizedDecodedNormal, 1e-6f);
        Assert.Equal(result.RawSignedDot, halfMagnitude.RawSignedDot, 6);
    }

    [Fact]
    public void Sls2000Oracle_KeepsSignedDotAndClampsOnlyTheFinalShade()
    {
        var result = FnvActiveAdtBasePolicy.EvaluateSls2000(
            FnvClassicBasicShaderMode.Sls1009,
            normalMapSample: new Vector3(0f, 0.5f, 0.5f),
            tangent: Vector3.UnitX,
            bitangent: Vector3.UnitY,
            vertexNormal: Vector3.UnitZ,
            lightData: Vector3.UnitX,
            ambientRgb: new Vector3(0.25f, 1.25f, 0.5f),
            sunRgb: new Vector3(0.5f, 0.5f, 0.25f),
            baseRgb: new Vector3(0.8f, 0.4f, 0.2f),
            vertexRgb: new Vector3(0.01f));

        Assert.Equal(-1f, result.RawSignedDot, 6);
        VectorAssert.Equal(new Vector3(0f, 0.75f, 0.25f), result.Shade, 1e-6f);
        VectorAssert.Equal(new Vector3(0f, 0.3f, 0.05f), result.Rgb, 1e-6f);
    }

    [Fact]
    public void Sls2000Oracle_MultipliesVertexRgbOnlyForTheClassifiedVertexColorVariant()
    {
        var ordinary = EvaluateAligned(FnvClassicBasicShaderMode.Sls1009);
        var vertexColor = EvaluateAligned(FnvClassicBasicShaderMode.Sls1013VertexColor);

        VectorAssert.Equal(new Vector3(0.15f, 0.075f, 0.225f), ordinary.Rgb, 1e-6f);
        VectorAssert.Equal(new Vector3(0.12f, 0.03f, 0.045f), vertexColor.Rgb, 1e-6f);
        Assert.Equal(ordinary.RawSignedDot, vertexColor.RawSignedDot, 6);
        VectorAssert.Equal(ordinary.Shade, vertexColor.Shade, 1e-6f);
    }

    [Fact]
    public void Sls2000Oracle_RejectsAnUnclassifiedMaterial()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            FnvActiveAdtBasePolicy.EvaluateSls2000(
                FnvClassicBasicShaderMode.None,
                Vector3.One,
                Vector3.UnitX,
                Vector3.UnitY,
                Vector3.UnitZ,
                Vector3.UnitZ,
                Vector3.Zero,
                Vector3.One,
                Vector3.One,
                Vector3.One));
    }

    private static FnvActiveAdtBaseEvaluation EvaluateAligned(FnvClassicBasicShaderMode mode) =>
        FnvActiveAdtBasePolicy.EvaluateSls2000(
            mode,
            normalMapSample: new Vector3(0.5f, 0.5f, 1f),
            tangent: Vector3.UnitX,
            bitangent: Vector3.UnitY,
            vertexNormal: Vector3.UnitZ,
            lightData: new Vector3(0f, 0f, 8f),
            ambientRgb: new Vector3(0.1f),
            sunRgb: new Vector3(0.2f),
            baseRgb: new Vector3(0.5f, 0.25f, 0.75f),
            vertexRgb: new Vector3(0.8f, 0.4f, 0.2f));

    private static FnvActiveAdtBaseEligibility Eligible(
        FnvClassicBasicShaderMode mode = FnvClassicBasicShaderMode.Sls1009) => new(
        BethesdaGame.FalloutNewVegas,
        LightingEnabled: true,
        PlacedLightCount: 0,
        HasProjectedSunShadow: false,
        FogEnabled: false,
        HasAlphaBlend: false,
        HasAlphaTest: false,
        MaterialAlpha: 1f,
        HasMaterialAlphaController: false,
        ClassifierMode: mode);
}
