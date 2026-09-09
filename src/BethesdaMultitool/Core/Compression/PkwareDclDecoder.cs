// Ported from zlib's contrib/blast.c (zlib License), Copyright (C) 2003-2019 Mark Adler,
//   https://github.com/madler/zlib/blob/develop/contrib/blast/blast.c — the decompressor for the
//   PKWARE Data Compression Library "implode" format. The licence is reproduced here rather than
//   only referenced, so this file stands on its own; THIRD_PARTY_LICENSES should carry it too.
//
//   This software is provided 'as-is', without any express or implied warranty. In no event will
//   the author be held liable for any damages arising from the use of this software.
//
//   Permission is granted to anyone to use this software for any purpose, including commercial
//   applications, and to alter it and redistribute it freely, subject to the following
//   restrictions:
//
//   1. The origin of this software must not be misrepresented; you must not claim that you wrote
//      the original software. If you use this software in a product, an acknowledgment in the
//      product documentation would be appreciated but is not required.
//   2. Altered source versions must be plainly marked as such, and must not be misrepresented as
//      being the original software.
//   3. This notice may not be removed or altered from any source distribution.
//
//   Mark Adler  madler@alumni.caltech.edu

namespace BethesdaMultitool.Core.Compression;

/// <summary>
///     Decoder for the PKWARE Data Compression Library ("DCL implode") stream, the codec Bethesda's
///     1996 Daggerfall installer used for <c>ARENA2\PACKED.DAT</c> — its <c>INSTALL.EXE</c> carries
///     the banner "PKWARE Data Compression Library for DOS32 Version 1.11".
///     <para>
///         The stream opens with two literal bytes: a coded-literals flag (0 = literals are raw
///         bytes, 1 = literals are Huffman coded) and a dictionary-size exponent 4..6 (1 KiB, 2 KiB
///         or 4 KiB of history). Everything after that is a bit stream read LSB-first within each
///         byte. Each iteration reads one flag bit: 0 introduces a literal, 1 a length/distance
///         pair. The length symbol comes from a fixed Huffman code over 16 slots whose bases are
///         3, 2, 4, 5, 6, 7, 8, 9, 10, 12, 16, 24, 40, 72, 136, 264 with 0..8 extra bits; a decoded
///         length of 519 is the end-of-stream code. The distance is a fixed 64-symbol Huffman code
///         supplying the high bits, shifted by 2 (when the length is 2) or by the dictionary
///         exponent, with that many verbatim low bits, plus one.
///     </para>
///     <para>
///         The three fixed codes are stored the way the reference stores them — run-length pairs of
///         (repeat count - 1) in the high nibble and code length in the low nibble — and are
///         expanded into canonical count/symbol tables once. Codes are read one bit at a time with
///         each bit INVERTED, which is what makes the reference's compact tables decode; dropping
///         the inversion still decodes a self-consistent-looking stream for a few symbols and then
///         diverges, so it is not a detail that can be guessed from output plausibility.
///     </para>
///     <para>
///         ⚠⚠ Daggerfall's blocks DO carry the end-of-stream code, and an earlier note here saying
///         they do not — that "the last two bytes of each stream go unread" as an encoder flush —
///         was an artefact of stopping the decode the instant the container's declared size was
///         reached. Measured over both streams of the retail <c>PACKED.DAT</c> 2026-09-07: decoding
///         PAST the declared size hits the 519 end code IMMEDIATELY on 134 of 134 blocks, emits
///         exactly 0 further bytes, and then leaves 0 (not 2) of the declared compressed bytes
///         unread. The end code is the 16 bits <c>1</c>, seven <c>0</c>s, eight <c>1</c>s — byte
///         aligned that is <c>01 FF</c>, and the retail blocks' last two bytes carry that pattern at
///         each of the eight bit offsets and nothing else (<c>01ff</c> 14, <c>fe01</c> 18,
///         <c>fc03</c> 15, <c>f807</c> 14, <c>f00f</c> 22, <c>e01f</c> 24, <c>c03f</c> 18,
///         <c>807f</c> 9 — 134 in total). ⚠ Only the 14 aligned blocks have the code IN those two
///         bytes; on the other 120 it begins in the third-from-last byte and the final byte ends
///         with 1..7 unused bits, so "the end code is the last two bytes" is false as a general
///         statement about the format. The length-bounded overload REQUIRES that code: the stream is
///         self-terminating, which makes the container's compressed size verifiable from the payload
///         instead of merely bounding it. A stream that would produce more than the declared size is
///         rejected rather than silently truncated to it.
///     </para>
///     <para>
///         ⚠ Coverage: no retail byte this repo decodes uses CODED literals — all 134 Daggerfall
///         blocks open <c>00 06</c> (raw literals, 4 KiB dictionary) — so
///         <see cref="LiteralCodeLengths" />, the largest transcription here, is exercised only by
///         the test suite. It is pinned there against the upstream <c>blast.c</c> table by a
///         hand-built stream that codes all 256 literal symbols in order and must decode to the
///         bytes 0..255; a permutation inside the table fails that test but nothing else.
///     </para>
/// </summary>
internal static class PkwareDclDecoder
{
    /// <summary>Match-length bases, indexed by the length code's Huffman symbol.</summary>
    private static readonly short[] LengthBase =
        [3, 2, 4, 5, 6, 7, 8, 9, 10, 12, 16, 24, 40, 72, 136, 264];

    /// <summary>Extra verbatim length bits, indexed like <see cref="LengthBase" />.</summary>
    private static readonly byte[] LengthExtraBits =
        [0, 0, 0, 0, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8];

    /// <summary>Coded-literal code: 256 symbols, run-length encoded as the reference stores it.</summary>
    private static readonly byte[] LiteralCodeLengths =
    [
        11, 124, 8, 7, 28, 7, 188, 13, 76, 4, 10, 8, 12, 10, 12, 10, 8, 23, 8,
        9, 7, 6, 7, 8, 7, 6, 55, 8, 23, 24, 12, 11, 7, 9, 11, 12, 6, 7, 22, 5,
        7, 24, 6, 11, 9, 6, 7, 22, 7, 11, 38, 7, 9, 8, 25, 11, 8, 11, 9, 12,
        8, 12, 5, 38, 5, 38, 5, 11, 7, 5, 6, 21, 6, 10, 53, 8, 7, 24, 10, 27,
        44, 253, 253, 253, 252, 252, 252, 13, 12, 45, 12, 45, 12, 61, 12, 45,
        44, 173
    ];

    /// <summary>Length code: 16 symbols.</summary>
    private static readonly byte[] LengthCodeLengths = [2, 35, 36, 53, 38, 23];

    /// <summary>Distance code: 64 symbols.</summary>
    private static readonly byte[] DistanceCodeLengths = [2, 20, 53, 230, 247, 151, 248];

    private static readonly HuffmanCode LiteralCode = HuffmanCode.FromRunLengths(LiteralCodeLengths);
    private static readonly HuffmanCode LengthCode = HuffmanCode.FromRunLengths(LengthCodeLengths);
    private static readonly HuffmanCode DistanceCode = HuffmanCode.FromRunLengths(DistanceCodeLengths);

    /// <summary>
    ///     Decodes exactly <paramref name="decompressedLength" /> bytes. Throws if the stream ends
    ///     early, so a truncated block cannot be mistaken for a short file, and equally if it would
    ///     produce more, so a framing error cannot be silently trimmed to the declared size.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int decompressedLength)
    {
        return Decompress(input, decompressedLength, out _);
    }

    /// <summary>
    ///     Decodes exactly <paramref name="decompressedLength" /> bytes, requires the stream to
    ///     close on its end-of-stream code, and reports how many input bytes that consumed. A
    ///     caller that knows the stream's declared compressed length can compare the two and so
    ///     verify the framing from the payload itself.
    /// </summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int decompressedLength, out int consumed)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decompressedLength);
        var output = new byte[decompressedLength];
        var written = Decode(input, output, decompressedLength, out var ended, out consumed);
        if (!ended)
        {
            throw new InvalidDataException(
                $"DCL stream produces more than the declared {decompressedLength} byte(s).");
        }

        if (written != decompressedLength)
        {
            throw new InvalidDataException(
                $"DCL stream ended after {written} of {decompressedLength} byte(s) at its end-of-stream code.");
        }

        return output;
    }

    /// <summary>Decodes until the stream's end-of-stream code, for streams that carry one.</summary>
    public static byte[] Decompress(ReadOnlySpan<byte> input)
    {
        var output = new byte[Math.Max(64, input.Length * 4)];
        while (true)
        {
            var written = Decode(input, output, output.Length, out var ended, out _);
            if (ended)
            {
                return output.AsSpan(0, written).ToArray();
            }

            // The buffer filled before the end code appeared; retry with room to finish.
            output = new byte[output.Length * 2];
        }
    }

    /// <summary>
    ///     The decoder proper. Fills <paramref name="output" /> to at most <paramref name="limit" />
    ///     bytes, reporting whether it stopped on the end-of-stream code and how many input bytes it
    ///     read. Stopping WITHOUT the end code means the stream had more to say than the limit
    ///     allowed — the caller decides whether that is an error (the length-bounded overload) or
    ///     just a buffer to grow (the unbounded one). Nothing is ever written past the limit.
    /// </summary>
    private static int Decode(
        ReadOnlySpan<byte> input, Span<byte> output, int limit, out bool ended, out int consumed)
    {
        ended = false;
        var bits = new BitReader(input);
        var coded = bits.ReadBits(8);
        var dictionaryExponent = bits.ReadBits(8);
        if (coded > 1)
        {
            throw new InvalidDataException(
                $"DCL header's literal flag is {coded}; it must be 0 (raw literals) or 1 (coded).");
        }

        if (dictionaryExponent is < 4 or > 6)
        {
            throw new InvalidDataException(
                $"DCL header's dictionary exponent is {dictionaryExponent}; it must be 4, 5 or 6.");
        }

        var written = 0;
        while (true)
        {
            if (bits.ReadBits(1) == 0)
            {
                if (written == limit)
                {
                    break;
                }

                output[written++] = (byte)(coded == 1 ? bits.DecodeSymbol(LiteralCode) : bits.ReadBits(8));
                continue;
            }

            var lengthSymbol = bits.DecodeSymbol(LengthCode);
            var length = LengthBase[lengthSymbol] + bits.ReadBits(LengthExtraBits[lengthSymbol]);
            if (length == 519)
            {
                ended = true;
                break;
            }

            var shift = length == 2 ? 2 : dictionaryExponent;
            var distance = (bits.DecodeSymbol(DistanceCode) << shift) + bits.ReadBits(shift) + 1;
            if (distance > written)
            {
                throw new InvalidDataException(
                    $"DCL match distance {distance} reaches before the start of the {written}-byte output.");
            }

            if (written + length > limit)
            {
                // Overproduction: report it by leaving `ended` false rather than trimming the copy,
                // which would turn a framing error into a plausible-looking short block.
                break;
            }

            for (var i = 0; i < length; i++)
            {
                output[written] = output[written - distance];
                written++;
            }
        }

        consumed = bits.Position;
        return written;
    }

    /// <summary>One of the format's three fixed canonical Huffman codes.</summary>
    private sealed class HuffmanCode
    {
        private HuffmanCode(int[] countsByLength, int[] symbols)
        {
            CountsByLength = countsByLength;
            Symbols = symbols;
        }

        /// <summary>Number of symbols of each code length, index 1..15 (index 0 unused).</summary>
        public int[] CountsByLength { get; }

        /// <summary>Symbols ordered canonically: by code length, then by symbol value.</summary>
        public int[] Symbols { get; }

        /// <summary>
        ///     Expands the reference's compact table — each byte holds (repeat count - 1) in its
        ///     high nibble and the code length in its low nibble — into canonical tables.
        /// </summary>
        public static HuffmanCode FromRunLengths(ReadOnlySpan<byte> runLengths)
        {
            var lengths = new List<int>(256);
            foreach (var packed in runLengths)
            {
                var repeat = (packed >> 4) + 1;
                var length = packed & 0x0F;
                for (var i = 0; i < repeat; i++)
                {
                    lengths.Add(length);
                }
            }

            var counts = new int[16];
            foreach (var length in lengths)
            {
                counts[length]++;
            }

            var offsets = new int[16];
            for (var length = 1; length < 15; length++)
            {
                offsets[length + 1] = offsets[length] + counts[length];
            }

            var symbols = new int[lengths.Count];
            for (var symbol = 0; symbol < lengths.Count; symbol++)
            {
                var length = lengths[symbol];
                if (length != 0)
                {
                    symbols[offsets[length]++] = symbol;
                }
            }

            return new HuffmanCode(counts, symbols);
        }
    }

    /// <summary>
    ///     LSB-first bit reader. Unlike the reference, which quietly reads zero bits past the end of
    ///     the input, this throws — a well-formed stream reaches its end code with bits to spare, so
    ///     a read past EOF means the framing is wrong, not that the stream is finished.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _input;
        private int _bitBuffer;
        private int _bitCount;

        public BitReader(ReadOnlySpan<byte> input)
        {
            _input = input;
        }

        /// <summary>Input bytes pulled so far — whole bytes, since the reader buffers by the byte.</summary>
        public int Position { get; private set; }

        /// <summary>Reads <paramref name="count" /> bits (0..16), least significant bit first.</summary>
        public int ReadBits(int count)
        {
            var value = _bitBuffer;
            while (_bitCount < count)
            {
                if (Position >= _input.Length)
                {
                    throw new InvalidDataException(
                        $"DCL stream truncated: needed {count} bit(s) past the {_input.Length}-byte input.");
                }

                value |= _input[Position++] << _bitCount;
                _bitCount += 8;
            }

            _bitBuffer = value >> count;
            _bitCount -= count;
            return value & ((1 << count) - 1);
        }

        /// <summary>Walks one of the fixed codes a bit at a time, inverting each bit as it goes.</summary>
        public int DecodeSymbol(HuffmanCode code)
        {
            var value = 0;
            var first = 0;
            var index = 0;
            for (var length = 1; length <= 15; length++)
            {
                value |= ReadBits(1) ^ 1;
                var count = code.CountsByLength[length];
                if (value < first + count)
                {
                    return code.Symbols[index + (value - first)];
                }

                index += count;
                first = (first + count) << 1;
                value <<= 1;
            }

            throw new InvalidDataException("DCL stream carries a code longer than 15 bits.");
        }
    }
}
