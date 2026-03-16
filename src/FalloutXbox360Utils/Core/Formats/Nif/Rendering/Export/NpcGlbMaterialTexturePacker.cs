using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbMaterialTexturePacker
{
    internal static DecodedTexture? BuildMetallicRoughnessTexture(
        DecodedTexture? normalTexture,
        bool hasGlossAlpha,
        DecodedTexture? environmentMaskTexture = null,
        bool hasEnvironmentMapping = false)
    {
        if ((normalTexture == null || !hasGlossAlpha) &&
            (environmentMaskTexture == null || !hasEnvironmentMapping))
        {
            return null;
        }

        var (width, height) = GetTargetSize(normalTexture, environmentMaskTexture);
        var packed = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var gloss = hasGlossAlpha && normalTexture != null
                    ? SampleAlpha(normalTexture, x, y, width, height)
                    : 0f;
                var roughness = hasGlossAlpha
                    ? 1f - gloss * 0.85f
                    : 1f;

                if (hasEnvironmentMapping && environmentMaskTexture != null)
                {
                    var mask = SampleLuminance(environmentMaskTexture, x, y, width, height);
                    roughness = roughness + (roughness * 0.55f - roughness) * mask;
                }

                packed[offset] = 255;
                packed[offset + 1] = (byte)Math.Clamp(MathF.Round(roughness * 255f), 0f, 255f);
                packed[offset + 2] = 0;
                packed[offset + 3] = 255;
            }
        }

        return DecodedTexture.FromBaseLevel(
            packed,
            width,
            height,
            false);
    }

    internal static DecodedTexture? BuildSpecularFactorTexture(
        DecodedTexture? normalTexture,
        bool hasGlossAlpha,
        DecodedTexture? environmentMaskTexture = null,
        bool hasEnvironmentMapping = false)
    {
        if ((normalTexture == null || !hasGlossAlpha) &&
            (environmentMaskTexture == null || !hasEnvironmentMapping))
        {
            return null;
        }

        var (width, height) = GetTargetSize(normalTexture, environmentMaskTexture);
        var packed = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var offset = (y * width + x) * 4;
                var spec = hasGlossAlpha && normalTexture != null
                    ? SampleAlpha(normalTexture, x, y, width, height)
                    : 1f;

                if (hasEnvironmentMapping && environmentMaskTexture != null)
                {
                    var mask = SampleLuminance(environmentMaskTexture, x, y, width, height);
                    spec *= mask;
                }

                packed[offset] = 255;
                packed[offset + 1] = 255;
                packed[offset + 2] = 255;
                packed[offset + 3] = (byte)Math.Clamp(MathF.Round(spec * 255f), 0f, 255f);
            }
        }

        return DecodedTexture.FromBaseLevel(
            packed,
            width,
            height,
            false);
    }

    internal static DecodedTexture? BuildOcclusionTexture(DecodedTexture? heightTexture)
    {
        if (heightTexture == null)
        {
            return null;
        }

        var pixels = heightTexture.Pixels;
        var packed = new byte[pixels.Length];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            var luminance = (byte)Math.Clamp(
                MathF.Round(pixels[index] * 0.2126f + pixels[index + 1] * 0.7152f + pixels[index + 2] * 0.0722f),
                0f,
                255f);
            packed[index] = luminance;
            packed[index + 1] = luminance;
            packed[index + 2] = luminance;
            packed[index + 3] = 255;
        }

        return DecodedTexture.FromBaseLevel(
            packed,
            heightTexture.Width,
            heightTexture.Height,
            false);
    }

    private static (int Width, int Height) GetTargetSize(
        DecodedTexture? primaryTexture,
        DecodedTexture? secondaryTexture)
    {
        if (primaryTexture != null)
        {
            return (primaryTexture.Width, primaryTexture.Height);
        }

        if (secondaryTexture != null)
        {
            return (secondaryTexture.Width, secondaryTexture.Height);
        }

        throw new ArgumentException("At least one texture is required.");
    }

    private static float SampleAlpha(
        DecodedTexture texture,
        int x,
        int y,
        int targetWidth,
        int targetHeight)
    {
        var sourceX = Math.Clamp((int)((long)x * texture.Width / targetWidth), 0, texture.Width - 1);
        var sourceY = Math.Clamp((int)((long)y * texture.Height / targetHeight), 0, texture.Height - 1);
        var offset = (sourceY * texture.Width + sourceX) * 4;
        return texture.Pixels[offset + 3] / 255f;
    }

    private static float SampleLuminance(
        DecodedTexture texture,
        int x,
        int y,
        int targetWidth,
        int targetHeight)
    {
        var sourceX = Math.Clamp((int)((long)x * texture.Width / targetWidth), 0, texture.Width - 1);
        var sourceY = Math.Clamp((int)((long)y * texture.Height / targetHeight), 0, texture.Height - 1);
        var offset = (sourceY * texture.Width + sourceX) * 4;
        return (texture.Pixels[offset] * 0.2126f +
                texture.Pixels[offset + 1] * 0.7152f +
                texture.Pixels[offset + 2] * 0.0722f) / 255f;
    }
}
