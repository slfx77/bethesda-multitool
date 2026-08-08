using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Pins the Oblivion water surface-fog source: the engine fills c9 FogParam AND the fog color
///     from the SCENE fog property (FUN_007dcbd0 — tools/GhidraProject/
///     oblivion_water_fog_source_decompiled.txt), NOT from the WATR DATA fog range (that pair is the
///     underwater range; DefaultWater authors FogNear = −8192, which fog-washed the whole surface
///     when the shader blended with it — "water looks more like fog than water").
/// </summary>
public sealed class OblivionWaterFogSourceContractTests
{
    [Fact]
    public void OblivionSurfaceFogBlock_UsesSceneFogDistances_NotWatrRange()
    {
        var shader = SourceContract.ReadShaderSource("water_oblivion.frag.hlsl");

        var blockStart = shader.IndexOf(
            "// Oblivion WATER000 surface fog:", StringComparison.Ordinal);
        Assert.True(blockStart >= 0, "Oblivion surface-fog block comment missing.");
        var blockEnd = shader.IndexOf("return float4(color, alpha);", blockStart, StringComparison.Ordinal);
        Assert.True(blockEnd > blockStart);
        var block = shader[blockStart..blockEnd];

        // Scene fog distances from the atmosphere CB drive visibility…
        Assert.Contains("uAtmosphereParams.z > uAtmosphereParams.y", block, StringComparison.Ordinal);
        Assert.Contains("saturate((uAtmosphereParams.z - viewDist)", block, StringComparison.Ordinal);
        // …the WATR fog range (uLegacySurface1.xy) must NOT re-enter the surface blend, and the
        // Skyrim-curve ApplyFog helper must stay out of the recovered TES4 composite.
        Assert.DoesNotContain("uLegacySurface1.x", block, StringComparison.Ordinal);
        Assert.DoesNotContain("uLegacySurface1.y", block, StringComparison.Ordinal);
        Assert.DoesNotContain("ApplyFog(", block, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The ripple distance attenuation must be CLAMPED, and the clamp must be labelled as a guard
    ///     rather than recovered math.
    ///     <para>
    ///         <c>WATER000.pso</c> computes it with no <c>_sat</c> (asm: <c>def c14, 2, -1, 0, -0.000122</c>;
    ///         <c>mad r2.w, dist, c14.w, -c14.y</c>; <c>mul r3.w, r2.w, r2.w</c>). Retail never needs one
    ///         because 0.000122 is 1/8192 and its water grid + fog far keep <c>distXY</c> inside that
    ///         envelope. This viewer allows a 34,000-unit aerial camera, where the unsaturated term
    ///         crosses zero at 8192 and then GROWS — reaching ~4.2 at the reported top-down pose, which
    ///         amplified the encoded perturbation instead of flattening it and lit the sun lobe across
    ///         the far field. It also drove the detail blend weight NEGATIVE past 8192.
    ///     </para>
    /// </summary>
    [Fact]
    public void OblivionRippleAttenuation_IsClampedToTheEngineEnvelope()
    {
        var shader = SourceContract.ReadShaderSource("water_oblivion.frag.hlsl");

        Assert.Contains(
            "float oblivionLinearDistanceAtten = saturate(1.0 - distXY * 0.000122);",
            shader, StringComparison.Ordinal);
        // The SQUARED term drives the normal; the UNSQUARED one drives the detail blend. Keeping both
        // derived from the saturated linear value is what fixes the negative-weight extrapolation.
        Assert.Contains(
            "float oblivionDistanceAtten = oblivionLinearDistanceAtten * oblivionLinearDistanceAtten;",
            shader, StringComparison.Ordinal);

        // An FNV ramp used to be declared here and never read; it must not creep back.
        Assert.DoesNotContain("float noiseFade", shader, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The TNAM DetailMap samples at the SAME UV scale as the NormalMap.
    ///     <para>
    ///         Re-verified against <c>oblivion_water_pkg019.asm</c>: asm 26 builds the NormalMap UV as
    ///         <c>add r2.xy, a6, c0/*Scroll*/</c>, and asm 40 builds the DetailMap UV as
    ///         <c>mad r1.xy, r3/*N*/, c4.xxxx, r2</c> — the same <c>r2</c>, offset by <c>N.xy * c4.x</c>
    ///         with <c>def c4 = (0.1, …)</c>. There is no tiling divisor anywhere in the program.
    ///         The shader previously multiplied by 4.75, attributed to an ini <c>fTileTextureDivisor</c>
    ///         — but that key is MORROWIND's, so the detail map tiled 4.75x finer than retail.
    ///     </para>
    /// </summary>
    [Fact]
    public void OblivionDetailMap_SharesTheNormalMapUvScale()
    {
        var shader = SourceContract.ReadShaderSource("water_oblivion.frag.hlsl");

        Assert.Contains(
            "float2 detailUv = input.vWorldPos.xy * fMacro + uLegacySurface0.zw * t + N.xy * 0.1;",
            shader, StringComparison.Ordinal);
        // Guard the code, not the prose — the comment above the fix deliberately names 4.75 so the
        // dead end is not rediscovered, so only the executable form may be asserted absent.
        Assert.DoesNotContain("fMacro * 4.75", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("(fMacro * 4.75)", shader, StringComparison.Ordinal);
    }
}