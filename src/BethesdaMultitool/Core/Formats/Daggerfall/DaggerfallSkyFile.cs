// Ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/SkyFile.cs. License
//   texts are collected centrally in THIRD_PARTY_LICENSES.

using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     A Daggerfall <c>SKYnn.DAT</c> sky set (32 of them, SKY00-SKY31, one per climate/weather
///     combination). Entirely fixed-layout with no header: 32 palettes of 776 bytes each from
///     offset 0 (the same COL layout as ART_PAL.COL, full-range 8-bit), then — after 524,288
///     bytes of unused padding — 64 raw 512x220 indexed frames from offset 549,120. Frames 0-31
///     are the west half of the sky dome, 32-63 the east; frame <c>f</c> of either half is lit
///     by palette <c>f</c>, which is how the sky shades from dawn through night.
///     <para>
///         Every retail set is exactly 7,758,080 bytes (verified across all 32, 2026-09-02).
///         <c>SKYPAL.DAT</c> beside them is a 1,536-byte pair of raw palettes, not a sky set,
///         and is not this format.
///     </para>
/// </summary>
internal sealed class DaggerfallSkyFile
{
    /// <summary>Frame width in pixels.</summary>
    public const int FrameWidth = 512;

    /// <summary>Frame height in pixels.</summary>
    public const int FrameHeight = 220;

    /// <summary>Palettes in a set, one per time-of-day step.</summary>
    public const int PaletteCount = 32;

    /// <summary>Frames per half of the dome.</summary>
    public const int FramesPerHalf = 32;

    /// <summary>Total frames: west half then east half.</summary>
    public const int FrameCount = FramesPerHalf * 2;

    /// <summary>Byte offset of the first frame.</summary>
    public const int ImageDataOffset = 549_120;

    /// <summary>Exact byte length of a sky set.</summary>
    public const int FileLength = ImageDataOffset + (FrameCount * FrameWidth * FrameHeight);

    private const int FrameLength = FrameWidth * FrameHeight;

    private DaggerfallSkyFile(string name, IReadOnlyList<Palette> palettes, IReadOnlyList<IndexedBitmap> frames)
    {
        Name = name;
        Palettes = palettes;
        Frames = frames;
    }

    /// <summary>Logical file name (e.g. <c>SKY04.DAT</c>).</summary>
    public string Name { get; }

    /// <summary>The 32 time-of-day palettes.</summary>
    public IReadOnlyList<Palette> Palettes { get; }

    /// <summary>All 64 frames: 0-31 west, 32-63 east.</summary>
    public IReadOnlyList<IndexedBitmap> Frames { get; }

    /// <summary>The palette that lights a given frame index (its time-of-day step).</summary>
    public Palette PaletteFor(int frameIndex)
    {
        if ((uint)frameIndex >= FrameCount)
        {
            throw new ArgumentOutOfRangeException(nameof(frameIndex), frameIndex, $"A sky set has {FrameCount} frames.");
        }

        return Palettes[frameIndex % FramesPerHalf];
    }

    /// <summary>One frame of one half: <paramref name="half" /> 0 = west, 1 = east.</summary>
    public IndexedBitmap GetFrame(int half, int step)
    {
        if ((uint)half >= 2 || (uint)step >= FramesPerHalf)
        {
            throw new ArgumentOutOfRangeException(nameof(step), $"Half {half}, step {step} is outside 2x{FramesPerHalf}.");
        }

        return Frames[(half * FramesPerHalf) + step];
    }

    /// <summary>Whether a name follows the SKYnn.DAT convention (two digits — SKYPAL.DAT is not a set).</summary>
    public static bool IsSkyFileName(string fileName)
    {
        return fileName.Length == 9
               && fileName.StartsWith("SKY", StringComparison.OrdinalIgnoreCase)
               && char.IsAsciiDigit(fileName[3])
               && char.IsAsciiDigit(fileName[4])
               && fileName.EndsWith(".DAT", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Parses a sky set.</summary>
    public static DaggerfallSkyFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException(
                $"'{name}' must be exactly {FileLength} bytes ({PaletteCount} palettes + padding + " +
                $"{FrameCount} frames of {FrameWidth}x{FrameHeight}); got {bytes.Length}.");
        }

        var palettes = new List<Palette>(PaletteCount);
        for (var i = 0; i < PaletteCount; i++)
        {
            palettes.Add(Palette.LoadArenaCol(bytes.Slice(i * Palette.ColFileLength, Palette.ColFileLength)));
        }

        var frames = new List<IndexedBitmap>(FrameCount);
        for (var i = 0; i < FrameCount; i++)
        {
            var pixels = bytes.Slice(ImageDataOffset + (i * FrameLength), FrameLength).ToArray();
            frames.Add(new IndexedBitmap(FrameWidth, FrameHeight, pixels));
        }

        return new DaggerfallSkyFile(name, palettes, frames);
    }
}
