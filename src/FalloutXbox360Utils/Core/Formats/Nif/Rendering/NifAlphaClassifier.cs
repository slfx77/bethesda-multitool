using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

internal static class NifAlphaClassifier
{
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

        var renderMode = (hasAlphaBlend, hasAlphaTest) switch
        {
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

        if (ContainsHairHint(submesh.ShapeName))
        {
            return true;
        }

        return ContainsHairHint(submesh.DiffuseTexturePath);

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
