using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>Classifies a NIF shape's transparency into a <see cref="NifAlphaRenderMode" /> from its alpha and shader flags.</summary>
internal static class NifAlphaClassifier
{
    internal static NifAlphaRenderState Classify(
        RenderableSubmesh submesh,
        DecodedTexture? diffuseTexture)
    {
        // Engine-accurate alpha classification, decompiled from BSShader::SetupGeometryAlphaBlending
        // (MemDebug XEX):
        //   • Alpha BLEND is enabled by NiAlphaProperty bit 0 (carried by HasAlphaBlend). A NiMaterialProperty
        //     alpha < 1 with NO NiAlphaProperty does NOT blend in this path — solid meshes (ManiaSm25,
        //     RockDementiaMed03) carry a sub-1 material alpha purely as a shader input, so it is not a blend
        //     trigger. (Material alpha still modulates the blend WHEN a NiAlphaProperty is present.)
        //   • Blend factors come from NiAlphaProperty bits 1-4 (src) / 5-8 (dst), carried by Src/DstBlendMode.
        //   • Depth WRITE: the 2026-08-04 RE round (memory note fnv_alpha_zwrite_order_re_2026_08_04)
        //     PROVED the earlier "z-write follows the alpha-test bit" reading FALSE — that call site
        //     (BSShader::SetupGeometryRenderStates) sets CULL MODE and ALPHA TEST only. The engine's
        //     real rule is ambient-ON with enumerated OFF exceptions; see EngineZWriteOff below. The
        //     LEGACY DepthWritingBlend heuristic is retained for every stream-off route.
        // Closed-hull see-through is handled deterministically the SAME way the engine handles it: the
        // renderer sorts blended draws back-to-front (ReferenceRenderer12._blendedDraws) and back-face-culls
        // non-double-sided shapes (ReferencePipelineFactory12). So every alpha shape keeps its authored blend —
        // no shader-type or geometry heuristic demotion.
        var hasAlphaBlend = submesh.HasAlphaBlend;
        var hasAlphaTest = submesh.HasAlphaTest;
        var alphaTestThreshold = submesh.AlphaTestThreshold;
        var alphaTestFunction = submesh.AlphaTestFunction;

        var isHair = IsHairLikeSubmesh(submesh);

        // Hair/brow/lash without an explicit NiAlphaProperty but with a see-through diffuse: the engine's
        // tinted-hair path renders these as an alpha-test cutout. Hair-only (gated by TintColor/hair name) —
        // never a general "texture has alpha ⇒ cutout" rule, which would punch holes in opaque
        // specular-alpha surfaces.
        if (!hasAlphaBlend && !hasAlphaTest &&
            diffuseTexture != null &&
            ShouldUseTextureAlphaCutoutFallback(submesh, diffuseTexture))
        {
            hasAlphaTest = true;
            alphaTestThreshold = 0;
            alphaTestFunction = 4;
        }

        // LEGACY depth-writing-blend heuristic (NOT the engine rule — see EngineZWriteOff below):
        // a shape that both blends and alpha-tests with a cutting threshold keeps its blend but
        // writes depth, and the renderer draws it before the water pass on stream-off routes
        // (water then height-sorts against it). Plain alpha-blend (no test) does not write depth.
        //
        // Threshold gate: the depth-writing hoist is only sound when the test actually CUTS the shape out —
        // kept texels then approximate opaque geometry (NVSeaPlant02: threshold 124). A trivial threshold
        // keeps near-invisible texels (FXMistLow01Long: blend+test at threshold 1, a 95%-transparent mist
        // sheet), and hoisting that writes a full-quad depth footprint that punches holes in the water
        // drawn after it. The engine writes Z for both, but only gets away with it because its water draws
        // BEFORE the whole alpha pass; our water pass sits between the hoisted and deferred blend lists, so
        // low-threshold blend+test stays a plain sorted blend with Z-write off.
        //
        // The threshold is a PROXY for "the test really cuts", not the property itself. Foliage
        // routinely authors blend+test at threshold 0 with function GREATER (WhiteHorseNettle:
        // flags 0x12ED, threshold 0) — its cut texels are exactly alpha 0, so a >0 test cuts the
        // silhouette just as hard as a threshold-124 test, and the engine writes depth for it. The
        // mist counter-example survives its own >0 test everywhere (a continuous low-alpha wash),
        // which is what would punch the water hole. So when the threshold is trivial, ask the
        // DIFFUSE ALPHA whether this is a binary cutout or a wash, and hoist only the cutout.
        // Without this, an intra-shape blend (leaf + flower + berries in ONE alpha shape) has no
        // depth to sort against and paints back geometry over front geometry.
        const int depthWriteCutoutMinThreshold = 32;
        var cutsLikeAFoliageCard = alphaTestThreshold >= depthWriteCutoutMinThreshold ||
                                   diffuseTexture?.HasCutoutAlpha() == true;
        var depthWritingBlend = hasAlphaBlend && hasAlphaTest && !isHair && cutsLikeAFoliageCard;

        // ENGINE z-write rule (decompile-proven, memory fnv_alpha_zwrite_order_re_2026_08_04):
        // z-write is ambient-ON; the ONLY off-switches are decals (Shader Flags bits 26/27 —
        // already folded into submesh.IsDecal alongside the BGSM decal byte), hair (bit 18 with
        // blend; the engine's ShadowLight tech-6/8 pass also drops color-write, a multi-pass
        // detail we approximate), and BSShaderNoLighting honoring Shader Flags 2 bit 0 "zbuffer
        // write" — CLEAR means OFF (alpha_zwrite3_decompiled.txt:383-391; FXMistLow01Long's
        // SmokeWispsTile sheet authors exactly flags2=0x0). Lit geometry IGNORES the flag pair,
        // so an ordinary blended LIT shape KEEPS z-write — that resolves WhiteHorseNettle.
        // NoLighting also honors Shader Flags bit 31 "zbuffer test": CLEAR ⇒ test OFF (the
        // authored default 0x82000000 has it SET — an inverted read would disable the depth test
        // for nearly every NoLighting sheet in the game).
        //
        // Both bits are consumed ONLY by the unified transparency stream's per-draw PSO choice.
        // A shape with no authored evidence (particle-system submeshes never populate
        // ShaderMetadata; NiTexturing-era games and unmatched FNV property types yield null)
        // MIRRORS the legacy classification instead of taking a lit default: Morrowind authors
        // its era's z-write channel on NiZBufferProperty (944/948 retail blocks say write-OFF),
        // and retail NoLighting particles (SandDust02) must keep their soft-particle feathering.
        bool engineZWriteOff;
        var depthTestOff = false;
        if (submesh.ShaderMetadata is { ShaderFlags: { } shaderFlags, ShaderFlags2: { } shaderFlags2 } metadata)
        {
            var isNoLighting = metadata.PropertyType == "BSShaderNoLightingProperty";
            engineZWriteOff =
                submesh.IsDecal ||
                (hasAlphaBlend && (shaderFlags & 0x40000) != 0) ||
                (isNoLighting && (shaderFlags2 & 1) == 0) ||
                // ⚠ VIEWER SAFETY RESTRICTION (not the engine's rule). Our depth-writing blend PSO
                // has no alpha clip of its own: the discard is the ALPHA TEST, and
                // ReferenceMeshCache12.BuildAlphaState only enables it when the shape authored one.
                // A pure blend (no test) that writes depth therefore deposits depth across its
                // FULLY TRANSPARENT texels — with thousands of blended cards on screen that
                // wrecked the frame (A/B at render distance 32: engine-rule ON differed from the
                // legacy order by 328,701 of 328,704 px; with this leg the same frame matches
                // legacy to 1,093 px). Blend+test shapes keep engine z-write, so the proven
                // WhiteHorseNettle fix (blend + test at threshold 0) is unaffected.
                // Lift this only together with a shader-side near-zero-alpha discard for
                // depth-writing blends.
                (hasAlphaBlend && !hasAlphaTest);
            depthTestOff = isNoLighting && (shaderFlags & 0x80000000) == 0;
        }
        else
        {
            engineZWriteOff = !depthWritingBlend;
        }

        // Hair / brow / lash submeshes use alpha-to-coverage instead of plain blend (the engine renders these
        // via BSRenderState::SetAlphaToCoverageEnable to avoid strand stacking / brown forehead patches).
        var renderMode = (hasAlphaBlend, hasAlphaTest) switch
        {
            (true, _) when isHair => NifAlphaRenderMode.AlphaToCoverage,
            (true, _) => NifAlphaRenderMode.Blend,
            (_, true) => NifAlphaRenderMode.Cutout,
            _ => NifAlphaRenderMode.Opaque
        };

        return new NifAlphaRenderState(
            renderMode,
            hasAlphaBlend,
            hasAlphaTest,
            alphaTestThreshold,
            alphaTestFunction,
            submesh.SrcBlendMode,
            submesh.DstBlendMode,
            submesh.MaterialAlpha,
            depthWritingBlend,
            engineZWriteOff,
            depthTestOff);
    }

    private static bool ShouldUseTextureAlphaCutoutFallback(
        RenderableSubmesh submesh,
        DecodedTexture diffuseTexture)
    {
        if (!diffuseTexture.HasSignificantAlpha())
        {
            return false;
        }

        if (submesh.TintColor.HasValue)
        {
            return true;
        }

        return IsHairLikeSubmesh(submesh);
    }

    /// <summary>
    ///     True when the submesh is hair, eyebrow, or eyelash geometry (identified by shape
    ///     name or diffuse texture path). Used by the GPU renderer to route these submeshes
    ///     to an alpha-to-coverage pipeline — Bethesda's engine renders them with A2C +
    ///     multi-pass sort (see BSRenderState::SetAlphaToCoverageEnable in the engine), and
    ///     a single-pass plain blend would otherwise stack to brown patches on the forehead.
    /// </summary>
    internal static bool IsHairLikeSubmesh(RenderableSubmesh submesh)
    {
        return ContainsHairHint(submesh.ShapeName) ||
               ContainsHairHint(submesh.DiffuseTexturePath);

        static bool ContainsHairHint(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("brow", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("lash", StringComparison.OrdinalIgnoreCase);
        }
    }

}
