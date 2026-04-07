using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbAlphaTexturePacker
{
    internal static PreparedAlphaTexture Prepare(
        RenderableSubmesh submesh,
        DecodedTexture? diffuseTexture)
    {
        ArgumentNullException.ThrowIfNull(submesh);

        var alphaState = NifAlphaClassifier.Classify(submesh, diffuseTexture);
        var texture = diffuseTexture;
        var renderMode = alphaState.RenderMode;
        var threshold = alphaState.AlphaTestThreshold;
        var hasTextureTransform = false;

        if (renderMode == NifAlphaRenderMode.Blend &&
            alphaState.HasAlphaTest &&
            diffuseTexture != null &&
            UsesStandardAlphaBlend(alphaState) &&
            !IsGlassLikeShape(submesh) &&
            HasMostlyBinaryAlpha(diffuseTexture))
        {
            renderMode = NifAlphaRenderMode.Cutout;
        }

        // Hair NIFs typically have HasAlphaBlend=true but HasAlphaTest=false. Bethesda's
        // engine still uses alpha-test discard for hair via the SM3002 shader, not real
        // alpha blending. In glTF, BLEND mode causes overlapping hair cards to compound
        // darken each other (each layer multiplies by (1-srcAlpha)), creating visible
        // dark patches where cards stack. Force CUTOUT for hair-like meshes (tinted) so
        // each card pixel is either fully opaque or fully discarded.
        if (renderMode == NifAlphaRenderMode.Blend &&
            diffuseTexture != null &&
            IsHairShape(submesh) &&
            HasMostlyBinaryAlpha(diffuseTexture))
        {
            renderMode = NifAlphaRenderMode.Cutout;
            // Default cutout threshold for hair: 128 (mid-range). The SM3002 shader uses
            // engine-specific alpha test thresholds we don't replicate exactly.
            threshold = 128;
        }

        if (renderMode == NifAlphaRenderMode.Cutout && diffuseTexture != null)
        {
            switch (alphaState.AlphaTestFunction)
            {
                case 0: // ALWAYS
                    renderMode = NifAlphaRenderMode.Opaque;
                    break;
                case 1: // LESS
                    texture = InvertAlpha(diffuseTexture);
                    threshold = (byte)Math.Clamp(255 - threshold + 1, 0, 255);
                    hasTextureTransform = true;
                    break;
                case 3: // LEQUAL
                    texture = InvertAlpha(diffuseTexture);
                    threshold = (byte)Math.Clamp(255 - threshold, 0, 255);
                    hasTextureTransform = true;
                    break;
                case 6: // GEQUAL
                    threshold = (byte)Math.Clamp(threshold - 1, 0, 255);
                    break;
                case 7: // NEVER
                    threshold = 255;
                    break;
            }
        }

        return new PreparedAlphaTexture(texture, renderMode, threshold, hasTextureTransform);
    }

    private static bool IsGlassLikeShape(RenderableSubmesh submesh)
    {
        return ContainsGlassHint(submesh.ShapeName) ||
               ContainsGlassHint(submesh.DiffuseTexturePath);

        static bool ContainsGlassHint(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("glass", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("lens", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool IsHairShape(RenderableSubmesh submesh)
    {
        if (submesh.TintColor.HasValue)
        {
            return true;
        }

        return ContainsHairHint(submesh.ShapeName) || ContainsHairHint(submesh.DiffuseTexturePath);

        static bool ContainsHairHint(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            return value.Contains("hair", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("brow", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("lash", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("beard", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("mustache", StringComparison.OrdinalIgnoreCase) ||
                   value.Contains("goatee", StringComparison.OrdinalIgnoreCase);
        }
    }

    private static bool UsesStandardAlphaBlend(NifAlphaRenderState alphaState)
    {
        return alphaState.SrcBlendMode == 6 &&
               alphaState.DstBlendMode == 7;
    }

    private static bool HasMostlyBinaryAlpha(DecodedTexture texture)
    {
        var pixels = texture.Pixels;
        if (pixels.Length < 4)
        {
            return false;
        }

        var total = 0;
        var transitional = 0;
        var step = Math.Max(1, pixels.Length / (4 * 4096));
        for (var index = 3; index < pixels.Length; index += 4 * step)
        {
            var alpha = pixels[index];
            total++;
            if (alpha > 16 && alpha < 239)
            {
                transitional++;
            }
        }

        return total > 0 &&
               transitional <= Math.Max(1, total / 20);
    }

    private static DecodedTexture InvertAlpha(DecodedTexture texture)
    {
        var inverted = new byte[texture.Pixels.Length];
        for (var index = 0; index < texture.Pixels.Length; index += 4)
        {
            inverted[index] = texture.Pixels[index];
            inverted[index + 1] = texture.Pixels[index + 1];
            inverted[index + 2] = texture.Pixels[index + 2];
            inverted[index + 3] = (byte)(255 - texture.Pixels[index + 3]);
        }

        return DecodedTexture.FromBaseLevel(
            inverted,
            texture.Width,
            texture.Height,
            false);
    }

    internal readonly record struct PreparedAlphaTexture(
        DecodedTexture? Texture,
        NifAlphaRenderMode RenderMode,
        byte AlphaThreshold,
        bool HasTextureTransform);
}
