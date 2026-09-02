// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/CIFFile.cpp / CIFFile.h. License texts are collected centrally
//   in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     Decodes Arena .CIF sprite collections (character faces, cursors, weapon and equipment
///     animations): a sequence of IMG-style frames, each led by its own 12-byte LE header
///     (x u16, y u16, width u16, height u16, flags u16, dataLen u16), iterated to end of
///     file. The compression type (flags low byte: 0 raw, 2 RLE, 4 LZSS, 8 LZHUF) is taken
///     from the FIRST frame's header and applied to every frame — later frames' flags are
///     re-read but never re-dispatch, faithful to the reference. A handful of tile CIFs are
///     headerless with hardcoded frame counts and dimensions. Per-frame x/y screen offsets
///     ride on the returned <see cref="IndexedBitmap" />s.
/// </summary>
internal static class ArenaCifDecoder
{
    private const int HeaderSize = 12;

    /// <summary>
    ///     Headerless tile .CIFs (frame count, width, height), ported verbatim from
    ///     OpenTESArena CIFFile.cpp (the RawCifOverride table).
    /// </summary>
    private static readonly Dictionary<string, (int FrameCount, int Width, int Height)> RawCifOverride =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["BRASS.CIF"] = (9, 8, 8),
            ["BRASS2.CIF"] = (9, 8, 8),
            ["MARBLE.CIF"] = (9, 3, 3),
            ["MARBLE2.CIF"] = (9, 3, 3),
            ["PARCH.CIF"] = (9, 20, 20),
            ["SCROLL.CIF"] = (9, 20, 20),
        };

    /// <summary>
    ///     Decode every frame of an Arena .CIF. <paramref name="fileName" /> (any path prefix
    ///     is stripped, comparisons case-insensitive) selects the headerless-tile table.
    /// </summary>
    /// <exception cref="InvalidDataException">Thrown when the file is truncated or malformed.</exception>
    public static IReadOnlyList<IndexedBitmap> Decode(ReadOnlySpan<byte> file, string fileName)
    {
        ArgumentNullException.ThrowIfNull(fileName);
        var name = Path.GetFileName(fileName);

        if (RawCifOverride.TryGetValue(name, out var raw))
        {
            return DecodeHeaderless(file, name, raw.FrameCount, raw.Width, raw.Height);
        }

        if (file.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"CIF \"{name}\" is too small for a {HeaderSize}-byte frame header ({file.Length} bytes).");
        }

        // The first frame's flags choose the codec for the whole file.
        var compression = BinaryPrimitives.ReadUInt16LittleEndian(file[8..]) & 0x00FF;
        if (compression is not (0x00 or 0x02 or 0x04 or 0x08))
        {
            throw new InvalidDataException(
                $"CIF \"{name}\": unrecognized compression type 0x{compression:X2}.");
        }

        var frames = new List<IndexedBitmap>();
        var offset = 0;
        while (offset < file.Length)
        {
            if (offset + HeaderSize > file.Length)
            {
                throw new InvalidDataException(
                    $"CIF \"{name}\": frame header at offset {offset} is truncated " +
                    $"({file.Length - offset} byte(s) remain).");
            }

            var xOffset = BinaryPrimitives.ReadUInt16LittleEndian(file[offset..]);
            var yOffset = BinaryPrimitives.ReadUInt16LittleEndian(file[(offset + 2)..]);
            var width = BinaryPrimitives.ReadUInt16LittleEndian(file[(offset + 4)..]);
            var height = BinaryPrimitives.ReadUInt16LittleEndian(file[(offset + 6)..]);
            var dataLength = BinaryPrimitives.ReadUInt16LittleEndian(file[(offset + 10)..]);

            var pixelCount = width * height;
            byte[] indices;
            if (compression == 0x00)
            {
                // The reference copies dataLength bytes (not width * height) into the frame
                // buffer for uncompressed .CIFs; more than pixelCount would overrun it there,
                // so that is rejected here, and fewer leaves the tail at index 0.
                if (dataLength > pixelCount)
                {
                    throw new InvalidDataException(
                        $"CIF \"{name}\": frame at offset {offset} declares {dataLength} pixel bytes " +
                        $"for a {width}x{height} ({pixelCount}-pixel) frame.");
                }

                if (offset + HeaderSize + dataLength > file.Length)
                {
                    throw new InvalidDataException(
                        $"CIF \"{name}\": frame pixel data at offset {offset + HeaderSize} is truncated " +
                        $"({dataLength} bytes declared, {file.Length - offset - HeaderSize} remain).");
                }

                indices = new byte[pixelCount];
                file.Slice(offset + HeaderSize, dataLength).CopyTo(indices);
            }
            else
            {
                indices = ArenaImgDecoder.DecodePixelData(
                    file, offset + HeaderSize, compression, dataLength, pixelCount, name);
            }

            frames.Add(new IndexedBitmap(width, height, indices, xOffset, yOffset));
            offset += HeaderSize + dataLength;
        }

        return frames;
    }

    /// <summary>Decode a headerless tile .CIF: frameCount raw width*height chunks, offsets 0.</summary>
    private static IReadOnlyList<IndexedBitmap> DecodeHeaderless(
        ReadOnlySpan<byte> file, string name, int frameCount, int width, int height)
    {
        var frameLength = width * height;
        if (file.Length < frameCount * frameLength)
        {
            throw new InvalidDataException(
                $"Headerless CIF \"{name}\" is truncated: {frameCount * frameLength} bytes needed " +
                $"for {frameCount} {width}x{height} frames, got {file.Length}.");
        }

        var frames = new IndexedBitmap[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            frames[i] = new IndexedBitmap(
                width, height, file.Slice(i * frameLength, frameLength).ToArray());
        }

        return frames;
    }
}
