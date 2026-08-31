using BethesdaMultitool.Core.Formats.Dds;

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
}

internal readonly record struct StarfieldGlbOpacityBakeResult(
    DecodedTexture? Texture,
    bool Applied);
