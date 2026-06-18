using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal static class NifAlphaClassifier
{
    // BSShaderFlags2 bit 0 = ZBuffer_Write (see nif.xml "BSShaderFlags2"). When set, the engine
    // writes depth for the shape regardless of its alpha-blend state — i.e. it is an occluder.
    private const uint ZBufferWriteFlag = 1u << 0;

    internal static NifAlphaRenderState Classify(
        RenderableSubmesh submesh,
        DecodedTexture? diffuseTexture)
    {
        var hasAlphaBlend = submesh.HasAlphaBlend || submesh.MaterialAlpha < 1f;
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

        // Depth-writing "blend": a shape that is alpha-blend + alpha-TEST and has ZBuffer_Write set is
        // an authored occluder with cutouts (e.g. the MobileHomeASNV cabin shell — an opaque hull with
        // window holes), not see-through transparency. Our renderer only writes depth in the
        // opaque/cutout pass, so drop the blend and let the switch route it to Cutout (it alpha-tests)
        // — otherwise its interior faces render in the depth-write-off blend pass and show through the
        // roof. The alpha-test requirement is load-bearing: pure-blend decals/glass/smoke (alpha-blend,
        // NO alpha-test) sometimes ALSO set ZBuffer_Write, and stripping their blend made them render
        // fully opaque. Hair keeps its A2C path (it sets the bit too, but A2C already writes depth).
        if (hasAlphaBlend && hasAlphaTest && !isHair &&
            submesh.ShaderMetadata?.ShaderFlags2 is { } shaderFlags2 &&
            (shaderFlags2 & ZBufferWriteFlag) != 0)
        {
            hasAlphaBlend = false;
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
}
