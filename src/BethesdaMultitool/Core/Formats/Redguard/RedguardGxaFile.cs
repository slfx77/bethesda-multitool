using System.Buffers.Binary;
using System.Text;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Redguard;

/// <summary>
///     Redguard's <c>.GXA</c> image container — menus, HUD art, inventory backdrops and the
///     full-screen location paintings. Original RE (2026-09-04); the MIT exporter carries readers
///     for <c>.3D</c>, <c>.ROB</c> and TEXBSI only, and none for this.
///     <para>
///         IFF-style and mixed-endian, the same shape as <see cref="RedguardRobParser" />: a 4-char
///         tag, a BIG-endian u32 length, then the payload, ending in a 4-byte <c>"END "</c>. Every
///         retail file is exactly <c>BMHD</c>, <c>BPAL</c>, <c>BBMP</c> in that order.
///     </para>
///     <list type="bullet">
///         <item>
///             <c>BMHD</c> (34 bytes): a 32-character ASCII banner — always "GXlib image
///             conversion" — then a little-endian u16 <b>image count</b>.
///         </item>
///         <item>
///             <c>BPAL</c> (768 bytes): 256 x RGB. Components top out at 63 on every retail file,
///             so it is 6-bit VGA and goes through <see cref="Imaging.Palette.FromVga6Bit" />.
///         </item>
///         <item>
///             <c>BBMP</c>: that many frame records back to back, each an 18-byte header
///             (u16 form, u16 width, u16 height, then 12 bytes that are zero except a form byte at
///             +10) followed by width x height 8-bit palette indices.
///         </item>
///     </list>
///     <para>
///         Verified 2026-09-05 across all 65 retail files: <b>60 tile exactly</b> under that walk,
///         for 320 frames — 640x480 location art down to 15x15 icons. The remaining 5
///         (GXICONS, INVBACK, INVMASK, pickblob, snuff) declare frames whose pixels do not fit
///         their dimensions, so those carry a compressed frame form that is not decoded here; the
///         header's +10 byte is non-zero on exactly the frames that do it. Such a file reports
///         <see cref="CompressedFrames" /> rather than returning wrong pixels. Two files
///         (GXICONS and gui) store BBMP's length as the absolute offset of the terminator rather
///         than a payload length; the chunk reader accepts exactly that value and nothing looser,
///         and with it honoured gui's 25 frames are plain raw pixels.
///     </para>
/// </summary>
internal sealed class RedguardGxaFile
{
    /// <summary>Bytes in a chunk header: a 4-char tag and a big-endian length.</summary>
    public const int ChunkHeaderLength = 8;

    /// <summary>Bytes in a frame header, before its pixels.</summary>
    public const int FrameHeaderLength = 18;

    /// <summary>Bytes of <c>"END "</c> terminator after the last chunk (63 of 65 retail files carry it).</summary>
    public const int TerminatorLength = 4;

    /// <summary>Bytes in the <c>BMHD</c> chunk: a 32-character banner plus the u16 image count.</summary>
    public const int ImageHeaderLength = 34;

    /// <summary>Characters of ASCII banner at the start of <c>BMHD</c>.</summary>
    public const int BannerLength = 32;

    /// <summary>The banner every retail file carries.</summary>
    public const string Banner = "GXlib image conversion";

    private RedguardGxaFile(string name, Palette palette, IReadOnlyList<IndexedBitmap> frames, int compressedFrames)
    {
        Name = name;
        Palette = palette;
        Frames = frames;
        CompressedFrames = compressedFrames;
    }

    /// <summary>Source file name, for messages.</summary>
    public string Name { get; }

    /// <summary>The file's own palette, promoted from 6-bit VGA.</summary>
    public Palette Palette { get; }

    /// <summary>The decoded frames, in file order.</summary>
    public IReadOnlyList<IndexedBitmap> Frames { get; }

    /// <summary>
    ///     Frames skipped because they use the undecoded compressed form. Non-zero only for the five
    ///     retail files named in the type remarks; a caller should surface it rather than treat the
    ///     decoded subset as the whole file.
    /// </summary>
    public int CompressedFrames { get; }

    /// <summary>Content probe: the chunk walk must reach the tags a GXA declares.</summary>
    public static bool TryProbe(ReadOnlySpan<byte> bytes)
    {
        return TryReadChunk(bytes, 0, out var tag, out _, out _) && tag == "BMHD";
    }

    /// <summary>Parses a GXA, throwing <see cref="InvalidDataException" /> when it does not walk.</summary>
    public static RedguardGxaFile Parse(ReadOnlySpan<byte> bytes, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        var (headerOffset, headerLength) = RequireChunk(bytes, 0, "BMHD", name);
        if (headerLength < ImageHeaderLength)
        {
            throw new InvalidDataException(
                $"{name}: BMHD is {headerLength} bytes, expected at least {ImageHeaderLength}.");
        }

        int count = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(headerOffset + BannerLength, 2));

        var (paletteOffset, paletteLength) = RequireChunk(bytes, headerOffset + headerLength, "BPAL", name);
        if (paletteLength != Palette.RgbByteCount)
        {
            throw new InvalidDataException($"{name}: BPAL is {paletteLength} bytes, expected {Palette.RgbByteCount}.");
        }

        var palette = Palette.FromVga6Bit(bytes.Slice(paletteOffset, paletteLength));

        var (pixelOffset, pixelLength) = RequireChunk(bytes, paletteOffset + paletteLength, "BBMP", name);
        var end = pixelOffset + pixelLength;

        var frames = new List<IndexedBitmap>(count);
        var compressed = 0;
        var position = pixelOffset;
        for (var i = 0; i < count; i++)
        {
            if (position + FrameHeaderLength > end)
            {
                compressed += count - i;
                break;
            }

            var width = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(position + 2, 2));
            var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes.Slice(position + 4, 2));
            var form = bytes[position + 10];
            var pixels = (long)width * height;

            // A compressed frame declares dimensions its payload cannot hold. Stop rather than
            // walk into the next frame's bytes and report absurd sizes for everything after it.
            if (form != 0 || pixels == 0 || position + FrameHeaderLength + pixels > end)
            {
                compressed += count - i;
                break;
            }

            frames.Add(new IndexedBitmap(
                width, height, bytes.Slice(position + FrameHeaderLength, (int)pixels).ToArray()));
            position += FrameHeaderLength + (int)pixels;
        }

        return new RedguardGxaFile(name, palette, frames, compressed);
    }

    private static (int Offset, int Length) RequireChunk(ReadOnlySpan<byte> bytes, int position, string expected,
        string name)
    {
        if (!TryReadChunk(bytes, position, out var tag, out var offset, out var length) || tag != expected)
        {
            throw new InvalidDataException(
                $"{name}: expected a '{expected}' chunk at offset {position}, found '{tag ?? "<none>"}'.");
        }

        return (offset, length);
    }

    private static bool TryReadChunk(ReadOnlySpan<byte> bytes, int position, out string? tag, out int offset,
        out int length)
    {
        tag = null;
        offset = 0;
        length = 0;
        if (position < 0 || position + ChunkHeaderLength > bytes.Length)
        {
            return false;
        }

        for (var i = 0; i < 4; i++)
        {
            var c = bytes[position + i];
            if (c is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        var declared = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(position + 4, 4));
        if (declared > int.MaxValue)
        {
            return false;
        }

        offset = position + ChunkHeaderLength;
        if (offset + declared > bytes.Length)
        {
            // Two retail files (GXICONS, gui) write BBMP's length as the ABSOLUTE offset of the
            // "END " terminator rather than a payload length — a quirk of whichever tool wrote the
            // compressed variant. It is exact (declared == fileLength - 4 on both), so accept
            // precisely that and nothing looser; the chunk then runs to the terminator.
            if (declared != bytes.Length - TerminatorLength || declared < offset)
            {
                offset = 0;
                return false;
            }

            declared = (uint)(declared - offset);
        }

        tag = Encoding.ASCII.GetString(bytes.Slice(position, 4));
        length = (int)declared;
        return true;
    }
}
