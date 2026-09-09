using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.VanBuren;

/// <summary>
///     The uncompressed image payload from the cancelled Van Buren (Fallout 3) prototype's
///     <c>.grp</c> archives — the game's textures. Original RE 2026-09-06; <c>kran27/VanBurenTools</c>
///     is GPL and was not consulted for code.
///     <para>
///         Little-endian and UNTAGGED — this is the largest of the archives' nameless payload
///         families (1,526 of the 7,044 entries), which is why it went unidentified while the
///         tagged families (<c>B3D</c>, <c>EEN2</c>, <c>RIFF</c>, <c>8TRE</c>) were read first.
///         <c>+0</c> is a u32 format id <c>0x00020000</c>, <c>+4</c> and <c>+8</c> are zero,
///         <c>+12</c> is a u16 WIDTH and <c>+14</c> a u16 HEIGHT, <c>+16</c> is BITS PER PIXEL,
///         and the pixels begin at <c>+18</c>.
///     </para>
///     <para>
///         ⚑ <b>The identification is arithmetic and then visual.</b> Bits per pixel is exactly 24
///         or 32 across all 1,526, every dimension is a power of two (256×256 on 625, 64×64 on 402,
///         32×32 on 188, 128×128 on 81), and <c>size − 18 − width*height*(bpp/8)</c> is 0, 26 or
///         521 — never an arbitrary remainder. Decoded and rendered, they are plainly game
///         textures: a <c>Critters.grp</c> 256×256 comes out as a character atlas of armour plates
///         and mechanical parts.
///     </para>
///     <para>
///         ⚠ Channel order is <b>BGR</b>, not RGB. ⚠ Many images carry a 26-byte TRAILER after the
///         pixels (939 of 1,526, concentrated in the sprite and interface archives, while the
///         <c>Maps.grp</c> images have none) — so the payload length is NOT
///         <c>18 + w*h*bpp/8</c> in general and must not be used to derive the dimensions.
///     </para>
///     <para>
///         ⚠ A sibling family with format id <c>0x000A0000</c> holds 85 payloads whose size does
///         NOT satisfy the relation, so those are compressed — consistent with the <c>.rle</c>
///         names the <c>EMAP</c> manifests carry beside their <c>.8</c> ones. Not decoded here.
///     </para>
/// </summary>
internal sealed class VanBurenImageFile
{
    /// <summary>The format id of an uncompressed image.</summary>
    public const uint UncompressedFormat = 0x0002_0000;

    /// <summary>The format id of the compressed sibling family. ⚠ NOT decoded.</summary>
    public const uint CompressedFormat = 0x000A_0000;

    /// <summary>Bytes before the pixel data.</summary>
    public const int HeaderLength = 18;

    private VanBurenImageFile(string name, int width, int height, int bitsPerPixel, int pixelOffset, int trailerLength)
    {
        Name = name;
        Width = width;
        Height = height;
        BitsPerPixel = bitsPerPixel;
        PixelOffset = pixelOffset;
        TrailerLength = trailerLength;
    }

    /// <summary>Source name, for messages.</summary>
    public string Name { get; }

    /// <summary>Image width in pixels.</summary>
    public int Width { get; }

    /// <summary>Image height in pixels.</summary>
    public int Height { get; }

    /// <summary>24 or 32 on every shipped image.</summary>
    public int BitsPerPixel { get; }

    /// <summary>Byte offset of the pixel data.</summary>
    public int PixelOffset { get; }

    /// <summary>Bytes after the pixels — 0 or 26 on the shipped corpus.</summary>
    public int TrailerLength { get; }

    /// <summary>Bytes of pixel data.</summary>
    public int PixelLength => Width * Height * (BitsPerPixel / 8);

    /// <summary>Content probe: the format id plus a header that accounts for the payload.</summary>
    public static bool IsImage(ReadOnlySpan<byte> bytes)
    {
        return TryParse(bytes, "probe", out _, out _);
    }

    /// <summary>Parses the header, throwing <see cref="InvalidDataException" /> when it does not fit.</summary>
    public static VanBurenImageFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        if (!TryParse(bytes, name, out var image, out var error))
        {
            throw new InvalidDataException(error);
        }

        return image;
    }

    /// <summary>Parses the header, reporting why rather than throwing.</summary>
    public static bool TryParse(ReadOnlySpan<byte> bytes, string name, out VanBurenImageFile image, out string error)
    {
        image = null!;
        if (bytes.Length < HeaderLength)
        {
            error = $"{name}: {bytes.Length} bytes is shorter than the {HeaderLength}-byte header.";
            return false;
        }

        if (BinaryPrimitives.ReadUInt32LittleEndian(bytes) != UncompressedFormat)
        {
            error = $"{name}: the format id is not 0x{UncompressedFormat:X8}.";
            return false;
        }

        var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[14..]);
        var bitsPerPixel = bytes[16];

        // Only these two occur, and requiring them keeps a payload that merely opens with the same
        // dword from yielding an enormous bogus image.
        if (bitsPerPixel is not (24 or 32) || width == 0 || height == 0)
        {
            error = $"{name}: {width}x{height} at {bitsPerPixel} bpp is not a shipped image shape.";
            return false;
        }

        var pixels = (long)width * height * (bitsPerPixel / 8);
        if (HeaderLength + pixels > bytes.Length)
        {
            error = $"{name}: {width}x{height} at {bitsPerPixel} bpp needs more than {bytes.Length} bytes.";
            return false;
        }

        image = new VanBurenImageFile(name, width, height, bitsPerPixel, HeaderLength,
            bytes.Length - HeaderLength - (int)pixels);
        error = string.Empty;
        return true;
    }

    /// <summary>
    ///     Decodes to 24-bit RGB, top row first.
    ///     <para>⚠ The stored channel order is BGR; this returns RGB.</para>
    /// </summary>
    public static byte[] DecodeRgb(ReadOnlySpan<byte> bytes, VanBurenImageFile image)
    {
        ArgumentNullException.ThrowIfNull(image);

        var step = image.BitsPerPixel / 8;
        var rgb = new byte[image.Width * image.Height * 3];
        var source = bytes.Slice(image.PixelOffset, image.PixelLength);

        for (int pixel = 0, at = 0; at + step <= source.Length; pixel += 3, at += step)
        {
            rgb[pixel] = source[at + 2];
            rgb[pixel + 1] = source[at + 1];
            rgb[pixel + 2] = source[at];
        }

        return rgb;
    }
}
