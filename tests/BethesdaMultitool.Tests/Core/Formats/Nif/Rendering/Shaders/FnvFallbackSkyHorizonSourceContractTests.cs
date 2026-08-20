using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Pins the FO3/FNV fallback sky-dome horizon fix (fog/skybox horizon seam). Retail does NOT fog
///     the sky — SKY.pso is vertexColor * Params.y with no _F fog variant in the sky family — so the
///     seam is about WHICH color the dome's horizon uses. Atmosphere::Update writes
///     SkyShader::pBlendColor rows from SkyColor[8] (NAM0 Horizon), SkyColor[7] (SkyLower),
///     SkyColor[0] (SkyUpper) UNCONDITIONALLY — no sun-elevation gate on row 0
///     (tools/GhidraProject/atmosphere_decompiled.txt, asm 0x8246884C-0x82468924: stores from weather
///     offsets 0x9C/0x90/0x3C into the pBlendColor block). The old 2-row fallback used the CPU-shaped
///     HorizonGlow horizon, which collapses to saturated SkyLower at noon (HorizonGlow == 0), so the
///     dome met the fogged terrain with the wrong row. Only FO3/FNV's row mapping is
///     decompile-grounded, so every other game must keep the 2-row math bit-identical.
/// </summary>
public sealed class FnvFallbackSkyHorizonSourceContractTests
{
    private const string ShaderFile = "sky_geo.frag.hlsl";

    private static string ReadRendererSource()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "SkyGeometryRenderer12.cs");
    }

    [Fact]
    public void FallbackDome_Fo3Fnv_UsesThreeRowRampOverTheAtmosphereUpdateRows()
    {
        var shader = SourceContract.ReadShaderSource(ShaderFile);

        // The gate travels in the CB's uTexIndex.y lane and selects the 3-row ramp.
        Assert.Contains("if (uTexIndex.y == 1)", shader, StringComparison.Ordinal);

        // Row 0 (NAM0 Horizon) at dir.z == 0, blending to row 1 (SkyLower) across the low band,
        // then to row 2 (SkyUpper) at zenith — the same rows Atmosphere::Update feeds pBlendColor.
        Assert.Contains(
            "const float kLowerBandZ = 0.2079;",
            shader, StringComparison.Ordinal);
        Assert.Contains(
            "float3 band = lerp(uSkyHorizon.rgb, uSkyLower.rgb, saturate(z / kLowerBandZ));",
            shader, StringComparison.Ordinal);
        Assert.Contains(
            "float3 ramp = lerp(band, uSkyUpper.rgb, saturate((z - kLowerBandZ) / (1.0 - kLowerBandZ)));",
            shader, StringComparison.Ordinal);

        // The decompile citation must stay attached to the ramp so the row mapping keeps its source.
        Assert.Contains("0x8246884C-0x82468924", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void FallbackDome_OtherGames_KeepTheTwoRowMathBitIdentical()
    {
        var shader = SourceContract.ReadShaderSource(ShaderFile);

        // The pre-ramp expression, verbatim. Any drift here silently changes MW/TES4/Skyrim/FO4
        // fallback skies, whose BlendColor row mapping has no decompile grounding.
        Assert.Contains(
            "float3 sky = lerp(uTintParam.rgb, uSkyUpper.rgb, saturate(dir.z));",
            shader, StringComparison.Ordinal);
        Assert.Contains("return float4(sky, 1.0);", shader, StringComparison.Ordinal);

        // The 2-row math must be the unconditional tail of the mode-0 fallback: the gated 3-row
        // block comes first and RETURNS, so every game that is not flagged falls through to it.
        SourceContract.AssertOrder(shader,
            "if (uTexIndex.y == 1)",
            "return float4(ramp, 1.0);",
            "float3 sky = lerp(uTintParam.rgb, uSkyUpper.rgb, saturate(dir.z));");
    }

    [Fact]
    public void Renderer_GatesTheRampToFallout3AndNewVegasOnly()
    {
        var renderer = ReadRendererSource();

        // Explicit per-game check (no family enum), feeding the CB's uTexIndex.y lane.
        Assert.Contains(
            "var fnvFallbackHorizonRamp = game is BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas;",
            renderer, StringComparison.Ordinal);
        Assert.Contains(
            "FallbackRampFlag = fnvFallbackHorizonRamp ? 1u : 0u,",
            renderer, StringComparison.Ordinal);
        // The flag rides the formerly padded uint4.y lane, so the CB layout (176 bytes) is unchanged.
        Assert.Contains(
            "public uint FallbackRampFlag; // uint4.y",
            renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void CaptureParityLine_CarriesTheFogLane()
    {
        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");

        // The seam magnitude must be a number in the log: fog color/distances/power plus the powered
        // amount the terrain reaches at the fog far plane (mirror of fog.hlsli FogAmountAtDistance).
        Assert.Contains(
            "fogColor={26} fogNear={27} fogFar={28} fogPower={29} fogAtFar={30}",
            capture, StringComparison.Ordinal);
        Assert.Contains(
            "MathF.Pow(fogQAtFar, MathF.Max(atmo.FogPower, 0.01f))",
            capture, StringComparison.Ordinal);
        Assert.Contains(
            "Math.Clamp(atmo.FogMaxOpacity, 0f, 1f)",
            capture, StringComparison.Ordinal);
    }
}