using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

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
            new Vector3(1f, 0.5f, 0.5f),
            new Vector3(2f, 0f, 0f),
            new Vector3(0f, 3f, 0f),
            new Vector3(0f, 0f, 4f),
            new Vector3(1f, 2f, 0f),
            Vector3.Zero,
            Vector3.One,
            Vector3.One,
            new Vector3(0.1f));

        var inverseRootTen = 1f / MathF.Sqrt(10f);
        VectorAssert.Equal(Vector3.UnitX, result.NormalizedDecodedNormal);
        VectorAssert.Equal(new Vector3(inverseRootTen, 3f * inverseRootTen, 0f),
            result.NormalizedTangentSpaceLight);
        Assert.Equal(inverseRootTen, result.RawSignedDot, 6);
        VectorAssert.Equal(new Vector3(inverseRootTen), result.Shade);
        VectorAssert.Equal(result.Shade, result.Rgb);

        // A decoded normal with twice the signed magnitude has the same result: SLS2000 normalizes it
        // directly and has no separate bump-scale term.
        var halfMagnitude = FnvActiveAdtBasePolicy.EvaluateSls2000(
            FnvClassicBasicShaderMode.Sls1009,
            new Vector3(0.75f, 0.5f, 0.5f),
            new Vector3(2f, 0f, 0f),
            new Vector3(0f, 3f, 0f),
            new Vector3(0f, 0f, 4f),
            new Vector3(1f, 2f, 0f),
            Vector3.Zero,
            Vector3.One,
            Vector3.One,
            Vector3.One);
        VectorAssert.Equal(result.NormalizedDecodedNormal, halfMagnitude.NormalizedDecodedNormal);
        Assert.Equal(result.RawSignedDot, halfMagnitude.RawSignedDot, 6);
    }

    [Fact]
    public void Sls2000Oracle_KeepsSignedDotAndClampsOnlyTheFinalShade()
    {
        var result = FnvActiveAdtBasePolicy.EvaluateSls2000(
            FnvClassicBasicShaderMode.Sls1009,
            new Vector3(0f, 0.5f, 0.5f),
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            Vector3.UnitX,
            new Vector3(0.25f, 1.25f, 0.5f),
            new Vector3(0.5f, 0.5f, 0.25f),
            new Vector3(0.8f, 0.4f, 0.2f),
            new Vector3(0.01f));

        Assert.Equal(-1f, result.RawSignedDot, 6);
        VectorAssert.Equal(new Vector3(0f, 0.75f, 0.25f), result.Shade);
        VectorAssert.Equal(new Vector3(0f, 0.3f, 0.05f), result.Rgb);
    }

    [Fact]
    public void Sls2000Oracle_MultipliesVertexRgbOnlyForTheClassifiedVertexColorVariant()
    {
        var ordinary = EvaluateAligned(FnvClassicBasicShaderMode.Sls1009);
        var vertexColor = EvaluateAligned(FnvClassicBasicShaderMode.Sls1013VertexColor);

        VectorAssert.Equal(new Vector3(0.15f, 0.075f, 0.225f), ordinary.Rgb);
        VectorAssert.Equal(new Vector3(0.12f, 0.03f, 0.045f), vertexColor.Rgb);
        Assert.Equal(ordinary.RawSignedDot, vertexColor.RawSignedDot, 6);
        VectorAssert.Equal(ordinary.Shade, vertexColor.Shade);
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

    /// <summary>
    ///     The runtime no-sun-shadow bits are a CPU↔GPU contract carried through a float: C# ORs the bit
    ///     into <c>TextureState.z</c> and the pixel shader masks it back out. Nothing but agreement of
    ///     two magic numbers holds them together, so pin both sides — a desync silently either restores
    ///     the artifact or suppresses shadows on the wrong draws, with no build or test failure.
    /// </summary>
    [Fact]
    public void RuntimeNoSunShadowFlags_MatchTheMasksTheReferenceShaderTests()
    {
        Assert.Equal(4096u, FnvActiveAdtBasePolicy.RuntimeFnvGrassNoSunShadowFlag);
        Assert.Equal(8192u, FnvActiveAdtBasePolicy.RuntimeSpeedTreeLeafNoSunShadowFlag);

        var shader = SourceContract.ReadShaderSource("reference.frag.hlsl");
        Assert.Contains(
            "bool HasFnvGrassNoSunShadow(float packedState)", shader, StringComparison.Ordinal);
        Assert.Contains(
            "MaterialTextureFlags(packedState) & 4096u", shader, StringComparison.Ordinal);
        Assert.Contains(
            "bool HasSpeedTreeLeafNoSunShadow(float packedState)", shader, StringComparison.Ordinal);
        Assert.Contains(
            "MaterialTextureFlags(packedState) & 8192u", shader, StringComparison.Ordinal);

        // Both bits must gate the SAME sun-cascade lookup; a helper that is declared but never
        // consulted is the failure mode this catches.
        SourceContract.AssertOrder(
            shader,
            "float sunShadow = !fullBright && !fnvActiveAdtBase",
            "!HasFnvGrassNoSunShadow(input.vTextureState.z)",
            "!HasSpeedTreeLeafNoSunShadow(input.vTextureState.z)",
            "? ShadowFactor(input.vWorldPos)");
    }

    /// <summary>
    ///     Leaf cards are flagged for FO3 as well as FNV (shared STLEAF family), and the flag is applied
    ///     OUTSIDE the FNV-only ADT-base block so FO3 is not dragged onto an unrecovered lighting route.
    ///     Pinned at the source level because the call site needs a live renderer + GPU device.
    /// </summary>
    [Fact]
    public void LeafCardNoSunShadowFlag_IsAppliedForFallout3AndNewVegasOutsideTheAdtBlock()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        SourceContract.AssertOrder(
            renderer,
            "private Vector4 ResolveTextureState(CachedSubmesh12 submesh)",
            "BethesdaGame.FalloutNewVegas",
            "or Core.Games.BethesdaGame.Fallout3 && submesh.IsLeafBillboard",
            "FnvActiveAdtBasePolicy.RuntimeSpeedTreeLeafNoSunShadowFlag",
            "if (_renderCache?.Game == Core.Games.BethesdaGame.FalloutNewVegas)");
    }

    private static FnvActiveAdtBaseEvaluation EvaluateAligned(FnvClassicBasicShaderMode mode)
    {
        return FnvActiveAdtBasePolicy.EvaluateSls2000(
            mode,
            new Vector3(0.5f, 0.5f, 1f),
            Vector3.UnitX,
            Vector3.UnitY,
            Vector3.UnitZ,
            new Vector3(0f, 0f, 8f),
            new Vector3(0.1f),
            new Vector3(0.2f),
            new Vector3(0.5f, 0.25f, 0.75f),
            new Vector3(0.8f, 0.4f, 0.2f));
    }

    private static FnvActiveAdtBaseEligibility Eligible(
        FnvClassicBasicShaderMode mode = FnvClassicBasicShaderMode.Sls1009)
    {
        return new FnvActiveAdtBaseEligibility(
            BethesdaGame.FalloutNewVegas,
            true,
            0,
            false,
            false,
            false,
            false,
            1f,
            false,
            mode);
    }
}