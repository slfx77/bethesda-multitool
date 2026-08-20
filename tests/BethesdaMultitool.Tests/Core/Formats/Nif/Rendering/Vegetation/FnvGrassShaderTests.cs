using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     Per-game shader #2, and the first on the INSTANCED axis. Retail FO3/FNV does not light grass
///     from the blade card at all: <c>TESTerrainLODManager::CreateGrass</c> bakes the land normal
///     and the land colour's luminance into each instance, and <c>GRASS2002.vso</c> lights from
///     those with an additive, vertex-colour-free ambient term plus a 1.5x sun boost. Its pixel
///     shader attenuates only the SUN term by the shadow map. None of that is expressible as a
///     uniform on the shared reference path.
/// </summary>
public sealed class FnvGrassShaderTests
{
    private static string VertexShader()
    {
        return SourceContract.ReadShaderSource("reference_grass_fnv.vert.hlsl");
    }

    private static string PixelShader()
    {
        return SourceContract.ReadShaderSource("reference_grass_fnv.frag.hlsl");
    }

    [Theory]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Fallout3)]
    public void FalloutClassicGamesSelectTheInstancedGrassPair(BethesdaGame game)
    {
        var profile = GrassShaderProfile.InstancedForGame(game);

        Assert.True(profile.Enabled);
        Assert.Equal("reference_grass_fnv.vert.hlsl", profile.VertexShaderName);
        Assert.Equal("reference_grass_fnv.frag.hlsl", profile.PixelShaderName);
    }

    [Theory]
    [InlineData(BethesdaGame.Unknown)]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void EveryOtherGameHasNoInstancedGrassPair(BethesdaGame game)
    {
        var profile = GrassShaderProfile.InstancedForGame(game);

        Assert.False(profile.Enabled);
        Assert.Null(profile.VertexShaderName);
        Assert.Null(profile.PixelShaderName);
    }

    [Fact]
    public void TheTwoGrassAxesStayDistinct()
    {
        // The instanced pair takes SV_InstanceID plus the t8 world-matrix buffer; handing it to the
        // blended per-draw route would bind a vertex shader whose ABI that route cannot satisfy.
        Assert.False(GrassShaderProfile.ForGame(BethesdaGame.FalloutNewVegas).Enabled);
        Assert.False(GrassShaderProfile.InstancedForGame(BethesdaGame.Oblivion).Enabled);
    }

    [Theory]
    // frac = clamp((ColorRange * 0.5 * signedRandom + (1 - ColorRange)) * landLuminance, 0, 0.99).
    // ColorRange 0 removes the variance entirely: the payload is the land luminance, and a fully
    // lit vertex saturates to the 0.99 ceiling that keeps floor(W) recoverable.
    [InlineData(0.5f, 0f, 1f, 0.99f)]
    [InlineData(0.5f, 0f, 0.5f, 0.5f)]
    // Random 1 -> signedRandom +1: variance = 1 - ColorRange/2. Random 0 -> 1 - 1.5 * ColorRange.
    [InlineData(1f, 0.5f, 1f, 0.75f)]
    [InlineData(0f, 0.5f, 1f, 0.25f)]
    [InlineData(0.5f, 0.1f, 0.5f, 0.45f)]
    // Black terrain paints black grass regardless of the variance draw.
    [InlineData(1f, 0.5f, 0f, 0f)]
    // An extreme ColorRange can drive the variance negative; the engine floors it at zero.
    [InlineData(0f, 1f, 1f, 0f)]
    public void ComputeBakedLightReplaysTheAddGrassFraction(
        float random01,
        float colorRange,
        float landLuminance,
        float expected)
    {
        var actual = FnvGrassLighting.ComputeBakedLight(random01, colorRange, landLuminance);

        Assert.Equal(expected, actual, 5);
    }

    [Fact]
    public void BakedLightNeverEscapesTheRangeTheShaderCanDecode()
    {
        // The vertex shader recovers the payload with frac(), so anything at or above 1.0 would
        // wrap to darkness instead of saturating to full brightness.
        for (var i = 0; i <= 10; i++)
        {
            var light = FnvGrassLighting.ComputeBakedLight(i / 10f, 0.5f, 1f);
            Assert.InRange(light, 0f, FnvGrassLighting.BakedLightMaximum);
        }
    }

    [Fact]
    public void LandLuminanceDefaultsToWhiteWhenTheCellAuthorsNoVertexColors()
    {
        // CreateGrass pre-fills the colour with 1.0 and only overwrites it when the land query
        // succeeds, so a cell without VCLR lights its grass at full terrain brightness.
        Assert.Equal(1f, FnvGrassLighting.SampleLandLuminance(null, 512f, 512f, 128f), 5);
    }

    [Fact]
    public void LandLuminanceUsesTheAuthoredWeightsAtAVertex()
    {
        var vclr = WhiteVertexColors();
        // Paint one vertex a pure primary and sample exactly on it: the result is that channel's
        // authored weight (0.31 / 0.37 / 0.32 — deliberately not Rec.601).
        SetVertex(vclr, 4, 8, 255, 0, 0);

        var luminance = FnvGrassLighting.SampleLandLuminance(vclr, 4 * 128f, 8 * 128f, 128f);

        Assert.Equal(FnvGrassLighting.LuminanceRed, luminance, 4);
        Assert.Equal(1f, FnvGrassLighting.LuminanceRed + FnvGrassLighting.LuminanceGreen
                                                       + FnvGrassLighting.LuminanceBlue, 5);
    }

    [Fact]
    public void LandLuminanceIndexesRowsByNorthingWithoutTheLayerTableFlip()
    {
        // The row flip in PopulateTextureWeights belongs to CellLayerWeightTable alone; the vertex
        // arrays are indexed j * 33 + i directly, like the heightmap. Painting (i=1, j=2) black and
        // reading there must darken, while the transposed position stays white.
        var vclr = WhiteVertexColors();
        SetVertex(vclr, 1, 2, 0, 0, 0);

        Assert.Equal(0f, FnvGrassLighting.SampleLandLuminance(vclr, 1 * 128f, 2 * 128f, 128f), 4);
        Assert.Equal(1f, FnvGrassLighting.SampleLandLuminance(vclr, 2 * 128f, 1 * 128f, 128f), 4);
    }

    [Fact]
    public void LandLuminanceInterpolatesAcrossTheTriangleRatherThanSnappingToAVertex()
    {
        // TESObjectLAND::GetLandColor blends the three triangle corners by the in-quad fractions.
        // A nearest-vertex approximation would return a flat 1.0 until the midpoint flipped it to
        // 0.0; barycentric interpolation must land strictly between the two.
        var vclr = WhiteVertexColors();
        SetVertex(vclr, 0, 0, 0, 0, 0);

        var quarter = FnvGrassLighting.SampleLandLuminance(vclr, 32f, 32f, 128f);

        Assert.InRange(quarter, 0.01f, 0.99f);
        Assert.True(
            quarter < FnvGrassLighting.SampleLandLuminance(vclr, 96f, 96f, 128f),
            "luminance must rise with distance from the darkened corner");
    }

    [Fact]
    public void LandNormalDecodesSignedBytesAndFallsBackWhenAbsent()
    {
        Assert.Null(FnvGrassLighting.SampleLandNormal(null, 128f, 128f, 128f));

        var vnml = new byte[33 * 33 * 3];
        for (var i = 0; i < 33 * 33; i++)
        {
            vnml[i * 3] = unchecked(0);
            vnml[i * 3 + 1] = unchecked(0);
            vnml[i * 3 + 2] = 127;
        }

        var normal = FnvGrassLighting.SampleLandNormal(vnml, 256f, 256f, 128f);

        Assert.NotNull(normal);
        Assert.Equal(Vector3.UnitZ, normal!.Value, EqualityComparer<Vector3>.Default);
    }

    [Fact]
    public void VertexShaderImplementsTheRecoveredLightingLanes()
    {
        var source = VertexShader();

        // L = 0.25 + 0.75 * frac(W), read out of the instance matrix's w-lanes.
        Assert.Contains("float4 payload = world[3];", source, StringComparison.Ordinal);
        Assert.Contains("LightBaseConstant + LightRangeConstant * frac(payload.w)", source,
            StringComparison.Ordinal);
        Assert.Contains("static const float LightBaseConstant = 0.25;", source, StringComparison.Ordinal);
        Assert.Contains("static const float LightRangeConstant = 0.75;", source, StringComparison.Ordinal);
        // The sun multiplier is AddlParams.x (c7.x) — a CPU-driven constant the engine fills from
        // the imagespace GrassDimmer, NOT a shader literal. The shipped GRASS2002.vso defs are
        // c8 = (0.75, 0.25, 0.0078125, 0) and it applies the sun term as mul oT3.xyz, r1, c7.x.
        Assert.Contains("static const float SunBoostFallback = 1.3;", source, StringComparison.Ordinal);
        Assert.Contains("grassDimmer * bakedLight * lambert", source, StringComparison.Ordinal);
        // Grass takes the sun colour WITHOUT the imagespace SunlightDimmer that ordinary geometry
        // receives through BSShaderLightingProperty; uSunColorLighting has it pre-multiplied.
        Assert.Contains("uGrassSunColorScale.rgb", source, StringComparison.Ordinal);
        // The sun term lights from the BAKED TERRAIN normal, never the card's own normal.
        Assert.Contains("saturate(dot(uSunDirIntensity.xyz, terrainNormal))", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("input.aVertexColor.rgb * uSunColorLighting.rgb", source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("input.aNormal", source, StringComparison.Ordinal);
        // Ambient carries the baked light alone — no vertex colour, unlike the shared path — with
        // the shared path's SDR componentwise clamp outside HDR.
        Assert.Contains("bakedLight * uAmbientColor.rgb", source, StringComparison.Ordinal);
        Assert.Contains("min(bakedLight * uAmbientColor.rgb, 1.0)", source, StringComparison.Ordinal);
        var ambientLine = source[source.IndexOf("o.vGrassAmbient =", StringComparison.Ordinal)..];
        Assert.DoesNotContain(
            "aVertexColor",
            ambientLine[..ambientLine.IndexOf(';', StringComparison.Ordinal)],
            StringComparison.Ordinal);
        // The engine's grass permutation has no ambient cube and no point lights.
        Assert.DoesNotContain("uAmbientPositiveX", source, StringComparison.Ordinal);
        // Instanced ABI + the payload-safe transform.
        Assert.Contains("StructuredBuffer<float4x4> uInstanceWorlds : register(t8);", source,
            StringComparison.Ordinal);
        Assert.Contains("uint instanceId : SV_InstanceID", source, StringComparison.Ordinal);
        Assert.Contains("worldPos.w = 1.0;", source, StringComparison.Ordinal);
        // Wind must survive the reroute or grass stops swaying on this path.
        Assert.Contains("tallGrassWindWeight * tallGrassWindWeight", source, StringComparison.Ordinal);
        Assert.Contains("uCameraOrigin.xy", source, StringComparison.Ordinal);
        Assert.Contains("uSunColorLighting.w < 0.5", source, StringComparison.Ordinal);
    }

    [Fact]
    public void PixelShaderShadowsOnlyTheSunTerm()
    {
        var source = PixelShader();

        // GRASS2002.pso: lit = sun * shadowFactor + ambient. Shadowed grass settles on its ambient
        // floor instead of going black, which is what the shared shader gets wrong.
        Assert.Contains("input.vGrassSun * sunShadow + input.vGrassAmbient", source,
            StringComparison.Ordinal);
        Assert.Contains("ShadowFactor(input.vWorldPos)", source, StringComparison.Ordinal);
        // NO per-sample ceiling: GRASS2002.pso has no saturate, and the terrain beside it
        // (terrain_textured.frag.hlsl) writes the same float target unclamped — clamping only grass
        // would whiten it away from retail's warm midday value.
        Assert.DoesNotContain("min(lit, 1.0)", source, StringComparison.Ordinal);
        // Alpha handling is deliberately unchanged from the shared path (lighting-only change).
        Assert.Contains("ALPHA_TO_COVERAGE", source, StringComparison.Ordinal);
        Assert.Contains("PassAlphaTest(testAlpha, input.vAlphaState.x, input.vAlphaState.y)", source,
            StringComparison.Ordinal);
        // The distance envelope stays CPU-side (GrassDistanceCullPolicy's hard end).
        Assert.DoesNotContain("distanceEnvelope", source, StringComparison.Ordinal);
    }

    [Fact]
    public void SharedVertexShadersRestoreTheAffineWAfterTransforming()
    {
        // The payload occupies the lanes that feed worldPos.w, so every shader that transforms an
        // instance matrix must restore it — the instanced VS at both of its transform sites, and
        // the per-draw VS for a blend-property grass NIF.
        var instanced = SourceContract.ReadShaderSource("reference_instanced.vert.hlsl");
        var perDraw = SourceContract.ReadShaderSource("reference.vert.hlsl");

        Assert.Equal(2, SourceContract.CountOccurrences(instanced, "worldPos.w = 1.0;"));
        Assert.Equal(1, SourceContract.CountOccurrences(perDraw, "worldPos.w = 1.0;"));
    }

    [Fact]
    public void RendererRoutesBothGrassDrawSitesThroughTheSameSeam()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var factory = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferencePipelineFactory12.cs");

        // The main pass and the shadow-only caster ring must agree, or the same grass submesh
        // splits into two batches (the PSO is part of the batch key).
        Assert.Equal(2, SourceContract.CountOccurrences(renderer, "_pipelines.GetGrassCutoutPso(sub.DoubleSided)"));
        Assert.Contains("SetInstancedGrassShaderProfile(", renderer, StringComparison.Ordinal);

        // Single-sampled scenes alias A2C onto the plain grass PSOs, so the per-game shader is not
        // silently dropped when MSAA is off.
        Assert.Contains("_grassOpaqueBackA2CPso = _grassOpaqueBackPso;", factory, StringComparison.Ordinal);
        Assert.Contains("_grassOpaqueDoubleA2CPso = _grassOpaqueDoublePso;", factory, StringComparison.Ordinal);
        // Fail-soft: a compile failure keeps the shared pipelines rather than throwing.
        Assert.Contains("return doubleSided ? OpaqueDoubleA2CPso : OpaqueBackA2CPso;", factory,
            StringComparison.Ordinal);
    }

    private static byte[] WhiteVertexColors()
    {
        var vclr = new byte[33 * 33 * 3];
        Array.Fill(vclr, (byte)255);
        return vclr;
    }

    private static void SetVertex(byte[] vclr, int i, int j, byte r, byte g, byte b)
    {
        var offset = (j * 33 + i) * 3;
        vclr[offset] = r;
        vclr[offset + 1] = g;
        vclr[offset + 2] = b;
    }
}