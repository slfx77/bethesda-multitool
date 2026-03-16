using FalloutXbox360Utils.Core.Formats.Dds;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Export;

internal static class NpcGlbNormalMapPacker
{
    internal static NpcGlbPackedNormal ResolvePacked(
        NifTextureResolver textureResolver,
        string? normalMapPath)
    {
        ArgumentNullException.ThrowIfNull(textureResolver);

        if (string.IsNullOrWhiteSpace(normalMapPath))
        {
            return default;
        }

        var normalTexture = textureResolver.GetTexture(normalMapPath);
        if (normalTexture == null)
        {
            return default;
        }

        var specularTexture = TryResolveSpecularTexture(textureResolver, normalMapPath);
        var mergedTexture = MergeNormalAndSpecular(normalTexture, specularTexture);
        var hasGlossAlpha = specularTexture != null || HasVariableAlpha(normalTexture);

        return new NpcGlbPackedNormal(mergedTexture, hasGlossAlpha);
    }

    internal static DecodedTexture? Resolve(
        NifTextureResolver textureResolver,
        string? normalMapPath)
    {
        return ResolvePacked(textureResolver, normalMapPath).Texture;
    }

    internal static string? DeriveSpecularPath(string? normalMapPath)
    {
        if (string.IsNullOrWhiteSpace(normalMapPath))
        {
            return null;
        }

        if (normalMapPath.EndsWith("_n.dds", StringComparison.OrdinalIgnoreCase))
        {
            return normalMapPath[..^6] + "_s.dds";
        }

        if (normalMapPath.EndsWith("_n.ddx", StringComparison.OrdinalIgnoreCase))
        {
            return normalMapPath[..^6] + "_s.ddx";
        }

        return null;
    }

    internal static DecodedTexture MergeNormalAndSpecular(
        DecodedTexture normalTexture,
        DecodedTexture? specularTexture)
    {
        ArgumentNullException.ThrowIfNull(normalTexture);

        if (specularTexture == null ||
            specularTexture.Width != normalTexture.Width ||
            specularTexture.Height != normalTexture.Height)
        {
            return normalTexture;
        }

        var mergedPixels = new byte[normalTexture.Pixels.Length];
        for (var index = 0; index < mergedPixels.Length; index += 4)
        {
            mergedPixels[index] = normalTexture.Pixels[index];
            mergedPixels[index + 1] = normalTexture.Pixels[index + 1];
            mergedPixels[index + 2] = normalTexture.Pixels[index + 2];
            mergedPixels[index + 3] = specularTexture.Pixels[index];
        }

        return DecodedTexture.FromBaseLevel(
            mergedPixels,
            normalTexture.Width,
            normalTexture.Height,
            false);
    }

    private static DecodedTexture? TryResolveSpecularTexture(
        NifTextureResolver textureResolver,
        string normalMapPath)
    {
        var specularPath = DeriveSpecularPath(normalMapPath);
        return specularPath != null
            ? textureResolver.GetTexture(specularPath)
            : null;
    }

    private static bool HasVariableAlpha(DecodedTexture texture)
    {
        var pixels = texture.Pixels;
        if (pixels.Length < 8)
        {
            return false;
        }

        var firstAlpha = pixels[3];
        for (var index = 7; index < pixels.Length; index += 4)
        {
            if (pixels[index] != firstAlpha)
            {
                return true;
            }
        }

        return false;
    }

    internal readonly record struct NpcGlbPackedNormal(
        DecodedTexture? Texture,
        bool HasGlossAlpha);
}
