using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>Classifies a NIF shape's transparency into a <see cref="NifAlphaRenderMode" /> from its alpha and shader flags.</summary>
internal static class NifAlphaClassifier
{
    internal static NifAlphaRenderState Classify(
        RenderableSubmesh submesh,
        DecodedTexture? diffuseTexture)
    {
        // Engine-accurate alpha classification, decompiled from BSShader::SetupGeometryAlphaBlending +
        // BSShader::SetupGeometryRenderStates (MemDebug XEX — tools/GhidraProject/shader_zwrite_decompiled.txt):
        //   • Alpha BLEND is enabled by NiAlphaProperty bit 0 (carried by HasAlphaBlend). A NiMaterialProperty
        //     alpha < 1 with NO NiAlphaProperty does NOT blend in this path — solid meshes (ManiaSm25,
        //     RockDementiaMed03) carry a sub-1 material alpha purely as a shader input, so it is not a blend
        //     trigger. (Material alpha still modulates the blend WHEN a NiAlphaProperty is present.)
        //   • Blend factors come from NiAlphaProperty bits 1-4 (src) / 5-8 (dst), carried by Src/DstBlendMode.
        //   • Depth WRITE in the alpha pass follows the alpha-TEST-enable bit (bit 9): alpha-tested geometry
        //     writes depth, plain alpha-blend does not. BSShaderFlags2 ZBuffer_Write does NOT drive the
        //     per-draw Z-write — the previous "ZBuffer_Write ⇒ demote alpha-blend to opaque" rule was a
        //     workaround (for a leak the engine actually avoids by sorting + culling), not the engine's logic.
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

        // Engine: Z-write in the alpha pass = the alpha-TEST bit. A shape that BOTH blends and alpha-tests
        // therefore writes depth (its kept cutout texels are opaque). Keep it a blend but flag it
        // depth-writing, so the renderer keeps the blend yet writes depth and draws it before the water pass
        // (water then height-sorts against it). Plain alpha-blend (no test) does not write depth.
        //
        // Threshold gate: the depth-writing hoist is only sound when the test actually CUTS the shape out —
        // kept texels then approximate opaque geometry (NVSeaPlant02: threshold 124). A trivial threshold
        // keeps near-invisible texels (FXMistLow01Long: blend+test at threshold 1, a 95%-transparent mist
        // sheet), and hoisting that writes a full-quad depth footprint that punches holes in the water
        // drawn after it. The engine writes Z for both, but only gets away with it because its water draws
        // BEFORE the whole alpha pass; our water pass sits between the hoisted and deferred blend lists, so
        // low-threshold blend+test stays a plain sorted blend with Z-write off.
        const int depthWriteCutoutMinThreshold = 32;
        var depthWritingBlend = hasAlphaBlend && hasAlphaTest && !isHair &&
                                alphaTestThreshold >= depthWriteCutoutMinThreshold;

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
            depthWritingBlend);
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
