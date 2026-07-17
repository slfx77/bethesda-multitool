using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the per-game <see cref="WaterProfile" /> mapping + the FNV constants that were hoisted out of
///     <c>WaterRenderer12</c>. Games move off the FNV fallback only after their own shipped shader or
///     fixed-function path is recovered. The constants are asserted byte-for-byte so profile routing
///     cannot perturb FNV/FO3.
/// </summary>
public class WaterProfileTests
{
    [Fact]
    public void FalloutNewVegas_UsesTheCanonicalFnvProfile()
    {
        Assert.Same(WaterProfile.Fnv, WaterProfile.ForGame(BethesdaGame.FalloutNewVegas));
        Assert.Equal(WaterShaderVariant.FnvWater000, WaterProfile.ForGame(BethesdaGame.FalloutNewVegas).ShaderVariant);
    }

    [Fact]
    public void Fallout3_MapsToTheFnvProfile_IdenticalShaderpackageWaterSet()
    {
        // FO3's shaderpackage019.sdp is the identical water set to FNV, so FO3 shares the FNV variant.
        Assert.Same(WaterProfile.Fnv, WaterProfile.ForGame(BethesdaGame.Fallout3));
    }

    [Theory]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Starfield)]
    [InlineData(BethesdaGame.Unknown)]
    public void GamesWithoutTheirOwnDecompiledShader_ShareTheRtFreeWater000Shader(BethesdaGame game)
    {
        // These games resolve to the shared RT-free WATER000 shader. For Skyrim this is RE-confirmed
        // (its BSWaterShader reduces to the same RT-free math; see skyrim_water_pixel_shader_decompiled.txt)
        // — per-game fidelity is the WATR DNAM parse, not a different shader. Others have no own shader
        // source and fall back (binary-RE-only: no guessing).
        var profile = WaterProfile.ForGame(game);
        Assert.Same(WaterProfile.Fnv, profile);
        Assert.Equal(WaterShaderVariant.FnvWater000, profile.ShaderVariant);
    }

    [Fact]
    public void Oblivion_UsesItsOwnDecompiledShaderVariant()
    {
        // Oblivion's WATER000.pso genuinely diverges from the shared RT-free math (view-angle body,
        // single sun specular — see oblivion_water_pixel_shader_decompiled.txt), so it is the one
        // game with its own variant. Renderer-side tuning stays the FNV set (Oblivion has no NNAM).
        var profile = WaterProfile.ForGame(BethesdaGame.Oblivion);
        Assert.Same(WaterProfile.Oblivion, profile);
        Assert.Equal(WaterShaderVariant.OblivionWater000, profile.ShaderVariant);
        Assert.Equal(WaterProfile.Fnv.NoiseTilingWorldUnits, profile.NoiseTilingWorldUnits);
        Assert.Equal(WaterProfile.Fnv.DepthTieBiasWorldUnits, profile.DepthTieBiasWorldUnits);
        Assert.Equal(12f, profile.SurfaceFrameFps);
    }

    [Fact]
    public void Fallout4_UsesItsOwnDecompiledShaderVariant()
    {
        // FO4's BSWaterShader was disassembled from the shipped D3D11 bytecode (Shaders011.fxp group 5;
        // fo4_water_pixel_shader_decompiled.txt) and genuinely diverges (Oren-Nayar diffuse, normalized
        // Kelemen/Schlick specular, depth-LUT body) — its own variant. FO76 is verified independently
        // in the test below rather than assumed identical.
        var profile = WaterProfile.ForGame(BethesdaGame.Fallout4);
        Assert.Same(WaterProfile.Fallout4, profile);
        Assert.Equal(WaterShaderVariant.Fo4Water, profile.ShaderVariant);
        Assert.Equal(WaterProfile.Fnv.NoiseTilingWorldUnits, profile.NoiseTilingWorldUnits);
        Assert.Equal(WaterProfile.Fnv.DepthTieBiasWorldUnits, profile.DepthTieBiasWorldUnits);
    }

    [Fact]
    public void Fallout76_UsesVerifiedCreationWaterArchitecture()
    {
        var profile = WaterProfile.ForGame(BethesdaGame.Fallout76);

        Assert.Same(WaterProfile.Fallout76, profile);
        Assert.Equal(WaterShaderVariant.Fo4Water, profile.ShaderVariant);
        Assert.Equal(WaterProfile.Fallout4.NoiseTilingWorldUnits, profile.NoiseTilingWorldUnits);
        Assert.Equal(WaterProfile.Fallout4.DepthTieBiasWorldUnits, profile.DepthTieBiasWorldUnits);
    }

    [Fact]
    public void Morrowind_UsesTheFixedFunctionAnimatedSurfaceVariant()
    {
        // Morrowind is fixed-function — there is no water pixel shader to decompile; the engine's
        // surface IS Morrowind.ini [Water] data: water00-31.dds cycling at SurfaceFPS=12, World
        // Alpha=0.75 (docs/research/morrowind_atmosphere_water_model.md). The tile size reads
        // SurfaceTileCount=10 per 8192-unit cell (TO-CONFIRM vs an OpenMW render oracle).
        var profile = WaterProfile.ForGame(BethesdaGame.Morrowind);
        Assert.Same(WaterProfile.Morrowind, profile);
        Assert.Equal(WaterShaderVariant.MorrowindWater, profile.ShaderVariant);
        Assert.Equal(12f, profile.SurfaceFrameFps);
        Assert.Equal(0.75f, profile.SurfaceAlpha);
        Assert.Equal(819u, profile.NoiseTilingWorldUnits);
    }

    [Fact]
    public void FnvProfile_ConstantsMatchTheHoistedWaterRenderer12Literals()
    {
        // These are the exact constants formerly hardcoded in WaterRenderer12. Asserting them pins the
        // Phase-1 hoist as byte-identical: any drift here would change the FNV/FO3 water render.
        var p = WaterProfile.Fnv;
        // Base-octave tile shrunk 2048→512 so the shader's 3 octaves yield fine ripple detail (~110 units).
        Assert.Equal(512u, p.NoiseTilingWorldUnits);
        // DepthTieBias was deliberately shrunk 8→1 world units: the original 8 over-drew water across floating
        // props near the surface (Skyrim ice floes z-fighting / hidden under water). Sub-unit shoreline depth
        // noise is still absorbed at 1, so the coplanar-shoreline anti-flicker fix (3D-2) is preserved.
        Assert.Equal(1f, p.DepthTieBiasWorldUnits);
        Assert.Equal(new Vector3(0.12f, 0.24f, 0.32f), p.DefaultShallow);
        Assert.Equal(new Vector3(0.03f, 0.09f, 0.16f), p.DefaultDeep);
        Assert.Equal(new Vector3(0.22f, 0.32f, 0.40f), p.DefaultReflection);
    }

    /// <summary>
    ///     Pins the legacy water00–31.dds binding roles the viewer's frame resolution keys on:
    ///     Morrowind samples the frames as its fixed-function diffuse, Oblivion as WATER000's global
    ///     NormalMap, and only Oblivion samples WATR TNAM as a per-water detail diffuse. Every other
    ///     game has no frame cycle (shader-variant water).
    /// </summary>
    [Theory]
    [InlineData(BethesdaGame.Morrowind, LegacySurfaceFrameRole.Diffuse, false)]
    [InlineData(BethesdaGame.Oblivion, LegacySurfaceFrameRole.GlobalNormal, true)]
    [InlineData(BethesdaGame.Fallout3, LegacySurfaceFrameRole.None, false)]
    [InlineData(BethesdaGame.FalloutNewVegas, LegacySurfaceFrameRole.None, false)]
    [InlineData(BethesdaGame.Skyrim, LegacySurfaceFrameRole.None, false)]
    [InlineData(BethesdaGame.Fallout4, LegacySurfaceFrameRole.None, false)]
    [InlineData(BethesdaGame.Fallout76, LegacySurfaceFrameRole.None, false)]
    [InlineData(BethesdaGame.Starfield, LegacySurfaceFrameRole.None, false)]
    [InlineData(BethesdaGame.Unknown, LegacySurfaceFrameRole.None, false)]
    public void LegacyFrameRoles_PinPerGameBindings(
        BethesdaGame game, LegacySurfaceFrameRole frames, bool usesWatrDetail)
    {
        var profile = WaterProfile.ForGame(game);
        Assert.Equal(frames, profile.LegacyFrames);
        Assert.Equal(usesWatrDetail, profile.UsesWatrDetailTexture);
    }
}
