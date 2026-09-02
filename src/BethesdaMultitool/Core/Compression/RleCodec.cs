// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/Compression.cpp — Compression::decodeRLE (itself adapted
//   from WinArena). License texts are collected centrally in THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Compression;

/// <summary>
///     Byte-oriented RLE used by DOS-era XnGine assets (Arena ".IMG"/".CIF" compression
///     type 02). The stream is a sequence of packets, each led by a control byte: if bit 7
///     is set, the next byte repeats <c>(control &amp; 0x7F) + 1</c> times; otherwise
///     <c>control + 1</c> literal bytes follow. Decoding stops once exactly
///     <c>decompressedLength</c> bytes have been produced.
///     <para>
///         The reference decoder walks the input with a pointer/index pair that nets out to a
///         plain sequential read; this port uses a single cursor.
///     </para>
/// </summary>
internal static class RleCodec
{
    /// <summary>Decode an RLE stream into a buffer of the known decompressed size.</summary>
    /// <param name="input">The compressed packet stream.</param>
    /// <param name="decompressedLength">Exact number of bytes the stream decodes to.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="InvalidDataException">
    ///     Thrown when the stream is truncated or a packet would overrun the declared output size.
    /// </exception>
    /// <summary>
    ///     The 16-bit-word variant, used by Arena <c>.RMD</c> wilderness chunks. Packets are led by
    ///     a SIGNED little-endian word: a positive <c>n</c> means <c>n</c> literal words follow, a
    ///     negative <c>n</c> means the next single word repeats <c>|n|</c> times. Decoding stops
    ///     once <paramref name="wordCount" /> words have been produced; the output is little-endian
    ///     words, so its byte length is twice that.
    ///     <para>
    ///         A packet header of 0 would consume input while producing nothing, so it is rejected
    ///         rather than allowed to spin — the reference has no such guard because it only ever
    ///         reads shipped files.
    ///     </para>
    /// </summary>
    public static byte[] DecompressWords(ReadOnlySpan<byte> input, int wordCount)
    {
        if (wordCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(wordCount), wordCount, "Word count cannot be negative.");
        }

        var output = new byte[wordCount * 2];
        var read = 0;
        var written = 0;

        while (written < wordCount)
        {
            if (read + 2 > input.Length)
            {
                throw new InvalidDataException(
                    $"Word-RLE stream ended after {written} of {wordCount} words.");
            }

            var sample = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(input[read..]);
            read += 2;

            if (sample == 0)
            {
                throw new InvalidDataException("Word-RLE packet header of 0 makes no progress.");
            }

            var count = sample > 0 ? sample : -sample;
            if (written + count > wordCount)
            {
                throw new InvalidDataException(
                    $"Word-RLE packet of {count} words overruns the {wordCount}-word output.");
            }

            var literals = sample > 0;
            var bytesNeeded = literals ? count * 2 : 2;
            if (read + bytesNeeded > input.Length)
            {
                throw new InvalidDataException("Word-RLE packet is truncated.");
            }

            if (literals)
            {
                input.Slice(read, count * 2).CopyTo(output.AsSpan(written * 2));
                read += count * 2;
            }
            else
            {
                var low = input[read];
                var high = input[read + 1];
                read += 2;
                for (var i = 0; i < count; i++)
                {
                    output[((written + i) * 2) + 0] = low;
                    output[((written + i) * 2) + 1] = high;
                }
            }

            written += count;
        }

        return output;
    }

    public static byte[] Decompress(ReadOnlySpan<byte> input, int decompressedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decompressedLength);

        var output = new byte[decompressedLength];
        var inPos = 0;
        var outPos = 0;

        while (outPos < decompressedLength)
        {
            if (inPos >= input.Length)
            {
                throw new InvalidDataException(
                    $"RLE stream truncated: control byte expected at offset {inPos} with {decompressedLength - outPos} output bytes still owed.");
            }

            var control = input[inPos++];

            if ((control & 0x80) != 0)
            {
                // Run packet: one value byte, repeated (control - 0x7F) == (control & 0x7F) + 1 times.
                if (inPos >= input.Length)
                {
                    throw new InvalidDataException($"RLE stream truncated: run value byte missing at offset {inPos}.");
                }

                var value = input[inPos++];
                var count = control - 0x7F;
                if (outPos + count > decompressedLength)
                {
                    throw new InvalidDataException(
                        $"RLE run of {count} at input offset {inPos - 2} overruns output ({outPos}/{decompressedLength}).");
                }

                output.AsSpan(outPos, count).Fill(value);
                outPos += count;
            }
            else
            {
                // Literal packet: (control + 1) verbatim bytes.
                var count = control + 1;
                if (inPos + count > input.Length)
                {
                    throw new InvalidDataException(
                        $"RLE stream truncated: {count} literal bytes declared at offset {inPos - 1}, {input.Length - inPos} remain.");
                }

                if (outPos + count > decompressedLength)
                {
                    throw new InvalidDataException(
                        $"RLE literal run of {count} at input offset {inPos - 1} overruns output ({outPos}/{decompressedLength}).");
                }

                input.Slice(inPos, count).CopyTo(output.AsSpan(outPos));
                inPos += count;
                outPos += count;
            }
        }

        return output;
    }
}
