using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Pins the 2026-08-17 Oblivion water rebuild against the regenerated permutation dumps
///     (tools/GhidraProject/oblivion_water_shaders, both decoders agreeing). The engine's RT-free
///     variants (WATER003 near / WATER013 LOD) compose reflection ADDITIVELY —
///     <c>body + Schlick · Reflectivity · lerp(body, ReflectionColor, 1 − Reflectivity)</c> — with
///     no sky term; the full <c>lerp(body, refl, F)</c> crossfade exists only in the planar-RT
///     variants, where <c>refl</c> is the projected scene. Porting that RT blend weight onto a
///     bright sky-gradient stand-in was the "huge reflections dominating at lakeside" defect: at
///     near-shore angles Schlick reaches 0.3–0.5 and half the surface crossfaded to sky.
/// </summary>
public sealed class OblivionWaterReflectionCompositionContractTests
{
    [Fact]
    public void ReflectionArmsAreEngineFaithful_RtFreeAdditiveAndWater007Projective()
    {
        var shader = SourceContract.ReadShaderSource("water_oblivion.frag.hlsl");

        // OFF arm — the WATER003/013 additive form, term for term (the no-RT truth).
        Assert.Contains(
            "color = body + F * reflectivity * lerp(body, uReflection.rgb, 1.0 - reflectivity);",
            shader, StringComparison.Ordinal);
        // ON arm — WATER007's projective sample: gated on a mirrored-SCENE target being bound,
        // the recovered wobble constants, and the full crossfade fed by the RT (never a sky term).
        Assert.Contains(
            "if (uWaterReflection.x != 0xFFFFFFFFu && uReflectionParams.x != 0u)",
            shader, StringComparison.Ordinal);
        Assert.Contains(
            "float wobble = saturate(distXY * 0.0002) * 2496.0 + 4.0;",
            shader, StringComparison.Ordinal);
        Assert.Contains(
            "float3 refl = lerp(uReflection.rgb, rt, reflectivity);",
            shader, StringComparison.Ordinal);
        Assert.Contains("color = lerp(body, refl, F);", shader, StringComparison.Ordinal);
        // The invented sky-gradient stand-in must not return in EITHER arm — the crossfade's
        // input is the mirrored scene RT or nothing.
        Assert.DoesNotContain("uSkyHorizon", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("uSkyTopSkyEnabled", shader, StringComparison.Ordinal);
        // WATER003/013 add the sun glint SATURATED (mad_sat) on the RT-free arm — ps_2_1 has no
        // HDR headroom there; WATER007's glint is a plain mad (unsaturated) on the RT arm.
        Assert.Contains(
            "color = saturate(color + sunSpec * sunCol * sunGate);",
            shader, StringComparison.Ordinal);
        // VarAmounts.z (WATR Opacity/100) stays an ALPHA floor and never re-enters the RGB blend.
        Assert.Contains(
            "float alphaFresnel = max(asfloat(uNoiseParams.w), saturate(F));",
            shader, StringComparison.Ordinal);
    }

    [Fact]
    public void SurfaceTileIsTheWater007WorldInterpolant()
    {
        // The machine-bound near permutation WATER007 samples its NormalMap at
        // t7.zw = worldXY · (3/4096) — a 4096/3 ≈ 1365.33-unit tile hard-wired in the water VS.
        // The ini fSurfaceTileSize=2048 feeds the mesh UV, which WATER007 hands to the
        // DisplacementMap (a sim input the viewer reproduces for no game).
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
        SourceContract.AssertOrder(renderer,
            "private float ResolveSurfaceUvScale(WaterSurfaceParams surface)",
            "WaterShaderVariant.OblivionWater000",
            "? 4096f / 3f");
    }

    [Fact]
    public void OblivionFftSobelFramesUploadAsOneLevelRuntimeTargets()
    {
        // Oblivion.exe 1.2.0.416 NiDX9RenderedTextureData::CreateSurf (0x00761730) calls
        // IDirect3DDevice9::CreateTexture through vtable slot 0x5C with Levels=1 (push 1 at
        // 0x007617DF). The synthesized CPU frames stand in for that dynamic one-level target;
        // box-filtered lower mips would erase frequencies before WATER007's implicit sample.
        var host = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "App", "Controls", "WorldView3D",
            "WorldView3DControl.Cells.cs");
        var resolver = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "TerrainTextureResolver12.cs");
        var cache = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuTextureCache12.cs");

        SourceContract.AssertOrder(host,
            "frames.Add(_textureResolver12.GetOrCreateSyntheticBindlessIndex(",
            "synthesized[i],",
            "generateMips: false));");
        Assert.Contains(
            "bool generateMips = true)", resolver, StringComparison.Ordinal);
        Assert.Contains(
            "_textureCache.GetOrCreateSynthetic(key, width, height, rgba, generateMips)",
            resolver, StringComparison.Ordinal);
        Assert.Contains(
            "bool generateMips = true)", cache, StringComparison.Ordinal);
        Assert.Contains(
            "_solidTextureFactory.CreateFromRgba(width, height, rgba, generateMips)",
            cache, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "_solidTextureFactory.CreateFromRgba(width, height, rgba, true)",
            cache, StringComparison.Ordinal);
    }

    [Fact]
    public void SunGateMatchesRecoveredTes4WriterAndWater007Saturation()
    {
        // Oblivion.exe's PC lighting writer stores FUN_00544b00() * 100 in SunDir.w
        // (tes4_sun_position_decompiled.txt 663-664), and WATER007 consumes c2.w through mov_sat
        // (oblivion_water_pkg013.asm 388/570). Our shared atmosphere CB stores the normalized
        // visibility instead, so the TES4 shader must apply both the recovered scale and clamp.
        var shader = SourceContract.ReadShaderSource("water_oblivion.frag.hlsl");

        Assert.Contains(
            "float sunGate = lit ? saturate(uSunDirIntensity.w * 100.0) : 1.0;",
            shader, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "float sunGate = lit ? max(uSunDirIntensity.w, 0.0) : 1.0;",
            shader, StringComparison.Ordinal);

        var writer = SourceContract.ReadSource("tools", "GhidraProject", "tes4_sun_position_decompiled.txt");
        Assert.Contains(
            "_DAT_00b45e00 = (float)(fVar11 * (float10)100.0);",
            writer, StringComparison.Ordinal);
        var water007 = SourceContract.ReadSource(
            "tools", "GhidraProject", "oblivion_water_shaders", "oblivion_water_pkg013.asm");
        Assert.Contains("mov_sat r0.w, c2/*SunDir*/.wwww", water007, StringComparison.Ordinal);
    }

    [Fact]
    public void OblivionWatrDataGateAdmitsEveryColorCarryingVintage()
    {
        // xEdit wbDefinitionsTES4 sizes 102/86/62/42/2 — every form from 42 bytes up carries the
        // three colors (the 42-byte vintage packs them at @28/32/36, NOT @44). The old ≥ 100 gate
        // dropped 6/23 shipped waters to FNV fallback tints (review finding #5), and a plain
        // truncated-prefix reading would still drop the 42-form and misread the 86-form's sims.
        var handler = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Esm", "Parsing", "Handlers",
            "MiscEnvironmentHandler.cs");
        Assert.Contains(
            "Context.Game == BethesdaGame.Oblivion && sub.DataLength >= 42",
            handler, StringComparison.Ordinal);
        // The size-versioned reader, not a prefix reader: the 42-form's color offsets…
        Assert.Contains("props[\"ShallowColor\"] = Color(d, 28);", handler, StringComparison.Ordinal);
        // …and the 86-form's three-float displacement block starting where the 102-form has RainDampener.
        Assert.Contains("props[\"DisplacementForce\"] = ReadFloat(d, 72, isBigEndian);", handler,
            StringComparison.Ordinal);
    }
}
