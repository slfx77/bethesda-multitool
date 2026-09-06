// Structure ported from RGUnity/redguard-file-exporter (MIT, (c) 2020 Dave Humphrey),
// 3DFileTest/Common/RedguardTexBsiFile.{h,cpp}, taken from master on 2026-09-05.
// See THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's <c>fxart\TEXBSI.###</c> — the 3dfx (Voodoo) texture sets. The Steam release runs
///     the software renderer and ships only <c>3dart</c>, so these reach us through Disc 1's
///     InstallShield cabinet; the software sets beside them are Daggerfall's TEXTURE container and
///     decode through <c>DaggerfallTextureFile</c> instead.
///     <para>
///         A file is a run of IMAGES terminated by a 9-byte all-NUL name. Each image is a 9-byte
///         NUL-padded ASCII name, a LITTLE-endian u32 body size, then subrecords in the Redguard
///         house style — 4-char tag + BIG-endian u32 length + payload — ending with <c>"END "</c>.
///         Exactly two shapes occur across retail: <c>BSIF, BHDR, DATA, END</c> (5,546 images,
///         a single frame with no palette of its own) and <c>IFHD, BHDR, CMAP, DATA, END</c>
///         (56 images carrying a 6-bit VGA palette, 55 of them animated).
///     </para>
///     <para>
///         <c>BHDR</c> is 26 bytes: u16 x @0, y @2, width @4, height @6; two bytes @8; two zero
///         words @10 and @12; <b>u16 frame count @14</b>; u16 @16; two zero words; two bytes; u16. A still image's DATA is exactly
///         width x height 8-bit indices — true on all 5,547 of them. An animated image's DATA
///         opens with a table of <c>height x frameCount</c> LITTLE-endian u32 ROW offsets,
///         relative to the DATA payload, each row being <c>width</c> bytes; frames therefore share
///         identical rows, which is the compression. Verified on all 55 animated images: 32,135
///         rows, every one inside its payload.
///     </para>
///     <para>
///         Two deviations from the reference, both measured against retail. It reads that offset
///         table with <c>fread(data, 1, height * frameCount)</c> into a <c>vector&lt;dword&gt;</c> of
///         the same element count — a quarter of the bytes it needs; the table is dwords, so this
///         reader reads <c>height * frameCount * 4</c>. And it takes the row-table path for every
///         IFHD image, but one retail image (TEXBSI.357's fourth) carries IFHD with a frame count
///         of 1 and a plain <c>width * height</c> DATA, so the frame count decides here instead.
///     </para>
///     <para>
///         Measured 2026-09-05 across all 415 retail files: the image walk tiles every one of them
///         exactly, for 5,602 images and 1,132 distinct sizes from 1x1 up to 256x256.
///     </para>
/// </summary>
internal sealed class RedguardTexBsiFile
{
    /// <summary>Bytes of NUL-padded image name; nine NULs instead terminate the file.</summary>
    public const int NameLength = 9;

    /// <summary>Bytes of subrecord header: a 4-char tag and a big-endian length.</summary>
    public const int SubrecordHeaderLength = 8;

    /// <summary>Bytes in the <c>BHDR</c> image header.</summary>
    public const int ImageHeaderLength = 26;

    /// <summary>Bytes in a <c>CMAP</c> palette: 256 x RGB.</summary>
    public const int PaletteLength = 768;

    private RedguardTexBsiFile(string name, IReadOnlyList<RedguardTexBsiImage> images)
    {
        Name = name;
        Images = images;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The images, in file order.</summary>
    public IReadOnlyList<RedguardTexBsiImage> Images { get; }

    /// <summary>True when the name is a numbered <c>TEXBSI.###</c> set.</summary>
    public static bool IsTexBsiFileName(string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        if (!Path.GetFileNameWithoutExtension(fileName).Equals("TEXBSI", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var extension = Path.GetExtension(fileName);
        return extension.Length == 4 && extension[1..].All(char.IsAsciiDigit);
    }

    /// <summary>Parses a texture set, throwing <see cref="InvalidDataException" /> when it does not tile.</summary>
    public static RedguardTexBsiFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var images = new List<RedguardTexBsiImage>();
        var position = 0;
        while (position < bytes.Length)
        {
            if (position + NameLength > bytes.Length)
            {
                throw new InvalidDataException($"{name}: {bytes.Length - position} trailing bytes at {position} are not an image name.");
            }

            if (!bytes.Slice(position, NameLength).ContainsAnyExcept((byte)0))
            {
                // Nine NULs end the file; retail leaves nothing after them.
                break;
            }

            images.Add(ReadImage(bytes, name, images.Count, ref position));
        }

        return new RedguardTexBsiFile(name, images);
    }

    private static RedguardTexBsiImage ReadImage(ReadOnlySpan<byte> bytes, string fileName, int index, ref int position)
    {
        var imageName = ReadName(bytes.Slice(position, NameLength));
        if (position + NameLength + 4 > bytes.Length)
        {
            throw new InvalidDataException($"{fileName}: image {index} has no body size.");
        }

        var bodySize = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(position + NameLength)..]);
        var body = position + NameLength + 4;
        if (bodySize > int.MaxValue || body + (long)bodySize > bytes.Length)
        {
            throw new InvalidDataException($"{fileName}: image {index} declares {bodySize} bytes, past the end of the file.");
        }

        var end = body + (int)bodySize;
        var at = body;
        var animated = false;
        ReadOnlySpan<byte> header = default;
        ReadOnlySpan<byte> data = default;
        Palette? palette = null;

        while (at < end)
        {
            if (at + SubrecordHeaderLength > end)
            {
                throw new InvalidDataException($"{fileName}: image {index} has a truncated subrecord at {at}.");
            }

            var tag = bytes.Slice(at, 4);
            var length = BinaryPrimitives.ReadUInt32BigEndian(bytes[(at + 4)..]);
            at += SubrecordHeaderLength;
            if (length > int.MaxValue || at + (long)length > end)
            {
                throw new InvalidDataException($"{fileName}: image {index} subrecord at {at - SubrecordHeaderLength} declares {length} bytes, past its body.");
            }

            var payload = bytes.Slice(at, (int)length);
            if (tag.SequenceEqual("IFHD"u8))
            {
                animated = true;
            }
            else if (tag.SequenceEqual("BHDR"u8))
            {
                if (payload.Length < ImageHeaderLength)
                {
                    throw new InvalidDataException($"{fileName}: image {index} BHDR is {payload.Length} bytes, expected {ImageHeaderLength}.");
                }

                header = payload;
            }
            else if (tag.SequenceEqual("CMAP"u8))
            {
                if (payload.Length != PaletteLength)
                {
                    throw new InvalidDataException($"{fileName}: image {index} CMAP is {payload.Length} bytes, expected {PaletteLength}.");
                }

                // Retail CMAPs top out at 63, so they are 6-bit VGA like the .GXA palettes.
                palette = Palette.FromVga6Bit(payload);
            }
            else if (tag.SequenceEqual("DATA"u8))
            {
                data = payload;
            }

            at += (int)length;
        }

        if (at != end)
        {
            throw new InvalidDataException($"{fileName}: image {index} subrecords end at {at} of {end}.");
        }

        if (header.IsEmpty)
        {
            throw new InvalidDataException($"{fileName}: image {index} has no BHDR.");
        }

        position = end;
        var xOffset = BinaryPrimitives.ReadUInt16LittleEndian(header);
        var yOffset = BinaryPrimitives.ReadUInt16LittleEndian(header[2..]);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(header[6..]);
        var frameCount = BinaryPrimitives.ReadUInt16LittleEndian(header[14..]);

        // The reference dispatches on IFHD alone, but TEXBSI.357's fourth image carries IFHD with
        // a frame count of 1 and a plain width x height DATA — reading that as a row table walks
        // pixels as offsets. The frame count decides here.
        var frames = animated && frameCount > 1
            ? ReadAnimatedFrames(data, fileName, index, width, height, frameCount)
            : [ReadStillFrame(data, fileName, index, width, height)];

        // IsAnimated means "has more than one frame", not "carried an IFHD tag" — TEXBSI.357's
        // fourth image has the tag and a single frame.
        return new RedguardTexBsiImage(
            imageName, xOffset, yOffset, width, height, frameCount, animated && frameCount > 1, palette, frames);
    }

    private static IndexedBitmap ReadStillFrame(ReadOnlySpan<byte> data, string fileName, int index, int width, int height)
    {
        var pixels = (long)width * height;
        if (data.Length != pixels)
        {
            throw new InvalidDataException($"{fileName}: image {index} is {width}x{height} but its DATA is {data.Length} bytes.");
        }

        return new IndexedBitmap(width, height, data.ToArray());
    }

    private static IndexedBitmap[] ReadAnimatedFrames(
        ReadOnlySpan<byte> data, string fileName, int index, int width, int height, int frameCount)
    {
        if (frameCount <= 0 || width <= 0 || height <= 0)
        {
            throw new InvalidDataException($"{fileName}: image {index} is animated but {width}x{height} with {frameCount} frames.");
        }

        var rows = (long)height * frameCount;
        if (rows * 4 > data.Length)
        {
            throw new InvalidDataException($"{fileName}: image {index} needs a {rows * 4}-byte row table but its DATA is {data.Length} bytes.");
        }

        var frames = new IndexedBitmap[frameCount];
        for (var f = 0; f < frameCount; f++)
        {
            var pixels = new byte[width * height];
            for (var y = 0; y < height; y++)
            {
                var offset = BinaryPrimitives.ReadUInt32LittleEndian(data[(4 * (height * f + y))..]);
                if (offset + (long)width > data.Length)
                {
                    throw new InvalidDataException(
                        $"{fileName}: image {index} frame {f} row {y} starts at {offset}, past its {data.Length}-byte DATA.");
                }

                data.Slice((int)offset, width).CopyTo(pixels.AsSpan(y * width));
            }

            frames[f] = new IndexedBitmap(width, height, pixels);
        }

        return frames;
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        var end = raw.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? raw : raw[..end]);
    }
}

/// <summary>
///     One TEXBSI image. <see cref="Palette" /> is present only on the IFHD form; the still images
///     share a palette supplied from outside — a <c>.COL</c> beside the set.
///     <see cref="IsAnimated" /> means more than one frame, which is 55 of the 56 IFHD images.
/// </summary>
internal sealed record RedguardTexBsiImage(
    string Name,
    int XOffset,
    int YOffset,
    int Width,
    int Height,
    int FrameCount,
    bool IsAnimated,
    Palette? Palette,
    IReadOnlyList<IndexedBitmap> Frames);
