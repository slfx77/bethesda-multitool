namespace BethesdaMultitool.Core.Formats.Png;

/// <summary>
///     The IHDR header of a PNG image — everything needed to describe the file without inflating
///     its pixel stream.
/// </summary>
/// <param name="Width">Image width in pixels.</param>
/// <param name="Height">Image height in pixels.</param>
/// <param name="BitDepth">Bits per sample: 1, 2, 4, 8 or 16.</param>
/// <param name="ColourType">
///     PNG colour type: 0 greyscale, 2 truecolour, 3 palette, 4 greyscale+alpha, 6 RGBA.
/// </param>
/// <param name="Interlaced">
///     True when the image uses Adam7 interlacing. Worth surfacing rather than ignoring: one of
///     the 16 retail Stormhold PNGs is interlaced, and a decoder that silently treats an
///     interlaced stream as sequential produces a scrambled image rather than an error.
/// </param>
/// <param name="HasTransparencyChunk">True when a <c>tRNS</c> chunk is present.</param>
internal readonly record struct PngImageInfo(
    int Width,
    int Height,
    int BitDepth,
    int ColourType,
    bool Interlaced,
    bool HasTransparencyChunk)
{
    /// <summary>True when the image stores palette indices (colour type 3).</summary>
    public bool IsPalettized => ColourType == 3;

    /// <summary>A short human-readable name for <see cref="ColourType" />.</summary>
    public string ColourTypeName => ColourType switch
    {
        0 => "greyscale",
        2 => "truecolour",
        3 => "palette",
        4 => "greyscale+alpha",
        6 => "RGBA",
        _ => $"unknown({ColourType})"
    };
}
