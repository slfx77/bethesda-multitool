// Ported from falltergeist/dat-unpacker (MIT License, (c) 2015-2018 Falltergeist developers),
//   https://github.com/falltergeist/dat-unpacker — src/LZSS.cpp (DatUnpacker::LZSS::decompress).
//   Block framing cross-checked against the CC0-1.0 Kaitai spec
//   https://github.com/kaitai-io/kaitai_struct_formats — game/fallout_dat.ksy.
//   License texts are collected centrally in THIRD_PARTY_LICENSES.

using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Compression;

/// <summary>
///     The block-framed LZSS used by Fallout 1 DAT (DAT1) archive entries. The stream is a
///     sequence of blocks, each led by a signed 16-bit big-endian length: 0 terminates the
///     stream, a negative value means |n| verbatim bytes follow, and a positive value means n
///     bytes of LZSS-coded data. Each coded block gets a fresh 4096-byte dictionary filled with
///     0x20 and a write cursor at 4078; flag bytes are consumed LSB-first, a set bit is one
///     literal, a clear bit a two-byte reference (<c>byte1</c> = offset low 8, <c>byte2</c> high
///     nibble = offset high 4, low nibble + 3 = copy length; both cursors wrap at 4096). The
///     stream may also end by simply exhausting the input without a 0 terminator, and bytes
///     after a terminator are ignored.
/// </summary>
internal static class FalloutLzss
{
    private const int DictionarySize = 4096;
    private const int InitialWriteCursor = DictionarySize - 18;

    /// <summary>Decode a DAT1 LZSS stream into a buffer of the known unpacked size.</summary>
    /// <param name="input">The complete packed entry (the DAT directory's size_packed bytes).</param>
    /// <param name="unpackedLength">Exact unpacked size declared by the DAT directory.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="InvalidDataException">
    ///     Thrown when a block is truncated, a copy would overrun the declared unpacked size, or
    ///     the stream ends before producing exactly <paramref name="unpackedLength" /> bytes.
    /// </exception>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int unpackedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(unpackedLength);

        var output = new byte[unpackedLength];
        Span<byte> dictionary = stackalloc byte[DictionarySize];

        var inPos = 0;
        var outPos = 0;

        while (inPos < input.Length)
        {
            if (inPos + 2 > input.Length)
            {
                throw new InvalidDataException(
                    $"DAT1 LZSS stream has a dangling block-length byte at offset {inPos}.");
            }

            var blockLength = BinaryPrimitives.ReadInt16BigEndian(input.Slice(inPos, 2));
            inPos += 2;

            if (blockLength == 0)
            {
                // Explicit terminator; any trailing bytes are ignored. A stream may also end by
                // exhausting the input instead — both endings are valid.
                break;
            }

            if (blockLength < 0)
            {
                inPos = CopyRawBlock(input, inPos, -blockLength, output, ref outPos);
            }
            else
            {
                inPos = DecodeCodedBlock(input, inPos, blockLength, dictionary, output, ref outPos);
            }
        }

        if (outPos != unpackedLength)
        {
            throw new InvalidDataException(
                $"DAT1 LZSS stream ended after {outPos} bytes; the directory declared {unpackedLength}.");
        }

        return output;
    }

    /// <summary>Copy a raw (stored) block of <paramref name="count" /> verbatim bytes.</summary>
    private static int CopyRawBlock(ReadOnlySpan<byte> input, int inPos, int count, byte[] output, ref int outPos)
    {
        if (inPos + count > input.Length)
        {
            throw new InvalidDataException(
                $"DAT1 raw block of {count} at offset {inPos} extends past the input end ({input.Length}).");
        }

        if (outPos + count > output.Length)
        {
            throw new InvalidDataException(
                $"DAT1 raw block of {count} at offset {inPos} overruns output ({outPos}/{output.Length}).");
        }

        input.Slice(inPos, count).CopyTo(output.AsSpan(outPos));
        outPos += count;
        return inPos + count;
    }

    /// <summary>
    ///     Decode one LZSS-coded block. The dictionary is re-initialised per block (0x20 fill,
    ///     write cursor at 4078), faithfully to the reference — earlier blocks contribute nothing.
    /// </summary>
    private static int DecodeCodedBlock(
        ReadOnlySpan<byte> input, int inPos, int blockLength, Span<byte> dictionary, byte[] output, ref int outPos)
    {
        var blockEnd = inPos + blockLength;
        if (blockEnd > input.Length)
        {
            throw new InvalidDataException(
                $"DAT1 coded block of {blockLength} at offset {inPos} extends past the input end ({input.Length}).");
        }

        dictionary.Fill(0x20);
        var writeCursor = InitialWriteCursor;

        while (inPos < blockEnd)
        {
            var flags = input[inPos++];
            for (var bit = 0; bit != 8 && inPos < blockEnd; bit++, flags >>= 1)
            {
                if ((flags & 1) != 0)
                {
                    // Literal: one byte through the dictionary.
                    if (outPos >= output.Length)
                    {
                        throw new InvalidDataException(
                            $"DAT1 literal at offset {inPos} overruns output ({outPos}/{output.Length}).");
                    }

                    var value = input[inPos++];
                    output[outPos++] = value;
                    dictionary[writeCursor] = value;
                    writeCursor = (writeCursor + 1) % DictionarySize;
                }
                else
                {
                    // Reference: 12-bit dictionary offset + 4-bit length, copied byte-by-byte so
                    // it may overlap the bytes it is itself writing.
                    if (inPos + 2 > blockEnd)
                    {
                        throw new InvalidDataException(
                            $"DAT1 reference pair at offset {inPos} is truncated by the block end ({blockEnd}).");
                    }

                    var readCursor = input[inPos] | ((input[inPos + 1] & 0xF0) << 4);
                    var toCopy = (input[inPos + 1] & 0x0F) + 3;
                    inPos += 2;

                    if (outPos + toCopy > output.Length)
                    {
                        throw new InvalidDataException(
                            $"DAT1 reference of {toCopy} at offset {inPos - 2} overruns output ({outPos}/{output.Length}).");
                    }

                    for (var i = 0; i < toCopy; i++)
                    {
                        var value = dictionary[readCursor];
                        output[outPos++] = value;
                        dictionary[writeCursor] = value;
                        readCursor = (readCursor + 1) % DictionarySize;
                        writeCursor = (writeCursor + 1) % DictionarySize;
                    }
                }
            }
        }

        return inPos;
    }
}
