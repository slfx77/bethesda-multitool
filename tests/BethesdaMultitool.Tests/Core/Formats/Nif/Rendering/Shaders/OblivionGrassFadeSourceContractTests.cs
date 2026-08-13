using System.Globalization;
using System.Text.RegularExpressions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Source contracts for Oblivion's grass distance-fade ramp (retail GRASS2020.vso oT5.w,
///     disassembly lines 81-97 of tools/GhidraProject/oblivion_grass_shaderpackage019_disassembled.txt;
///     multiplied into the blended alpha by GRASS2002.pso line 2670 and the same closing multiply in
///     GRASS2003-2008). The ramp HIDES the envelope boundary; the CPU hard cull at the envelope end
///     KEEPS the draw count — both halves are pinned here so neither can silently drop the other.
/// </summary>
public sealed class OblivionGrassFadeSourceContractTests
{
    [Fact]
    public void VertexShaderComputesTheTwoSidedRampInRetailForm()
    {
        var vs = SourceContract.ReadShaderSource("reference_grass_oblivion.vert.hlsl");

        // The two-sided form: a fade-in factor (neutralized while its CPU fill is unrecovered) times
        // the linear fade-out. Both saturate, matching retail's max-0/min-1 pair.
        Assert.Contains(
            "saturate((cameraDistance - GrassFadeInStart) / GrassFadeInRange)",
            vs,
            StringComparison.Ordinal);
        Assert.Contains(
            "float fadeOut = 1.0 - saturate((cameraDistance - GrassFadeOutStart) / GrassFadeOutRange);",
            vs,
            StringComparison.Ordinal);
        Assert.Contains("return fadeIn * fadeOut;", vs, StringComparison.Ordinal);

        // The fade-in pair has no recovered CPU fill: a zero range must pin the factor at 1 rather
        // than divide by zero or invent values.
        Assert.Contains("static const float GrassFadeInRange = 0.0;", vs, StringComparison.Ordinal);
        Assert.Contains("GrassFadeInRange > 0.0", vs, StringComparison.Ordinal);

        // Documented deviation stays documented: our d is horizontal world distance from the fog
        // camera (retail's is length(clipPos)) — the same metric the CPU hard cull uses.
        Assert.Contains(
            "length(worldPos.xy - uCameraPosFogPower.xy)",
            vs,
            StringComparison.Ordinal);
        Assert.Contains("length(clipPos)", vs, StringComparison.Ordinal);
    }

    [Fact]
    public void FragmentShaderMultipliesTheRampIntoTheBlendedAlpha()
    {
        var fs = SourceContract.ReadShaderSource("reference_grass_oblivion.frag.hlsl");

        // GRASS2002.pso `mul r0.w, r0.w, a5.w`: the envelope multiplies the BLENDED alpha (material
        // alpha included), not the alpha-test input — the blade mask test stays on raw texture alpha.
        Assert.Contains(
            "float outAlpha = saturate(sample.a * input.vAlphaState.z) * saturate(input.vGrassDistanceFade);",
            fs,
            StringComparison.Ordinal);

        // A2C stays reverted (2026-07-26): the ramp relies on the restored blend path.
        Assert.DoesNotContain("ALPHA_TO_COVERAGE", fs, StringComparison.Ordinal);
    }

    [Fact]
    public void RampInterpolantLaneMatchesAcrossTheShaderPair()
    {
        var vs = SourceContract.ReadShaderSource("reference_grass_oblivion.vert.hlsl");
        var fs = SourceContract.ReadShaderSource("reference_grass_oblivion.frag.hlsl");

        const string lane = "float vGrassDistanceFade : TEXCOORD7;";
        Assert.Contains(lane, vs, StringComparison.Ordinal);
        Assert.Contains(lane, fs, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderEnvelopeConstantsMatchTheOblivionScatterProfile()
    {
        var vs = SourceContract.ReadShaderSource("reference_grass_oblivion.vert.hlsl");
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.Oblivion).DistanceEnvelope;

        // The shader constants are compile-time (the blended PerDraw CB is full at its 256-byte CBV
        // allocation), so profile and shader can only stay in sync by contract — this is it.
        Assert.Equal(envelope.FadeStart, ParseShaderFloat(vs, "GrassFadeOutStart"));
        Assert.Equal(envelope.FadeRange, ParseShaderFloat(vs, "GrassFadeOutRange"));

        // Shipped Oblivion_default.ini [Grass]: fGrassStartFadeDistance=2000, fGrassEndDistance=3000.
        // Raising the distance is a USER decision — these values must not drift in a render change.
        Assert.Equal(2000f, envelope.FadeStart);
        Assert.Equal(1000f, envelope.FadeRange);
        Assert.Equal(3000f, envelope.HardEnd);
    }

    [Theory]
    [InlineData(0f, true)]
    [InlineData(1999f, true)]
    [InlineData(2000f, true)]
    [InlineData(2999f, true)]
    [InlineData(3000f, true)]
    [InlineData(3001f, false)]
    public void HardCullStillRemovesFullyFadedBladesAtTheEnvelopeEnd(float distance, bool expected)
    {
        var envelope = GrassScatterProfile.ForGame(BethesdaGame.Oblivion).DistanceEnvelope;

        // Exact per-frame filtering (zero slack): the ramp hides the boundary, the cull keeps the
        // draw count — a fully faded blade beyond 3000 must never survive to a draw.
        var actual = GrassDistanceCullPolicy.Passes(
            true,
            in envelope,
            distance * distance,
            12000f);

        Assert.Equal(expected, actual);
    }

    private static float ParseShaderFloat(string source, string constantName)
    {
        var match = Regex.Match(
            source,
            $@"static const float {Regex.Escape(constantName)} = ([0-9]+(?:\.[0-9]+)?);");
        Assert.True(match.Success, $"'{constantName}' constant not found in shader source.");
        return float.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
    }
}
