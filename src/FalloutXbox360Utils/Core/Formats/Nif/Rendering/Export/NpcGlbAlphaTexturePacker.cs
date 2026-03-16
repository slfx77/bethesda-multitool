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
            HasMostlyBinaryAlpha(diffuseTexture))
        {
            renderMode = NifAlphaRenderMode.Cutout;
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
