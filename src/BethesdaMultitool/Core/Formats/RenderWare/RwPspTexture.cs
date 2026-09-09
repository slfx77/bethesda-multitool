using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Dds;

namespace BethesdaMultitool.Core.Formats.RenderWare;

/// <summary>PSP GE pixel formats, as stored at <see cref="RwPspTexture.FormatOffset" />.</summary>
internal enum RwPspPixelFormat
{
    /// <summary>16-bit 5:6:5.</summary>
    Rgb565 = 0,

    /// <summary>16-bit 5:5:5 with one alpha bit.</summary>
    Rgba5551 = 1,

    /// <summary>16-bit 4:4:4:4.</summary>
    Rgba4444 = 2,

    /// <summary>32-bit 8:8:8:8.</summary>
    Rgba8888 = 3,

    /// <summary>4-bit indexed into a 16-entry CLUT.</summary>
    Indexed4 = 4,

    /// <summary>8-bit indexed into a 256-entry CLUT.</summary>
    Indexed8 = 5
}

/// <summary>
///     The PSP raster inside a RenderWare <c>TEXTURENATIVE</c> chunk.
///     <para>
///         ⚑
///         <b>
///             Layout settled by measurement 2026-09-06 and it predicts the exact body length of
///             5,067 of 5,067 textures.
///         </b>
///         This is the "genuinely new decode" the porting plan
///         identified: the sibling tool's <c>TEXTURENATIVE</c> path is PS2 from the first byte of
///         the raster header — PSMT4/8, CSM1 CLUT swizzle, GS block unswizzle — and none of it
///         applies here. Only the chunk envelope was reusable.
///     </para>
///     <code>
///     +0x04  u16 width, u16 height   width in the LOW half, height in the HIGH half
///     +0x5C  u32 pixel format        the PSP GE enum (RwPspPixelFormat)
///     +0x6C  char[64] name           FIXED length, NUL padded
///     +0xAC  CLUT                    32-bit RGBA; 16 entries for Indexed4, 256 for Indexed8
///            pixels                  rows padded to max(16, align16(width * bpp / 8))
///     </code>
///     <para>
///         ⚠ <b>The dimensions are LINEAR u16, not log2.</b> An earlier search of this header for a
///         log2 pair found nothing and the rasters were briefly recorded as unsolvable — the answer
///         was inside the searched bytes the whole time, in an encoding that search never tried.
///     </para>
///     <para>
///         ⚠ <b>The row pitch has a 16-byte FLOOR.</b> Below 32 pixels at 4bpp every width shares
///         pitch 16, so the byte total alone cannot recover a width — which is why the dimension
///         field matters rather than being a convenience.
///     </para>
/// </summary>
internal sealed class RwPspTexture
{
    /// <summary>Offset of the packed width/height word.</summary>
    public const int DimensionsOffset = 0x04;

    /// <summary>Offset of the pixel-format word.</summary>
    public const int FormatOffset = 0x5C;

    /// <summary>Offset of the fixed-length name field.</summary>
    public const int NameOffset = 0x6C;

    /// <summary>Bytes in the name field.</summary>
    public const int NameLength = 0x40;

    /// <summary>Offset of the CLUT — <see cref="NameOffset" /> plus <see cref="NameLength" />.</summary>
    public const int ClutOffset = NameOffset + NameLength;

    /// <summary>Row pitch alignment, and also its minimum.</summary>
    public const int PitchAlignment = 16;

    private RwPspTexture(string name, int width, int height, RwPspPixelFormat format, int mipCount, byte[] rgba)
    {
        Name = name;
        Width = width;
        Height = height;
        Format = format;
        MipCount = mipCount;
        Rgba = rgba;
    }

    /// <summary>The texture's name, e.g. <c>SFX_Skydome_1_lava</c>.</summary>
    public string Name { get; }

    /// <summary>Width in pixels.</summary>
    public int Width { get; }

    /// <summary>Height in pixels.</summary>
    public int Height { get; }

    /// <summary>The stored pixel format.</summary>
    public RwPspPixelFormat Format { get; }

    /// <summary>Levels in the mip chain; 1 for all but fifteen retail textures.</summary>
    public int MipCount { get; }

    /// <summary>The base level decoded to RGBA, row-major, four bytes per pixel.</summary>
    public byte[] Rgba { get; }

    /// <summary>Bits per pixel for a format.</summary>
    public static int BitsPerPixel(RwPspPixelFormat format)
    {
        return format switch
        {
            RwPspPixelFormat.Indexed4 => 4,
            RwPspPixelFormat.Indexed8 => 8,
            RwPspPixelFormat.Rgba8888 => 32,
            _ => 16
        };
    }

    /// <summary>CLUT entries a format uses, or 0 when it is not indexed.</summary>
    public static int ClutEntries(RwPspPixelFormat format)
    {
        return format switch
        {
            RwPspPixelFormat.Indexed4 => 16,
            RwPspPixelFormat.Indexed8 => 256,
            _ => 0
        };
    }

    /// <summary>
    ///     Bytes per row: the width's bit length rounded up to <see cref="PitchAlignment" />, with
    ///     that same value as a floor.
    /// </summary>
    public static int Pitch(int width, RwPspPixelFormat format)
    {
        var bytes = width * BitsPerPixel(format) / 8;
        var aligned = (bytes + PitchAlignment - 1) / PitchAlignment * PitchAlignment;
        return Math.Max(PitchAlignment, aligned);
    }

    /// <summary>Bytes a mip chain of <paramref name="mipCount" /> levels occupies.</summary>
    public static long PixelBytes(int width, int height, RwPspPixelFormat format, int mipCount)
    {
        long total = 0;
        for (var level = 0; level < Math.Max(1, mipCount); level++)
        {
            total += (long)Pitch(Math.Max(1, width >> level), format) * Math.Max(1, height >> level);
        }

        return total;
    }

    /// <summary>
    ///     Parses a TEXTURENATIVE Struct body, decoding the base level to RGBA. Returns null when
    ///     the bytes do not fit the layout — including when no mip count reproduces the body length
    ///     exactly, which is the check that keeps a mis-located chunk from decoding as noise.
    /// </summary>
    public static RwPspTexture? TryParse(ReadOnlySpan<byte> body)
    {
        if (body.Length <= ClutOffset)
        {
            return null;
        }

        var packed = BinaryPrimitives.ReadUInt32LittleEndian(body[DimensionsOffset..]);
        var width = (int)(packed & 0xFFFF);
        var height = (int)((packed >> 16) & 0xFFFF);
        var format = (RwPspPixelFormat)BinaryPrimitives.ReadUInt32LittleEndian(body[FormatOffset..]);

        if (width <= 0 || height <= 0 || width > 4096 || height > 4096 || !Enum.IsDefined(format))
        {
            return null;
        }

        var clutBytes = ClutEntries(format) * 4;
        var pixelStart = ClutOffset + clutBytes;
        if (pixelStart >= body.Length)
        {
            return null;
        }

        var available = body.Length - pixelStart;
        var mipCount = 0;
        for (var levels = 1; levels <= 12; levels++)
        {
            if (PixelBytes(width, height, format, levels) == available)
            {
                mipCount = levels;
                break;
            }
        }

        if (mipCount == 0)
        {
            return null;
        }

        var name = RwChunk.ReadString(body, NameOffset, NameLength);
        var clut = clutBytes > 0 ? body.Slice(ClutOffset, clutBytes) : default;
        var rgba = Decode(body[pixelStart..], width, height, format, clut);
        return new RwPspTexture(name, width, height, format, mipCount, rgba);
    }

    /// <summary>Decodes the base mip level into RGBA.</summary>
    private static byte[] Decode(
        ReadOnlySpan<byte> pixels, int width, int height, RwPspPixelFormat format, ReadOnlySpan<byte> clut)
    {
        var rgba = new byte[width * height * 4];
        var pitch = Pitch(width, format);

        for (var y = 0; y < height; y++)
        {
            var row = pixels[(y * pitch)..];
            for (var x = 0; x < width; x++)
            {
                var target = (y * width + x) * 4;
                switch (format)
                {
                    case RwPspPixelFormat.Indexed4:
                        // Two texels per byte, LOW nibble first.
                        var pair = row[x / 2];
                        WriteClut(rgba, target, clut, (x & 1) == 0 ? pair & 0x0F : pair >> 4);
                        break;

                    case RwPspPixelFormat.Indexed8:
                        WriteClut(rgba, target, clut, row[x]);
                        break;

                    case RwPspPixelFormat.Rgba8888:
                        row.Slice(x * 4, 4).CopyTo(rgba.AsSpan(target));
                        break;

                    default:
                        WriteSixteenBit(
                            rgba, target, BinaryPrimitives.ReadUInt16LittleEndian(row[(x * 2)..]), format);
                        break;
                }
            }
        }

        return rgba;
    }

    private static void WriteClut(byte[] rgba, int target, ReadOnlySpan<byte> clut, int index)
    {
        var at = index * 4;
        if (at + 4 > clut.Length)
        {
            return;
        }

        clut.Slice(at, 4).CopyTo(rgba.AsSpan(target));
    }

    private static void WriteSixteenBit(byte[] rgba, int target, ushort texel, RwPspPixelFormat format)
    {
        switch (format)
        {
            case RwPspPixelFormat.Rgb565:
                rgba[target] = Expand((texel & 0x1F) << 3, 5);
                rgba[target + 1] = Expand(((texel >> 5) & 0x3F) << 2, 6);
                rgba[target + 2] = Expand(((texel >> 11) & 0x1F) << 3, 5);
                rgba[target + 3] = 255;
                break;

            case RwPspPixelFormat.Rgba5551:
                rgba[target] = Expand((texel & 0x1F) << 3, 5);
                rgba[target + 1] = Expand(((texel >> 5) & 0x1F) << 3, 5);
                rgba[target + 2] = Expand(((texel >> 10) & 0x1F) << 3, 5);
                rgba[target + 3] = (texel & 0x8000) != 0 ? (byte)255 : (byte)0;
                break;

            default:
                rgba[target] = (byte)((texel & 0x0F) * 17);
                rgba[target + 1] = (byte)(((texel >> 4) & 0x0F) * 17);
                rgba[target + 2] = (byte)(((texel >> 8) & 0x0F) * 17);
                rgba[target + 3] = (byte)(((texel >> 12) & 0x0F) * 17);
                break;
        }
    }

    /// <summary>Replicates the high bits into the low ones so full-scale values reach 255.</summary>
    private static byte Expand(int value, int bits)
    {
        return (byte)(value | (value >> bits));
    }

    /// <summary>The base level as a <see cref="DecodedTexture" /> for the viewer and browser.</summary>
    public DecodedTexture ToDecodedTexture()
    {
        return DecodedTexture.FromBaseLevel(Rgba, Width, Height, false);
    }
}
