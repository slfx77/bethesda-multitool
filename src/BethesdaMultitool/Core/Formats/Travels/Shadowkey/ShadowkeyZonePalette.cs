using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Travels.Shadowkey;

/// <summary>
///     A Shadowkey zone's <c>.pal</c> file: 768 raw bytes, 256 RGB triplets, uncompressed (this is
///     the one per-zone file that is NOT wrapped in the <see cref="ShadowkeyCompressedFile" />
///     envelope). It is what the zone's <c>.ztx</c> textures and <c>.zsk</c> sky index.
///     <para>
///         The components are FULL-RANGE 8-bit, not 6-bit VGA: the maximum component is 255 in all
///         21 retail palettes, so they go through <see cref="Palette.FromRgb8" /> and must never be
///         promoted — the same trap the Arena COL palettes carry, where promoting turns grey into
///         cyan.
///     </para>
///     <para>
///         Seven distinct palettes serve the 21 zones (12 share one, 4 share another, 5 zones have
///         their own). In 13 of them entry 6 is 0xFF00FF and no other entry is magenta; the other
///         five palettes carry no magenta at all. Where it exists, index 6 is the colour key: the
///         zone's <c>.zlu</c> light table pins that entry to white at every light level so the
///         rasteriser can still reject it after lighting. Index 255 is black in all 21.
///     </para>
/// </summary>
internal static class ShadowkeyZonePalette
{
    /// <summary>Bytes in a <c>.pal</c> file: 256 RGB triplets.</summary>
    public const int FileLength = Palette.RgbByteCount;

    /// <summary>The colour-key colour: magenta, entry 6 in 13 of the 21 retail zones.</summary>
    public const uint ColourKeyRgb = 0xFF00FFu;

    /// <summary>
    ///     Parses a <c>.pal</c> file. Throws <see cref="InvalidDataException" /> naming
    ///     <paramref name="name" /> when it is not exactly <see cref="FileLength" /> bytes.
    /// </summary>
    public static Palette Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException(
                $"'{name}': a Shadowkey palette is {FileLength} bytes (256 x RGB8), got {bytes.Length}.");
        }

        return Palette.FromRgb8(bytes);
    }

    /// <summary>
    ///     Parses a <c>.pal</c> file and, when it carries the magenta colour key, returns the
    ///     palette with that entry made transparent. Zones whose palette has no magenta (8 of 21)
    ///     come back unchanged.
    /// </summary>
    public static Palette ParseWithColourKey(ReadOnlySpan<byte> bytes, string name)
    {
        var palette = Parse(bytes, name);
        var key = FindColourKeyIndex(bytes);
        return key.HasValue ? palette.WithTransparentIndex(key.Value) : palette;
    }

    /// <summary>
    ///     Returns the index of the single 0xFF00FF entry, or null when the palette has none.
    ///     Retail: 6 in 13 zones, absent in the other 8.
    /// </summary>
    public static int? FindColourKeyIndex(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != FileLength)
        {
            return null;
        }

        for (var i = 0; i < Palette.EntryCount; i++)
        {
            var offset = i * 3;
            if (bytes[offset] == 0xFF && bytes[offset + 1] == 0x00 && bytes[offset + 2] == 0xFF)
            {
                return i;
            }
        }

        return null;
    }
}
