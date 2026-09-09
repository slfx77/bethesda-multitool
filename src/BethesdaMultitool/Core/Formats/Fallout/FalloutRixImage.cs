using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Fallout;

/// <summary>
///     A Fallout <c>.RIX</c> splash image — the full-screen plates the game shows on startup.
///     <para>
///         ⚠⚠ <b>This is the one LITTLE-endian format in Fallout's line-up.</b> Everything else here
///         — FRM, PRO, MAP, DAT1 — is big-endian, so the instinct to reach for a BE read is exactly
///         wrong. It is also self-evident when it happens: the dimensions come out as 32770 x 57345
///         instead of 640 x 480.
///     </para>
///     <para>
///         Layout, and the arithmetic is exact: <c>"RIX3"</c> magic, LE u16 width, LE u16 height, a
///         two-byte field, then a 768-byte palette, then <c>width * height</c> 8-bit indices.
///         <c>10 + 768 + 640*480 == 307,978</c>, which is the size of all five retail plates to the
///         byte.
///     </para>
///     <para>
///         ⚑ The palette is <b>6-bit VGA</b> (no component exceeds 63), like Fallout's other
///         palettes, so it goes through <see cref="Imaging.Palette.FromVga6Bit" />. Reading it as 8-bit
///         renders the splash four times too dark — the same trap <c>COLOR.PAL</c> sets.
///     </para>
/// </summary>
internal sealed class FalloutRixImage
{
    /// <summary>The four-byte magic every plate carries.</summary>
    public const string Magic = "RIX3";

    /// <summary>Bytes before the palette.</summary>
    public const int HeaderLength = 10;

    private FalloutRixImage(string name, IndexedBitmap bitmap, Palette palette, ushort unknown)
    {
        Name = name;
        Bitmap = bitmap;
        Palette = palette;
        Unknown = unknown;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The image indices with their dimensions.</summary>
    public IndexedBitmap Bitmap { get; }

    /// <summary>The plate's own palette, promoted from 6-bit VGA.</summary>
    public Palette Palette { get; }

    /// <summary>The u16 at +8; constant 0x00AF on retail and not interpreted.</summary>
    public ushort Unknown { get; }

    /// <summary>Content probe: the magic.</summary>
    public static bool IsRixImage(ReadOnlySpan<byte> bytes)
    {
        return bytes.Length >= HeaderLength && bytes[..4].SequenceEqual(Encoding.ASCII.GetBytes(Magic));
    }

    /// <summary>Parses a plate, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static FalloutRixImage Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var image, out var error))
        {
            throw new InvalidDataException(error);
        }

        return image;
    }

    /// <summary>Parses a plate, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out FalloutRixImage image, out string error)
    {
        image = null!;
        if (!IsRixImage(bytes))
        {
            error = $"{name}: does not open with the '{Magic}' magic.";
            return false;
        }

        // ⚠ LITTLE-endian, alone among Fallout's formats.
        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var unknown = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        if (width == 0 || height == 0)
        {
            error = $"{name}: declares {width}x{height}.";
            return false;
        }

        var required = HeaderLength + Palette.RgbByteCount + (long)width * height;
        if (bytes.Length != required)
        {
            error = $"{name}: {width}x{height} needs exactly {required} bytes, not {bytes.Length}.";
            return false;
        }

        var palette = Palette.FromVga6Bit(bytes.Slice(HeaderLength, Palette.RgbByteCount));
        var indices = bytes[(HeaderLength + Palette.RgbByteCount)..].ToArray();

        image = new FalloutRixImage(name, new IndexedBitmap(width, height, indices), palette, unknown);
        error = string.Empty;
        return true;
    }
}
