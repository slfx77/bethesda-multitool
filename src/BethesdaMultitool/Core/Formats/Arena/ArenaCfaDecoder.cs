// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/CFAFile.cpp / CFAFile.h (itself adapted from WinArena). License
//   texts are collected centrally in THIRD_PARTY_LICENSES.
//
// One deliberate restructuring: the reference carries seven hand-written "demux" routines, one
// per bits-per-pixel value, each unpacking a fixed group of source bytes into a fixed count of
// values. Every one of them is plain MSB-first bit unpacking, and every group is byte-aligned —
// for each supported depth, groupBytes * 8 == groupValues * bitsPerPixel exactly (1bpp 8=8,
// 2bpp 8=8, 3bpp 24=24, 4bpp 16=16, 5bpp 40=40, 6bpp 24=24, 7bpp 56=56). A single continuous
// bit reader is therefore equivalent to all seven, and ArenaCfaDecoderTests pins that equivalence
// against expected values computed from the reference's own bit masks.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Compression;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     Decodes Arena <c>.CFA</c> animation sets — creature walk cycles, spell effects and the
///     player's weapon swings. The most involved of Arena's sprite formats: pixels are RLE
///     compressed, then bit-packed at 1-8 bits per pixel, and the unpacked values are indices into
///     a per-file lookup table that yields the actual palette index.
///     <para>
///         Every frame shares one width, height and draw offset, and all frames are stored as a
///         single RLE stream, so the file must be decoded as a whole rather than frame by frame.
///     </para>
///     <para>
///         All eight depths ship in the retail archive (measured 2026-09-01: 1bpp x4, 2bpp x4,
///         3bpp x6, 4bpp x12, 5bpp x99, 6bpp x153, 7bpp x48, 8bpp x14). The 8-bit files carry no
///         lookup table at all — their declared header size is exactly the table's offset — which
///         is why that depth copies bytes straight through.
///     </para>
///     <para>
///         Note that a few 8-bit sprites are not meant to be shown against the base palette.
///         GHOST1.CFA, for instance, stores only indices 0-13, the standard EGA colour block, and
///         renders as rainbow stripes when mapped directly; the engine draws ghosts through a
///         translucency effect that reinterprets those low indices. That is a runtime behaviour,
///         not a decode step, so this decoder reproduces the stored indices faithfully and leaves
///         the effect to whatever displays them.
///     </para>
/// </summary>
internal static class ArenaCfaDecoder
{
    /// <summary>Fixed offset of the palette-index lookup table; the RLE data starts at the header size.</summary>
    private const int LookupTableOffset = 76;

    /// <summary>Decodes every frame of a .CFA.</summary>
    public static IReadOnlyList<IndexedBitmap> Decode(ReadOnlySpan<byte> bytes, string name)
    {
        if (bytes.Length < LookupTableOffset)
        {
            throw new InvalidDataException(
                $"'{name}' is too small to be an Arena .CFA ({bytes.Length} bytes; the lookup table alone " +
                $"starts at {LookupTableOffset}).");
        }

        var widthUncompressed = BinaryPrimitives.ReadUInt16LittleEndian(bytes);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(bytes[2..]);
        var widthCompressed = BinaryPrimitives.ReadUInt16LittleEndian(bytes[4..]);
        var xOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes[6..]);
        var yOffset = BinaryPrimitives.ReadUInt16LittleEndian(bytes[8..]);
        int bitsPerPixel = bytes[10];
        int frameCount = bytes[11];
        var headerSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes[12..]);

        if (bitsPerPixel is < 1 or > 8)
        {
            throw new InvalidDataException(
                $"'{name}' declares {bitsPerPixel} bits per pixel; .CFA supports 1-8.");
        }

        if (widthUncompressed == 0 || height == 0 || frameCount == 0)
        {
            throw new InvalidDataException(
                $"'{name}' declares an empty animation ({widthUncompressed}x{height}, {frameCount} frame(s)).");
        }

        if (headerSize < LookupTableOffset || headerSize > bytes.Length)
        {
            throw new InvalidDataException(
                $"'{name}' declares a header of {headerSize} bytes, which does not lie between the lookup " +
                $"table at {LookupTableOffset} and end of file ({bytes.Length}).");
        }

        var lookup = bytes[LookupTableOffset..headerSize];
        var rleLength = widthCompressed * height * frameCount;
        var decompressed = RleCodec.Decompress(bytes[headerSize..], rleLength);

        var frames = new List<IndexedBitmap>(frameCount);
        var sourceOffset = 0;

        for (var frame = 0; frame < frameCount; frame++)
        {
            var pixels = new byte[widthUncompressed * height];

            for (var y = 0; y < height; y++)
            {
                var rowStart = y * widthUncompressed;
                var line = decompressed.AsSpan(sourceOffset, widthCompressed);

                if (bitsPerPixel == 8)
                {
                    // At full depth the bytes are already palette indices: the reference applies
                    // neither demuxing nor the lookup table here, and copies only the compressed
                    // width, leaving any remainder of the row at 0.
                    var copy = Math.Min(widthCompressed, widthUncompressed);
                    line[..copy].CopyTo(pixels.AsSpan(rowStart, copy));
                }
                else
                {
                    UnpackRow(line, bitsPerPixel, lookup, pixels.AsSpan(rowStart, widthUncompressed));
                }

                sourceOffset += widthCompressed;
            }

            frames.Add(new IndexedBitmap(widthUncompressed, height, pixels, xOffset, yOffset));
        }

        return frames;
    }

    /// <summary>
    ///     Unpacks one scan line: MSB-first values of <paramref name="bitsPerPixel" /> bits, each
    ///     mapped through the file's lookup table. Stops at the uncompressed width, so the padding
    ///     that bit alignment leaves at the end of a line is discarded.
    /// </summary>
    private static void UnpackRow(
        ReadOnlySpan<byte> line,
        int bitsPerPixel,
        ReadOnlySpan<byte> lookup,
        Span<byte> destination)
    {
        var mask = (1 << bitsPerPixel) - 1;
        var bitPosition = 0;
        var totalBits = line.Length * 8;

        for (var x = 0; x < destination.Length; x++)
        {
            if (bitPosition + bitsPerPixel > totalBits)
            {
                // The compressed line ran out before the uncompressed width was filled; the rest of
                // the row stays 0, matching the reference's zero-filled scratch buffer.
                return;
            }

            var value = 0;
            for (var bit = 0; bit < bitsPerPixel; bit++)
            {
                var absolute = bitPosition + bit;
                var sourceBit = (line[absolute >> 3] >> (7 - (absolute & 7))) & 1;
                value = (value << 1) | sourceBit;
            }

            bitPosition += bitsPerPixel;
            var index = value & mask;
            destination[x] = index < lookup.Length ? lookup[index] : (byte)0;
        }
    }
}
