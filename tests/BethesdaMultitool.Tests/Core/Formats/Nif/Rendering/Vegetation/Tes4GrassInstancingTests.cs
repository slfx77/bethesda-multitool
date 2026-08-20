using BethesdaMultitool.Core.Formats.Nif.Rendering.Vegetation;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Vegetation;

/// <summary>
///     TES4 grass batches instead of drawing one call per blade.
///     <para>
///         The whole difference between Oblivion and FNV grass is one authored NiAlphaProperty bit —
///         0x12ED (blend AND test) versus 0x12EC (test only) — which NifAlphaClassifier turns
///         directly into <c>NifAlphaRenderMode.Blend</c> with no policy layer. That pinned an entire
///         ~3,900-blade carpet to the per-draw blended path, where each blade costs a draw call, a
///         256-byte ring allocation, and three whole-list re-scans per frame. The fix compiles the
///         same recovered shader against the instanced ABI rather than reclassifying the alpha, so
///         the July 2026 A2C revert and the recovered GRASS2020 blended distance ramp both stand.
///     </para>
/// </summary>
public sealed class Tes4GrassInstancingTests
{
    [Fact]
    public void OblivionSelectsTheInstancedBlendRoute()
    {
        var profile = GrassShaderProfile.InstancedBlendForGame(BethesdaGame.Oblivion);

        Assert.True(profile.Enabled);
        Assert.Equal("reference_grass_oblivion.vert.hlsl", profile.VertexShaderName);
        Assert.Equal("reference_grass_oblivion.frag.hlsl", profile.PixelShaderName);
    }

    [Fact]
    public void TheInstancedBlendRouteIsTheSamePairAsThePerDrawRoute()
    {
        // Load-bearing: ONE shader text serves both ABIs (GRASS_INSTANCED), so the recovered
        // GRASS2020 lighting has one implementation and the two routes cannot drift apart. If these
        // ever name different files, a blade would light differently depending on whether it happened
        // to be billboarded.
        Assert.Equal(
            GrassShaderProfile.ForGame(BethesdaGame.Oblivion),
            GrassShaderProfile.InstancedBlendForGame(BethesdaGame.Oblivion));
    }

    [Theory]
    // FO3/FNV grass is alpha-TESTED, so it already batches through the opaque cutout PSOs. Handing
    // it a BLENDED pipeline would silently turn cutout grass translucent.
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    [InlineData(BethesdaGame.Morrowind)]
    [InlineData(BethesdaGame.Skyrim)]
    // Unknown is what the headless NIF renderer passes.
    [InlineData(BethesdaGame.Unknown)]
    public void EveryOtherGameFallsBackStructurally(BethesdaGame game)
    {
        Assert.False(GrassShaderProfile.InstancedBlendForGame(game).Enabled);
    }

    [Fact]
    public void TheVertexShaderCarriesBothAbisFromOneText()
    {
        var vs = SourceContract.ReadShaderSource("reference_grass_oblivion.vert.hlsl");

        // The instanced arm must read the world matrix from the t8 instance buffer via SV_InstanceID…
        Assert.Contains("#ifdef GRASS_INSTANCED", vs, StringComparison.Ordinal);
        Assert.Contains("StructuredBuffer<float4x4> uInstanceWorlds : register(t8);", vs,
            StringComparison.Ordinal);
        Assert.Contains("uInstanceWorlds[uInstanceBase + instanceId]", vs, StringComparison.Ordinal);

        // …and the per-draw arm must keep uWorld at the head of b1. Both cbuffers must be declared:
        // the shared fields sit 64 bytes apart between the two layouts, so no single prefix serves
        // both, and picking the wrong one reads the world matrix out of AlphaState.
        Assert.Contains("cbuffer InstanceDraw : register(b1)", vs, StringComparison.Ordinal);
        Assert.Contains("cbuffer PerDraw : register(b1)", vs, StringComparison.Ordinal);
        Assert.Contains("float4x4 uWorld;", vs, StringComparison.Ordinal);

        // uInstanceBase sits at byte 64 of InstanceDraw, so every field ahead of it is load-bearing
        // padding and the struct must be declared in full rather than truncated.
        SourceContract.AssertOrder(
            vs, "cbuffer InstanceDraw", "uAlphaState", "uRenderState", "uTextureState", "uTexIndices",
            "uInstanceBase");
    }

    [Fact]
    public void GrassBatchesDrainAfterOpaqueAndNeverEnterTheShadowCascades()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        // Grass carries a BLENDED depth-writing pipeline here, so it must be issued after every
        // opaque batch or its soft edge texels blend against terrain and then depth-reject whatever
        // stands behind them.
        SourceContract.AssertOrder(
            renderer, "_opaqueBatches.OrderGrassBatchesLast();", "SortBatchInstancesByCascade();");

        // Grass was invisible to the shadow capture while it was blended per-draw. Moving it onto the
        // batch path must not make it start casting — retail shaderpackage019 has no grass caster
        // permutation for either game. ⚠ The exclusion must stay PAIRED with the per-game route flag;
        // see TheShadowCasterExclusionIsPairedWithTheRouteFlag for why the batch marker alone is wrong.
        Assert.Contains("var grassNeverCasts = fnvGrassNeverCasts", renderer, StringComparison.Ordinal);
        Assert.Contains("|| (_instancedBlendGrassSupported && batchState.UsesGrassDistanceEnvelope)",
            renderer, StringComparison.Ordinal);
    }

    /// <summary>
    ///     REGRESSION (introduced 2026-08-13, caught in review 08-14). The grass shadow-caster
    ///     exclusion must be scoped to the game that actually uses the instanced+blended route.
    ///     <c>UsesGrassDistanceEnvelope</c> reads like a TES4 marker but is NOT game-specific —
    ///     Skyrim and FO3/FNV set an envelope too — so gating on it alone silently stopped Skyrim
    ///     grass and FO3/FNV NON-TallGrass grass from casting sun shadows, which the original
    ///     <c>_tallGrassWindSupported &amp;&amp; sub.IsTallGrass</c> gate deliberately left alone.
    /// </summary>
    [Fact]
    public void EveryGameWithAGrassEnvelopeProvesTheMarkerIsNotGameScoped()
    {
        // If this ever becomes Oblivion-only, the pairing below stops being load-bearing — but do NOT
        // then simplify the gate: re-derive it, because the envelope is authored per game from INI
        // data and can gain arms at any time.
        foreach (var game in new[]
                 {
                     BethesdaGame.Oblivion, BethesdaGame.FalloutNewVegas,
                     BethesdaGame.Fallout3, BethesdaGame.Skyrim
                 })
        {
            Assert.True(
                GrassScatterProfile.ForGame(game).DistanceEnvelope.Enabled,
                $"{game} sets a grass distance envelope, so UsesGrassDistanceEnvelope cannot stand " +
                "alone as a TES4 marker.");
        }
    }

    [Fact]
    public void TheShadowCasterExclusionIsPairedWithTheRouteFlag()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");

        // The exclusion must AND the batch marker with the per-game route flag, never use it alone.
        Assert.Contains(
            "|| (_instancedBlendGrassSupported && batchState.UsesGrassDistanceEnvelope)",
            renderer, StringComparison.Ordinal);
        Assert.Contains(
            "_instancedBlendGrassSupported = GrassShaderProfile.InstancedBlendForGame(renderCache.Game).Enabled;",
            renderer, StringComparison.Ordinal);
    }

    [Fact]
    public void OnlyTheShapesTheInstancedAbiCanExpressAreRouted()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "ReferenceRenderer12.cs");
        var guard = SourceContract.Extract(renderer, "var instancedGrass = r.IsGrass", "if ((sub.AlphaRenderMode");

        // A billboard needs a unique camera-facing matrix per placement; a NiAlphaController's
        // material alpha is sampled per draw. Neither survives a per-BATCH constant buffer.
        Assert.Contains("!sub.IsBillboard", guard, StringComparison.Ordinal);
        Assert.Contains("sub.MaterialAlphaController is null", guard, StringComparison.Ordinal);
        // The route always picks the depth-WRITING blend PSO, so it may only take submeshes that
        // actually want depth writes.
        Assert.Contains("sub.DepthWritingBlend", guard, StringComparison.Ordinal);
        // And it must degrade structurally when the instanced VS failed to compile.
        Assert.Contains("_pipelines.InstancedBlendGrassShaderAvailable", guard, StringComparison.Ordinal);
    }
}