// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/DFAFile.cpp / DFAFile.h. License texts are collected centrally
//   in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;
using BethesdaMultitool.Core.Compression;
using BethesdaMultitool.Core.Imaging;

namespace BethesdaMultitool.Core.Formats.Arena;

/// <summary>
///     Decodes Arena .DFA animations (entities that animate in place: shopkeepers, tavern
///     folk, lamps, fountains, staff pieces, torches). Header (all LE u16): image count, two
///     unknowns, width, height, first-frame compressed length. The first frame is
///     RLE-compressed; every later frame is a fresh copy of the FIRST frame patched by its
///     own uncompressed delta chunks (deltas are per-frame against frame 1, not cumulative).
///     Each later frame's chunk stream: chunk-group size u16 (read and unused, as in the
///     reference), chunk count u16, then per chunk a pixel offset u16, byte count u16, and
///     that many raw bytes written at consecutive pixel indices. All frames share the header
///     dimensions and have no draw offsets.
/// </summary>
internal static class ArenaDfaDecoder
{
    private const int HeaderSize = 12;

    /// <summary>Decode every frame of an Arena .DFA.</summary>
    /// <exception cref="InvalidDataException">Thrown when the file is truncated or malformed.</exception>
    public static IReadOnlyList<IndexedBitmap> Decode(ReadOnlySpan<byte> file)
    {
        if (file.Length < HeaderSize)
        {
            throw new InvalidDataException(
                $"DFA is too small for its {HeaderSize}-byte header ({file.Length} bytes).");
        }

        var imageCount = BinaryPrimitives.ReadUInt16LittleEndian(file);
        var width = BinaryPrimitives.ReadUInt16LittleEndian(file[6..]);
        var height = BinaryPrimitives.ReadUInt16LittleEndian(file[8..]);
        var firstFrameCompressedLength = BinaryPrimitives.ReadUInt16LittleEndian(file[10..]);

        if (imageCount == 0)
        {
            // The reference would index an empty frame buffer here.
            throw new InvalidDataException("DFA declares zero images.");
        }

        var pixelCount = width * height;
        var firstFrame = RleCodec.Decompress(file[HeaderSize..], pixelCount);

        var frames = new IndexedBitmap[imageCount];
        frames[0] = new IndexedBitmap(width, height, firstFrame);

        var offset = HeaderSize + firstFrameCompressedLength;
        for (var frameIndex = 1; frameIndex < imageCount; frameIndex++)
        {
            // Each frame patches its own copy of frame 1, never a previous frame's output.
            var indices = (byte[])firstFrame.Clone();

            if (offset + 4 > file.Length)
            {
                throw new InvalidDataException(
                    $"DFA chunk-group header for frame {frameIndex} at offset {offset} is truncated.");
            }

            // The u16 chunk-group size at [offset] is read and discarded by the reference.
            var chunkCount = BinaryPrimitives.ReadUInt16LittleEndian(file[(offset + 2)..]);
            offset += 4;

            for (var chunkIndex = 0; chunkIndex < chunkCount; chunkIndex++)
            {
                if (offset + 4 > file.Length)
                {
                    throw new InvalidDataException(
                        $"DFA update header {chunkIndex} for frame {frameIndex} at offset {offset} is truncated.");
                }

                var updateOffset = BinaryPrimitives.ReadUInt16LittleEndian(file[offset..]);
                var updateCount = BinaryPrimitives.ReadUInt16LittleEndian(file[(offset + 2)..]);
                offset += 4;

                if (offset + updateCount > file.Length)
                {
                    throw new InvalidDataException(
                        $"DFA update data for frame {frameIndex} at offset {offset} is truncated " +
                        $"({updateCount} bytes declared, {file.Length - offset} remain).");
                }

                if (updateOffset + updateCount > pixelCount)
                {
                    // The reference would write past its frame buffer.
                    throw new InvalidDataException(
                        $"DFA update for frame {frameIndex} writes pixels {updateOffset}..{updateOffset + updateCount - 1} " +
                        $"in a {width}x{height} ({pixelCount}-pixel) frame.");
                }

                file.Slice(offset, updateCount).CopyTo(indices.AsSpan(updateOffset));
                offset += updateCount;
            }

            frames[frameIndex] = new IndexedBitmap(width, height, indices);
        }

        return frames;
    }
}
