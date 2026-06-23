using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Inspection;

/// <summary>Classifies a NIF shape's transparency into a <see cref="NifAlphaRenderMode" /> from its alpha and shader flags.</summary>
internal static class NifAlphaClassifier
{
    // BSShaderFlags2 bit 0 = ZBuffer_Write (see nif.xml "BSShaderFlags2"). When set, the engine
    // writes depth for the shape regardless of its alpha-blend state — i.e. it is an occluder.
    private const uint ZBufferWriteFlag = 1u << 0;

    internal static NifAlphaRenderState Classify(
        RenderableSubmesh submesh,
        DecodedTexture? diffuseTexture)
    {
        // Alpha blending is enabled ONLY by NiAlphaProperty (bit 0) — that's what `HasAlphaBlend`
        // carries. A NiMaterialProperty alpha < 1 with NO NiAlphaProperty does NOT make geometry
        // transparent in Gamebryo; many opaque architecture/rock meshes (e.g. ManiaSm25,
        // RockDementiaMed03) carry a sub-1 material alpha purely as a shader input. Treating that
        // alone as "blend" misclassified solid meshes, rendering them see-through over the
        // premultiplied swapchain. The material alpha still modulates the blend WHEN a NiAlphaProperty
        // is present (it flows through to vAlphaState.z in BuildAlphaState), so dropping the heuristic
        // only changes property-less meshes — and matches the engine, not a substring/threshold guess.
        var hasAlphaBlend = submesh.HasAlphaBlend;
        var hasAlphaTest = submesh.HasAlphaTest;
        var alphaTestThreshold = submesh.AlphaTestThreshold;
        var alphaTestFunction = submesh.AlphaTestFunction;

        if (!hasAlphaBlend && !hasAlphaTest &&
            diffuseTexture != null &&
            ShouldUseTextureAlphaCutoutFallback(submesh, diffuseTexture))
        {
            hasAlphaTest = true;
            alphaTestThreshold = 0;
            alphaTestFunction = 4;
        }

        var isHair = IsHairLikeSubmesh(submesh);

        // Depth-writing "blend": a shape whose shader sets ZBuffer_Write is an authored OCCLUDER — the
        // engine writes depth for it — not see-through transparency. Our renderer only writes depth in
        // the opaque/cutout pass, so an occluder wrongly left in the depth-write-OFF blend pass renders
        // see-through: its interior faces and anything behind it show through. Two real cases:
        //   • MobileHomeASNV cabin shell — alpha-blend + alpha-TEST on an opaque hull with window cutouts
        //     (interior showed through the roof).
        //   • McCarran NV_McCarran-Wall03 tower body — alpha-blend + ZBuffer_Write, NO alpha-test (a solid
        //     stone tower); leaving it in the blend pass let the railing/interior render through the
        //     metal ring on the pillar.
        // So drop the blend whenever ZBuffer_Write is set, routing the shape to a depth-writing pass —
        // EXCEPT for genuine FX (smoke / glass / decals / light cones in effects\ or so named): some of
        // those quirkily set ZBuffer_Write yet are real transparency, and forcing them opaque (or hard
        // alpha-test, which gives the reported "sharp edges" on smoke/sandstorms) is worse than the
        // occlusion artifact. When a non-FX occluder has no alpha-test but its diffuse carries see-through
        // texels (window glass), enable an alpha-test cutout so those holes survive instead of filling in
        // solid. Hair keeps its A2C path (it sets the bit too, but A2C already writes depth).
        if (hasAlphaBlend && !isHair && !IsEffectsLikeSubmesh(submesh) &&
            submesh.ShaderMetadata?.ShaderFlags2 is { } shaderFlags2 &&
            (shaderFlags2 & ZBufferWriteFlag) != 0)
        {
            hasAlphaBlend = false;
            if (!hasAlphaTest && diffuseTexture is not null && diffuseTexture.HasSignificantAlpha())
            {
                hasAlphaTest = true;
                alphaTestThreshold = 128;
                alphaTestFunction = 4; // GREATER — discard see-through texels, keep the solid body
            }
        }

        // Hair / brow / lash submeshes use alpha-to-coverage instead of plain blend.
        // Bethesda's engine renders these via BSRenderState::SetAlphaToCoverageEnable to avoid
        // strand stacking (visible as brown patches on the forehead with standard sorted blend).
        // The CPU rasterizer turns this into a stochastic per-pixel Bayer threshold; the GPU
        // renderer routes to its AlphaToCoverageEnable pipeline on the 4x MSAA render target.
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
            submesh.MaterialAlpha);
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

    /// <summary>
    ///     True when the submesh is a genuine transparency effect — smoke, dust/sandstorm, cloud, steam,
    ///     fog, glass/ice, a decal, a light cone/shaft, or a lens flare/halo — identified by its diffuse
    ///     texture path (notably an <c>effects\</c> / <c>fx\</c> folder) or shape name. Such shapes are
    ///     left in the alpha-blend pass even when they set ZBuffer_Write, so they keep their soft gradient
    ///     edges (forcing them to a depth-writing opaque/alpha-test pass gives the reported "sharp edges"
    ///     on smoke/sandstorms and makes glass/ice render solid). Architecture occluders never match.
    /// </summary>
    internal static bool IsEffectsLikeSubmesh(RenderableSubmesh submesh)
    {
        return ContainsFxHint(submesh.DiffuseTexturePath) || ContainsFxHint(submesh.ShapeName);

        static bool ContainsFxHint(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var v = value.Replace('/', '\\');
            return v.Contains("\\effects\\", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("\\fx\\", StringComparison.OrdinalIgnoreCase) ||
                   v.StartsWith("effects\\", StringComparison.OrdinalIgnoreCase) ||
                   v.StartsWith("fx\\", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("smoke", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("sandstorm", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("dust", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("cloud", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("steam", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("fog", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("decal", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("lightcone", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("lightshaft", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("flare", StringComparison.OrdinalIgnoreCase) ||
                   v.Contains("halo", StringComparison.OrdinalIgnoreCase);
        }
    }
}
