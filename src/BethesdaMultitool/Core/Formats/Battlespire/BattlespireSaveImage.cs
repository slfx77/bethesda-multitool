using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Geometry;

namespace BethesdaMultitool.Core.Formats.Battlespire;

/// <summary>
///     A Battlespire save's <c>IMAGE.RAW</c>: the 80 x 50 thumbnail the load menu shows, headerless,
///     one little-endian u16 per pixel, row-major with an 80-pixel stride, in <b>x555</b> — bit 15
///     unused, R in bits 10-14, G in 5-9, B in 0-4 (the VESA 15-bit layout the game's PATCH.TXT names
///     as its video mode). Original RE 2026-09-07 against the SAVE0 fixture.
///     <para>
///         ⚑ <b>Measured, and each alternative refuted by a control that could discriminate.</b>
///         8,000 bytes = 80 x 50 x 2. The stride is 80 by autocorrelation of the mean pixel
///         difference: lag 80 gives 2,073, the minimum over lags 49..160 (lag 50 = 3,853, lag 100 =
///         3,571). Under 555 the channels correlate the way a photograph's do — r-g 0.881, g-b 0.889,
///         r-b 0.684; under RGB565 they fall to 0.058 / 0.245, so 565 is refuted. Bit 15 is set on
///         0/4,000 pixels and bit 0 on 1,804/4,000, so this is NOT <see cref="BsiFile" />'s HICL packing
///         (which leaves bit 0 spare) — reusing that expansion here would be wrong.
///     </para>
///     <para>
///         ⚠⚠ <b>Channel ORDER is settled by the SCENE, not by the HUD strip.</b> Correlation is
///         symmetric and cannot separate RGB from BGR; the render can, but only where the pixels are
///         warm. Under this reading the whole image is red-dominant — ΣR 21,750 vs ΣB 14,522, R&gt;B
///         on 2,913 pixels against B&gt;R on 273 — the dungeon floor of row 35 is R&gt;B on 76 of 80
///         pixels and B&gt;R on none, and the brightest pixel, (75, 3), is (31, 31, 14): a warm lamp
///         that BGR would turn into (14, 31, 31) cyan above a blue floor. ⛔ Do NOT cite the HUD
///         strip, as an earlier revision of this comment did ("brass HUD ... BGR shows a blue HUD"):
///         the bottom rows are close to channel-neutral and, counted, lean the OTHER way — rows
///         44..49 are B&gt;R on 52 / 37 / 41 / 39 / 51 / 53 pixels against R&gt;B on 6 / 23 / 22 /
///         27 / 16 / 12. Read literally that sentence selects BGR; it is the floor, the lamp and the
///         image-wide sums that refute it.
///     </para>
///     <para>
///         Pixel 0 of the fixture is 0x10A4 = (R 4, G 5, B 4) in 5-bit components.
///     </para>
/// </summary>
internal sealed class BattlespireSaveImage
{
    public const int Width = 80;
    public const int Height = 50;

    /// <summary>Bytes in the file: 80 x 50 x 2.</summary>
    public const int FileLength = Width * Height * 2;

    private BattlespireSaveImage(ushort[] pixels)
    {
        Pixels = pixels;
    }

    /// <summary>The raw x555 words, row-major.</summary>
    public IReadOnlyList<ushort> Pixels { get; }

    /// <summary>Content probe: the exact length.</summary>
    public static bool IsSaveImage(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length == FileLength;
    }

    /// <summary>Parses the file, throwing <see cref="InvalidDataException" /> when it is not 8,000 bytes.</summary>
    public static BattlespireSaveImage Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (bytes.Length != FileLength)
        {
            throw new InvalidDataException(
                $"{name}: IMAGE.RAW is {FileLength} bytes (80 x 50 x u16), this one is {bytes.Length}.");
        }

        var pixels = new ushort[Width * Height];
        for (var i = 0; i < pixels.Length; i++)
        {
            pixels[i] = BinaryPrimitives.ReadUInt16LittleEndian(bytes[(i * 2)..]);
        }

        return new BattlespireSaveImage(pixels);
    }

    /// <summary>The 5-bit components of an x555 word: R from bits 10-14, G from 5-9, B from 0-4.</summary>
    public static (int R, int G, int B) Split(ushort pixel)
    {
        return ((pixel >> 10) & 0x1F, (pixel >> 5) & 0x1F, pixel & 0x1F);
    }

    /// <summary>A 5-bit component widened to 8 bits by replicating its top bits (0 -> 0, 31 -> 255).</summary>
    public static byte Widen(int component)
    {
        return (byte)((component << 3) | (component >> 2));
    }

    /// <summary>The pixel at a grid position, as its raw word.</summary>
    public ushort PixelAt(int column, int row)
    {
        return Pixels[row * Width + column];
    }

    /// <summary>The image as opaque 8-bit RGBA, row-major.</summary>
    public byte[] ToRgba()
    {
        var rgba = new byte[Width * Height * 4];
        for (var i = 0; i < Pixels.Count; i++)
        {
            var (r, g, b) = Split(Pixels[i]);
            rgba[i * 4] = Widen(r);
            rgba[i * 4 + 1] = Widen(g);
            rgba[i * 4 + 2] = Widen(b);
            rgba[i * 4 + 3] = 255;
        }

        return rgba;
    }

    /// <summary>Encodes the thumbnail as a PNG.</summary>
    public byte[] EncodePng()
    {
        return PngWriter.EncodeRgba(ToRgba(), Width, Height);
    }

    /// <summary>Writes the thumbnail as a PNG to <paramref name="path" />.</summary>
    public void SavePng(string path)
    {
        PngWriter.SaveRgba(ToRgba(), Width, Height, path);
    }
}
