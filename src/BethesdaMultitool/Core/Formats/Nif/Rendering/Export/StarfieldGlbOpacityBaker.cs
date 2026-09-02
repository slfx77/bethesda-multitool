using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Packs CE2's separate slot-2 red opacity into glTF base-color alpha. Core glTF has no separate
///     opacity texture, so this is exact only for the bounded default-UV path with matching texel
///     dimensions; mismatches fail closed and the caller exports the material opaque.
/// </summary>
internal static class StarfieldGlbOpacityBaker
{
    internal static StarfieldGlbOpacityBakeResult Bake(
        DecodedTexture? baseColor,
        DecodedTexture? opacity)
    {
        if (opacity is null || opacity.Width <= 0 || opacity.Height <= 0)
        {
            return new StarfieldGlbOpacityBakeResult(baseColor, false);
        }

        if (baseColor is not null &&
            (baseColor.Width != opacity.Width || baseColor.Height != opacity.Height) &&
            (baseColor.Width != 1 || baseColor.Height != 1))
        {
            return new StarfieldGlbOpacityBakeResult(baseColor, false);
        }

        var opacityPixels = opacity.Pixels;
        var pixelCount = checked(opacity.Width * opacity.Height);
        if (opacityPixels.Length != checked(pixelCount * 4) ||
            (baseColor is not null &&
             baseColor.Pixels.Length != opacityPixels.Length &&
             baseColor.Pixels.Length != 4))
        {
            return new StarfieldGlbOpacityBakeResult(baseColor, false);
        }

        var source = baseColor?.Pixels;
        var packed = new byte[opacityPixels.Length];
        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var offset = pixel * 4;
            var sourceOffset = source is { Length: 4 } ? 0 : offset;
            packed[offset] = source?[sourceOffset] ?? byte.MaxValue;
            packed[offset + 1] = source?[sourceOffset + 1] ?? byte.MaxValue;
            packed[offset + 2] = source?[sourceOffset + 2] ?? byte.MaxValue;
            packed[offset + 3] = opacityPixels[offset];
        }

        return new StarfieldGlbOpacityBakeResult(
            DecodedTexture.FromBaseLevel(packed, opacity.Width, opacity.Height, false),
            true);
    }

    /// <summary>
    ///     Projects the bounded Effect-route glass state into glTF base-colour alpha. CE2 ignores
    ///     albedo-image alpha here: slot 2 red (or its flat replacement) is the opacity source and
    ///     <c>MaterialOverallAlpha</c> is a separate multiplier.
    /// </summary>
    internal static StarfieldGlbEffectAlphaBakeResult BakeEffectAlpha(
        DecodedTexture? baseColor,
        DecodedTexture? opacity,
        StarfieldMaterialEffectAlphaState state)
    {
        if (!float.IsFinite(state.MaterialOverallAlpha) ||
            state.MaterialOverallAlpha is < 0f or > 1f)
        {
            return new StarfieldGlbEffectAlphaBakeResult(baseColor, false, 1f);
        }

        if (state.OpacitySlot.TexturePath is { Length: > 0 })
        {
            // A declared-but-unavailable image must not fall through to arbitrary albedo alpha.
            var packed = Bake(baseColor, opacity);
            return new StarfieldGlbEffectAlphaBakeResult(
                packed.Texture,
                packed.Applied,
                state.MaterialOverallAlpha);
        }

        var opacityFactor = state.OpacitySlot.ReplacementRgba is { } replacement
            ? (replacement & 0xFF) / 255f
            : 1f;
        if (!TryForceOpaqueTextureAlpha(baseColor, out var sanitized))
        {
            return new StarfieldGlbEffectAlphaBakeResult(baseColor, false, 1f);
        }

        return new StarfieldGlbEffectAlphaBakeResult(
            sanitized,
            true,
            state.MaterialOverallAlpha * opacityFactor);
    }

    private static bool TryForceOpaqueTextureAlpha(
        DecodedTexture? texture,
        out DecodedTexture? sanitized)
    {
        sanitized = texture;
        if (texture is null)
        {
            return true;
        }

        var pixelCount = checked(texture.Width * texture.Height);
        if (texture.Width <= 0 || texture.Height <= 0 ||
            texture.Pixels.Length != checked(pixelCount * 4))
        {
            return false;
        }

        var pixels = (byte[])texture.Pixels.Clone();
        for (var offset = 3; offset < pixels.Length; offset += 4)
        {
            pixels[offset] = byte.MaxValue;
        }

        sanitized = DecodedTexture.FromBaseLevel(
            pixels,
            texture.Width,
            texture.Height,
            false);
        return true;
    }
}

internal readonly record struct StarfieldGlbOpacityBakeResult(
    DecodedTexture? Texture,
    bool Applied);

internal readonly record struct StarfieldGlbEffectAlphaBakeResult(
    DecodedTexture? Texture,
    bool Applied,
    float AlphaFactor);
