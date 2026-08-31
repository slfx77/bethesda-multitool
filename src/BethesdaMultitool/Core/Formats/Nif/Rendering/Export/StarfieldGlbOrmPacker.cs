using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Projects CE2's three separate red-channel scalar textures into core glTF's shared ORM image:
///     ambient occlusion in R, roughness in G, and metalness in B. No resampling is performed; authored
///     images must have identical dimensions for their texels to retain the same UV correspondence.
/// </summary>
internal static class StarfieldGlbOrmPacker
{
    internal static StarfieldGlbOrmPackResult Pack(
        StarfieldMaterialOrmState state,
        DecodedTexture? roughness,
        DecodedTexture? metalness,
        DecodedTexture? ambientOcclusion)
    {
        if (!MatchesSlot(state.RoughnessSlot, roughness) ||
            !MatchesSlot(state.MetalnessSlot, metalness) ||
            !MatchesSlot(state.AmbientOcclusionSlot, ambientOcclusion))
        {
            return default;
        }

        DecodedTexture? firstTexture = roughness ?? metalness ?? ambientOcclusion;
        var needsImage = firstTexture is not null ||
                         state.RoughnessSlot.ReplacementRgba.HasValue ||
                         state.MetalnessSlot.ReplacementRgba.HasValue ||
                         state.AmbientOcclusionSlot.ReplacementRgba.HasValue;
        if (!needsImage)
        {
            // libfo76utils' CE2 constructor defaults are roughness=black, metalness=black, AO=white.
            // glTF can express the first two as factors without allocating a redundant white image.
            return new StarfieldGlbOrmPackResult(null, true, false, 0f, 0f);
        }

        var width = firstTexture?.Width ?? 1;
        var height = firstTexture?.Height ?? 1;
        if (!IsValidTexture(roughness, width, height) ||
            !IsValidTexture(metalness, width, height) ||
            !IsValidTexture(ambientOcclusion, width, height))
        {
            return default;
        }

        int pixelCount;
        int byteCount;
        try
        {
            pixelCount = checked(width * height);
            byteCount = checked(pixelCount * 4);
        }
        catch (OverflowException)
        {
            return default;
        }

        var packed = new byte[byteCount];
        var roughnessConstant = RedOrDefault(state.RoughnessSlot, 0);
        var metalnessConstant = RedOrDefault(state.MetalnessSlot, 0);
        var aoConstant = RedOrDefault(state.AmbientOcclusionSlot, byte.MaxValue);
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 4;
            packed[offset] = ambientOcclusion?.Pixels[offset] ?? aoConstant;
            packed[offset + 1] = roughness?.Pixels[offset] ?? roughnessConstant;
            packed[offset + 2] = metalness?.Pixels[offset] ?? metalnessConstant;
            packed[offset + 3] = byte.MaxValue;
        }

        return new StarfieldGlbOrmPackResult(
            DecodedTexture.FromBaseLevel(packed, width, height, generateMipChain: false),
            true,
            state.AmbientOcclusionSlot.IsResolved,
            1f,
            1f);
    }

    private static bool MatchesSlot(StarfieldMaterialSlot slot, DecodedTexture? texture)
    {
        // A caller must provide every authored image and must not substitute an image for a flat
        // replacement/missing channel. Either mismatch makes a partial ORM image misleading.
        if (slot.TexturePath is { Length: > 0 })
        {
            return texture is not null && !slot.ReplacementRgba.HasValue;
        }

        return texture is null;
    }

    private static bool IsValidTexture(DecodedTexture? texture, int width, int height)
    {
        if (texture is null)
        {
            return true;
        }

        if (texture.Width <= 0 ||
            texture.Height <= 0 ||
            texture.Width != width ||
            texture.Height != height)
        {
            return false;
        }

        try
        {
            return texture.Pixels.Length == checked(width * height * 4);
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    private static byte RedOrDefault(StarfieldMaterialSlot slot, byte defaultValue)
    {
        return slot.ReplacementRgba is { } rgba
            ? (byte)(rgba & 0xFF)
            : defaultValue;
    }
}

internal readonly record struct StarfieldGlbOrmPackResult(
    DecodedTexture? Texture,
    bool Applied,
    bool HasAmbientOcclusion,
    float MetallicFactor,
    float RoughnessFactor);
