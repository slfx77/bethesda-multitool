// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/Compression.h — Compression::decodeType04.
//   License texts are collected centrally in THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Compression;

/// <summary>
///     The LZSS variant used by Arena ".IMG"/".CIF" compression type 04. A flag byte precedes
///     up to eight items, consumed LSB-first with no shift before the first bit: a set bit is one
///     literal byte, a clear bit is a two-byte back-reference into a 4096-byte ring initialised
///     to 0x20. The reference pair encodes a 12-bit ring offset (<c>byte1</c> = low 8,
///     <c>byte2</c> high nibble = high 4) plus a copy length of <c>(byte2 &amp; 0x0F) + 3</c>;
///     the decoder starts its write cursor at ring index 0 and biases the read offset by +18,
///     which is exactly the classic Okumura layout (write cursor 4078, unbiased offset) relabelled.
///     Decoding is input-driven: it stops when the input runs out and zero-fills any remaining
///     output, faithfully to the reference.
///     <para>
///         The Battlespire per-entry BSA compression (ariscop/battlespire-tools
///         <c>bsatool/bsa_format.txt</c>, Unlicense) is a close relative but NOT bit-compatible:
///         its code pair is byte-swapped (offset high nibble + length first, low offset byte
///         second) and its ring prefill zeroes the final 18 bytes instead of using 0x20
///         throughout. That variant is <see cref="DecompressBattlespire" />; the two must never
///         be merged into one code path, because every one of those differences silently decodes
///         garbage on the other game's data.
///     </para>
/// </summary>
internal static class LzssCodec
{
    private const int WindowSize = 4096;
    private const int WindowMask = 0x0FFF;

    /// <summary>
    ///     Decode the Battlespire per-entry BSA compression (clean-room from ariscop's
    ///     battlespire-tools <c>bsatool/bsa_format.txt</c>, Unlicense). Same LZSS family as the
    ///     Arena codec but NOT bit-compatible with it, in three ways this method must not blur:
    ///     the code pair is byte-swapped (first byte = length high nibble + offset high nibble,
    ///     second byte = offset low 8 bits), the window prefill is 0x20 for the first 4,078 bytes
    ///     but 0x00 for the final 18 (Arena is 0x20 throughout), and the output size is not stored
    ///     anywhere — decoding is input-driven and returns whatever the stream produces.
    /// </summary>
    public static byte[] DecompressBattlespire(ReadOnlySpan<byte> input)
    {
        const int prefillBoundary = WindowSize - 18;

        var output = new List<byte>(input.Length * 2);
        Span<byte> window = stackalloc byte[WindowSize];
        window[..prefillBoundary].Fill(0x20);
        window[prefillBoundary..].Clear();

        var writePos = prefillBoundary;
        var inPos = 0;
        var bitCount = 0;
        var mask = 0;

        while (inPos < input.Length)
        {
            if (bitCount == 0)
            {
                mask = input[inPos++];
                bitCount = 8;
                continue;
            }

            bitCount--;
            var literal = (mask & 1) != 0;
            mask >>= 1;

            if (literal)
            {
                if (inPos >= input.Length)
                {
                    // A trailing flag bit with no byte behind it ends the stream.
                    break;
                }

                var value = input[inPos++];
                output.Add(value);
                window[writePos] = value;
                writePos = (writePos + 1) & WindowMask;
            }
            else
            {
                if (inPos + 2 > input.Length)
                {
                    break;
                }

                var first = input[inPos++];
                var second = input[inPos++];

                // Offset is an ABSOLUTE window position, not a distance back from the cursor.
                var offset = ((first & 0x0F) << 8) | second;
                var length = (first >> 4) + 3;

                for (var i = 0; i < length; i++)
                {
                    var value = window[(offset + i) & WindowMask];
                    output.Add(value);
                    window[writePos] = value;
                    writePos = (writePos + 1) & WindowMask;
                }
            }
        }

        return [.. output];
    }

    /// <summary>Decode a type-04 LZSS stream into a buffer of the known decompressed size.</summary>
    /// <param name="input">The compressed stream (flag bytes, literals, reference pairs).</param>
    /// <param name="decompressedLength">Exact size of the decoded output buffer.</param>
    /// <returns>The decoded bytes; output beyond the stream's end is zero-filled.</returns>
    /// <exception cref="InvalidDataException">
    ///     Thrown when a flagged item is truncated or a copy would overrun the declared output size.
    /// </exception>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int decompressedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decompressedLength);

        var output = new byte[decompressedLength];
        Span<byte> window = stackalloc byte[WindowSize];
        window.Fill(0x20);

        var historyPos = 0;
        var inPos = 0;
        var outPos = 0;
        var bitCount = 0;
        var mask = 0;

        while (inPos < input.Length)
        {
            if (bitCount == 0)
            {
                bitCount = 8;
                mask = input[inPos++];
            }
            else
            {
                mask >>= 1;
            }

            if ((mask & 1) != 0)
            {
                // Literal: copy one byte through the ring.
                if (inPos >= input.Length)
                {
                    throw new InvalidDataException($"LZSS stream truncated: literal byte missing at offset {inPos}.");
                }

                if (outPos >= decompressedLength)
                {
                    throw new InvalidDataException(
                        $"LZSS literal at input offset {inPos} overruns output ({outPos}/{decompressedLength}).");
                }

                var value = input[inPos++];
                window[historyPos++ & WindowMask] = value;
                output[outPos++] = value;
            }
            else
            {
                // Back-reference: 12-bit ring offset + 4-bit length, copied byte-by-byte so a
                // reference may overlap the bytes it is itself writing (run expansion).
                if (inPos + 2 > input.Length)
                {
                    throw new InvalidDataException(
                        $"LZSS stream truncated: reference pair expected at offset {inPos}, {input.Length - inPos} byte(s) remain.");
                }

                var byte1 = input[inPos++];
                var byte2 = input[inPos++];
                var toCopy = (byte2 & 0x0F) + 3;
                var copyPos = (((byte2 & 0xF0) << 4) | byte1) + 18;

                if (outPos + toCopy > decompressedLength)
                {
                    throw new InvalidDataException(
                        $"LZSS reference of {toCopy} at input offset {inPos - 2} overruns output ({outPos}/{decompressedLength}).");
                }

                for (var i = 0; i < toCopy; i++)
                {
                    var value = window[copyPos++ & WindowMask];
                    output[outPos++] = value;
                    window[historyPos++ & WindowMask] = value;
                }
            }

            bitCount--;
        }

        // Remaining output stays zero (the reference explicitly zero-fills the tail).
        return output;
    }
}
