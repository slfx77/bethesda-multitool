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
    ///     The ripple distance attenuation must be CLAMPED — and the clamp IS recovered engine math.
    ///     <para>
    ///         <c>WATER000.pso</c> computes <c>mad_sat r2.w, r1.w, c14.w, -c14.y</c> with
    ///         <c>c14.w = -0.000122070312 = -2^-13</c>, i.e. <c>saturate(1 - dist/8192)</c>, then squares
    ///         it. An earlier docstring here asserted the asm carried "no _sat"; that was FALSE — the
    ///         2026-07-06 dump came from a decoder that dropped every destination modifier (93 saturating
    ///         instructions across the WATER family). Verified 2026-08-08 by raw-token decode and by two
    ///         independent disassemblers agreeing on the regenerated
    ///         <c>tools/GhidraProject/oblivion_water_shaders/oblivion_water_pkg019.asm</c>. Without the
    ///         clamp the term crossed zero at 8192 units and grew, amplifying the perturbation and
    ///         driving the detail blend weight negative — the pre-fix far-field defect.
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
    ///     The TNAM DetailMap samples at the SAME UV base as the NormalMap.
    ///     <para>
    ///         <c>WATER000.pso</c> builds the NormalMap UV as <c>add r2.xy, t6, c0/*Scroll*/</c>
    ///         (t6 = mesh texcoord0) and the DetailMap UV as <c>mad r1.xy, r3/*N*/, c4.xxxx, r2</c> —
    ///         the same <c>r2</c>, offset by <c>N.xy * 0.1</c>. WATER000 applies no divisor between the
    ///         two samplers, so the removal of the 4.75 multiply stands ON THE ASM. But note the
    ///         CORRECTED provenance: the first removal rationale called <c>fTileTextureDivisor</c>
    ///         "MORROWIND's" — false; <c>Oblivion_default.ini:209</c> ships
    ///         <c>fTileTextureDivisor=4.7500</c> in its own [Water] section. The key is real and, if
    ///         live anywhere, acts in the engine's water-grid MESH UV generation (still unrecovered),
    ///         not between these two fetches.
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