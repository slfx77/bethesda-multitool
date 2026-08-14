using BethesdaMultitool.Core.Formats.Nif.Rendering.Abstractions;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     The first per-game shader. Retail Oblivion lights grass from the TERRAIN normal with no
///     surface normal anywhere, and composes ambient ADDITIVELY (<c>lit = oT5 + oT4</c>), whereas the
///     shared reference path multiplies through the blade card's own near-vertical normal — which
///     collapses at high sun and produced the reported "grass too dark".
/// </summary>
public sealed class Tes4GrassShaderTests
{
    private static string VertexShader() => SourceContract.ReadShaderSource("reference_grass_oblivion.vert.hlsl");

    private static string PixelShader() => SourceContract.ReadShaderSource("reference_grass_oblivion.frag.hlsl");

    [Fact]
    public void OnlyOblivionSelectsAPerGameGrassShader()
    {
        var oblivion = GrassShaderProfile.ForGame(BethesdaGame.Oblivion);

        Assert.True(oblivion.Enabled);
        Assert.Equal("reference_grass_oblivion.vert.hlsl", oblivion.VertexShaderName);
        Assert.Equal("reference_grass_oblivion.frag.hlsl", oblivion.PixelShaderName);
    }

    [Theory]
    // Unknown is what the headless NIF renderer passes (a default WorldRenderCache), so it is the
    // fallback that must keep working, not a theoretical case.
    [InlineData(BethesdaGame.Unknown)]
    [InlineData(BethesdaGame.Morrowind)]
    // FO3/FNV have a recovered pair, but on the INSTANCED axis (GrassShaderProfile.InstancedForGame
    // — see FnvGrassShaderTests): their grass draws through the opaque cutout route, so the blended
    // per-draw route this method covers still falls back for them.
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Fallout76)]
    public void EveryOtherGameFallsBackToTheSharedShaders(BethesdaGame game)
    {
        var profile = GrassShaderProfile.ForGame(game);

        Assert.False(profile.Enabled);
        Assert.Null(profile.VertexShaderName);
        Assert.Null(profile.PixelShaderName);
    }

    [Fact]
    public void VertexShaderLightsFromThePlacementNormalAndNeverFromASurfaceNormal()
    {
        var source = VertexShader();

        // The world matrix's up axis IS the terrain normal (GrassPlacementBuilder composes
        // up = fitToSlope ? terrainNormal : UnitZ). CPU System.Numerics rows become HLSL columns
        // here, so the up axis is column 2 — the same convention ClassicSpecularLodFade relies on.
        Assert.Contains("float3(world[0].z, world[1].z, world[2].z)", source, StringComparison.Ordinal);
        Assert.Contains("saturate(dot(uSunDirIntensity.xyz, placementNormal))", source, StringComparison.Ordinal);

        // The absence of a surface normal is the entire fix, so assert it directly: consuming
        // aNormal here would reintroduce the vertical-card collapse this shader exists to remove.
        Assert.DoesNotContain("input.aNormal", source, StringComparison.Ordinal);
        Assert.DoesNotContain("vWorldNormal", source, StringComparison.Ordinal);
    }

    [Fact]
    public void VertexShaderReproducesTheEnginePackingCeiling()
    {
        // CreateGrass stores each normal component as min((N + 1) / 2, 0.97), so retail's decode can
        // never exceed 0.94 — a flat-ground tuft lights at 0.94, not 1.0. Dropping this would make
        // our grass ~6% brighter than retail on level ground.
        Assert.Contains("return min(normal, 0.94);", VertexShader(), StringComparison.Ordinal);
    }

    [Fact]
    public void VertexShaderHonorsTheLightingOffToggle()
    {
        // Without this arm the toolbar's lighting toggle would silently stop applying to grass the
        // moment it moved onto its own shader.
        Assert.Contains("uSunColorLighting.w < 0.5", VertexShader(), StringComparison.Ordinal);
    }

    [Fact]
    public void PixelShaderAddsAmbientToDiffuseRatherThanMultiplying()
    {
        // GRASS2002's `add r1.xyz, r1, a4`. This is the divergence no uniform can express: the
        // shared path computes sample.rgb * shade * vertexRgb, retail computes tex * (diffuse +
        // ambient). Multiplying here would silently restore the original defect.
        Assert.Contains(
            "sample.rgb * (input.vGrassDiffuse + input.vGrassAmbient)",
            PixelShader(),
            StringComparison.Ordinal);
    }

    [Fact]
    public void PixelShaderTestsRawTextureAlphaBecauseVertexAlphaIsWindData()
    {
        var source = PixelShader();

        // Oblivion grass authors vertex alpha as a WIND-SWAY WEIGHT (0.0 at every ground vertex,
        // 0.37-0.65 at the tips). Testing against it would discard the bottom of every blade and
        // leave the tufts floating.
        Assert.Contains(
            "PassAlphaTest(sample.a, input.vAlphaState.x, input.vAlphaState.y)",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("vVertexColor.a", source, StringComparison.Ordinal);
    }

    [Fact]
    public void BothBlendSitesRouteGrassSoTheCarpetCannotBeHalfLit()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var compact = new string(renderer.Where(c => !char.IsWhiteSpace(c)).ToArray());

        // Oblivion grass authors 0x12ED (blend AND test) and BuildAlphaState keeps the test live for
        // a depth-writing blend, so grass reaches BOTH lists. Routing only one would light half the
        // carpet with the shared shader — the same both-sites lesson the A2C work already learned.
        // Three sites since the unified transparency stream: the legacy hoisted depth-writing loop,
        // and BOTH arms of the stream's per-draw PSO choice inside DrawBlended.
        // ⚠ Since 2026-08-13 the BULK of TES4 grass no longer reaches these lists at all — it batches
        // through the instanced+blended route (Tes4GrassInstancingTests). These three sites now serve
        // the residual the instanced ABI cannot express (billboarded submeshes, NiAlphaController
        // material alpha), which is exactly why they must keep routing grass: that residual is a
        // handful of draws sharing a carpet with thousands of batched ones, and lighting it through
        // the shared shader would be the same half-lit carpet in miniature.
        Assert.Contains("_pipelines.GetBlendPipeline(", compact, StringComparison.Ordinal);
        Assert.Contains("_pipelines.GetBlendDepthWritePipeline(", compact, StringComparison.Ordinal);
        Assert.Equal(3, SourceContract.CountOccurrences(compact, "grassRoute:draw.IsGrass"));
    }

    [Fact]
    public void GrassPsoCachesAreSeparateFromTheSharedOnes()
    {
        var factory = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferencePipelineFactory12.cs");
        var compact = new string(factory.Where(c => !char.IsWhiteSpace(c)).ToArray());

        // The blend keys are identical between routes — only the shaders differ — so every route must
        // be a DISTINCT ShaderRoutePsos instance owning its own PSO caches: one shared cache would
        // hand a grass PSO to non-grass geometry (and vice versa) after the first draw. THREE routes
        // since 2026-08-13, because the instanced grass VS reads a different b1 layout than the
        // per-draw one and so cannot share either of the others' pipelines.
        Assert.Contains("privatesealedclassShaderRoutePsos", compact, StringComparison.Ordinal);
        Assert.Contains("readonlyShaderRoutePsos_sharedRoute;", compact, StringComparison.Ordinal);
        Assert.Contains("readonlyShaderRoutePsos_grassRoute=new();", compact, StringComparison.Ordinal);
        Assert.Contains("readonlyShaderRoutePsos_instancedGrassBlendRoute=new();", compact,
            StringComparison.Ordinal);

        // Both blend getters must pick the route the same way. This used to be checked by counting
        // two IDENTICAL inline ternaries; they are now one shared selector, which enforces the same
        // property by construction instead of by duplication — so assert that BOTH getters go
        // through it, and that each arm still falls back to the shared route rather than throwing.
        Assert.Equal(2, SourceContract.CountOccurrences(
            compact, "SelectBlendRoute(grassRoute,instancedGrass);"));
        Assert.Equal(1, SourceContract.CountOccurrences(compact, "?_grassRoute:_sharedRoute;"));
        Assert.Equal(1, SourceContract.CountOccurrences(
            compact, "?_instancedGrassBlendRoute:_sharedRoute;"));

        // Compile failure must degrade to the shared shaders, never throw: the caller's catch would
        // otherwise take down the whole reference pipeline (every placed object) over one game's
        // grass. TryCompile is the fail-soft seam (logs + returns null), and the route resets its
        // stored profile so a later Set of the same pair retries instead of no-oping.
        Assert.Contains("profile.TryCompile(consumerName)", factory, StringComparison.Ordinal);
        Assert.Contains("_profile = default;", factory, StringComparison.Ordinal);
        var pair = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Abstractions",
            "GameShaderPair.cs");
        Assert.Contains(
            "catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException)",
            pair,
            StringComparison.Ordinal);
        Assert.Contains("return null;", pair, StringComparison.Ordinal);
    }
}
