// Ported from OpenTESArena (MIT License), https://github.com/afritz1/OpenTESArena
//   OpenTESArena/src/Assets/Compression.h — Compression::decodeType08 (an Okumura/Yoshizaki
//   LZHUF-style decoder). License texts are collected centrally in THIRD_PARTY_LICENSES.

namespace BethesdaMultitool.Core.Compression;

/// <summary>
///     The LZHUF (LZSS + adaptive Huffman) decoder used by Arena ".IMG"/".CIF" compression
///     type 08 and .MIF voxel data. 314 symbols (256 literal bytes + 58 match lengths 3..60) are
///     coded with an adaptive Huffman tree that starts uniform and re-sorts by frequency after
///     every decoded symbol; matches then carry a 12-bit window offset coded as a table-driven
///     prefix (top 6 bits) plus 6 verbatim low bits. Matches copy through a 4096-byte ring
///     initialised to 0x20, byte-by-byte, so a match may overlap its own output. The output size
///     drives termination.
///     <para>
///         Faithful to the reference, which omits Okumura's MAX_FREQ tree rebuild — frequency
///         counters climb monotonically, which is only observable on streams of ~32K+ symbols
///         (no Arena asset reaches that). One deliberate divergence: the reference feeds
///         phantom zero bits once the input is exhausted and decodes garbage to the end of the
///         output; this port throws <see cref="InvalidDataException" /> the moment a consumed
///         bit was never actually present in the input, which cannot trigger on a well-formed
///         stream (padding bits in the final byte are real input).
///     </para>
/// </summary>
internal static class LzhufCodec
{
    private const int CharCount = 314; // 256 literals + 58 match-length codes.
    private const int TreeSize = 627; // 2 * CharCount - 1 tree slots.
    private const int RootIndex = 626; // Highest-frequency slot; always the root.
    private const int LeafBase = 627; // Tree values >= this are leaves (value - 627 = symbol).
    private const int WindowSize = 4096;
    private const int WindowMask = 0x0FFF;

    /// <summary>Top 6 bits of the match offset, indexed by the first 8 offset bits (Okumura d_code).</summary>
    private static readonly byte[] HighOffsetBits =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01,
        0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02, 0x02,
        0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
        0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
        0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09, 0x09,
        0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0A, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B, 0x0B,
        0x0C, 0x0C, 0x0C, 0x0C, 0x0D, 0x0D, 0x0D, 0x0D, 0x0E, 0x0E, 0x0E, 0x0E, 0x0F, 0x0F, 0x0F, 0x0F,
        0x10, 0x10, 0x10, 0x10, 0x11, 0x11, 0x11, 0x11, 0x12, 0x12, 0x12, 0x12, 0x13, 0x13, 0x13, 0x13,
        0x14, 0x14, 0x14, 0x14, 0x15, 0x15, 0x15, 0x15, 0x16, 0x16, 0x16, 0x16, 0x17, 0x17, 0x17, 0x17,
        0x18, 0x18, 0x19, 0x19, 0x1A, 0x1A, 0x1B, 0x1B, 0x1C, 0x1C, 0x1D, 0x1D, 0x1E, 0x1E, 0x1F, 0x1F,
        0x20, 0x20, 0x21, 0x21, 0x22, 0x22, 0x23, 0x23, 0x24, 0x24, 0x25, 0x25, 0x26, 0x26, 0x27, 0x27,
        0x28, 0x28, 0x29, 0x29, 0x2A, 0x2A, 0x2B, 0x2B, 0x2C, 0x2C, 0x2D, 0x2D, 0x2E, 0x2E, 0x2F, 0x2F,
        0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x3B, 0x3C, 0x3D, 0x3E, 0x3F,
    ];

    /// <summary>
    ///     Total bit length of the offset prefix code, indexed like <see cref="HighOffsetBits" />
    ///     (Okumura d_len); the decoder reads (value - 2) bits beyond the initial 8.
    /// </summary>
    private static readonly byte[] LowOffsetBitCount =
    [
        0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
        0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03, 0x03,
        0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
        0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
        0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04,
        0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
        0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
        0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
        0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05, 0x05,
        0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
        0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
        0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06, 0x06,
        0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07, 0x07,
        0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08, 0x08,
    ];

    /// <summary>Decode an LZHUF stream into a buffer of the known decompressed size.</summary>
    /// <param name="input">The compressed bit stream.</param>
    /// <param name="decompressedLength">Exact size of the decoded output.</param>
    /// <returns>The decoded bytes.</returns>
    /// <exception cref="InvalidDataException">
    ///     Thrown when the stream is truncated (a consumed bit was never present in the input) or
    ///     a match would overrun the declared output size.
    /// </exception>
    public static byte[] Decompress(ReadOnlySpan<byte> input, int decompressedLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(decompressedLength);

        var output = new byte[decompressedLength];
        var parent = new ushort[TreeSize + CharCount];
        var tree = new ushort[TreeSize];
        var freq = new ushort[TreeSize];
        InitializeTree(parent, tree, freq);

        Span<byte> window = stackalloc byte[WindowSize];
        window.Fill(0x20);
        var historyPos = 0;

        var bits = new BitReader(input);
        var outPos = 0;

        while (outPos < decompressedLength)
        {
            // Walk from the root, one input bit per branch, until a leaf (>= 627) is hit.
            int node = tree[RootIndex];
            while (node < LeafBase)
            {
                node = tree[node + bits.ReadBit()];
            }

            UpdateTree(node, parent, tree, freq);

            var codeword = node - LeafBase;
            if (codeword < 256)
            {
                // Literal byte, echoed through the ring.
                var value = (byte)codeword;
                window[historyPos++ & WindowMask] = value;
                output[outPos++] = value;
            }
            else
            {
                // Match: 8 offset bits select the prefix tables, then (d_len - 2) more bits
                // complete the low 6 offset bits. Length comes from the Huffman symbol itself.
                var tableIdx = bits.ReadTableIndex();
                var offsetHigh = HighOffsetBits[tableIdx] << 6;
                var extraBits = LowOffsetBitCount[tableIdx] - 2;
                var offsetLow = tableIdx;
                for (var i = 0; i < extraBits; i++)
                {
                    offsetLow = (offsetLow << 1) | bits.ReadBit();
                }

                var copyPos = historyPos - (offsetHigh | (offsetLow & 0x3F)) - 1;
                var toCopy = codeword - 256 + 3;
                if (outPos + toCopy > decompressedLength)
                {
                    throw new InvalidDataException(
                        $"LZHUF match of {toCopy} overruns output ({outPos}/{decompressedLength}).");
                }

                // Byte-by-byte through the ring so the match may overlap its own output.
                for (var i = 0; i < toCopy; i++)
                {
                    var value = window[copyPos++ & WindowMask];
                    output[outPos++] = value;
                    window[historyPos++ & WindowMask] = value;
                }
            }
        }

        return output;
    }

    /// <summary>
    ///     Build the initial uniform tree: leaves for all 314 symbols at slots 0..313 (frequency 1),
    ///     internal nodes pairing them bottom-up at 314..626, the root at 626. <paramref name="parent" />
    ///     maps a tree slot to its parent's slot, and additionally maps <c>627 + symbol</c> to the
    ///     slot currently holding that symbol's leaf.
    /// </summary>
    private static void InitializeTree(ushort[] parent, ushort[] tree, ushort[] freq)
    {
        for (var i = 0; i < RootIndex; i++)
        {
            parent[i] = (ushort)((i >> 1) + CharCount);
        }

        parent[RootIndex] = 0;
        for (var symbol = 0; symbol < CharCount; symbol++)
        {
            parent[LeafBase + symbol] = (ushort)symbol;
        }

        for (var i = 0; i < CharCount; i++)
        {
            tree[i] = (ushort)(LeafBase + i);
            freq[i] = 1;
        }

        for (var i = CharCount; i < TreeSize; i++)
        {
            var firstChild = 2 * (i - CharCount);
            tree[i] = (ushort)firstChild;
            freq[i] = (ushort)(freq[firstChild] + freq[firstChild + 1]);
        }
    }

    /// <summary>
    ///     Increment the decoded leaf's frequency and re-sort ancestors so slots stay ordered by
    ///     ascending frequency (the adaptive step). Faithful port, including uint16 counters and
    ///     no MAX_FREQ rebuild.
    /// </summary>
    private static void UpdateTree(int node, ushort[] parent, ushort[] tree, ushort[] freq)
    {
        int slot = parent[node];
        do
        {
            var newFreq = (ushort)(freq[slot] + 1);
            freq[slot] = newFreq;

            // If this slot now outweighs a later one, bubble it up just before the next
            // greater-or-equal frequency and fix both slots' parent mappings.
            var nextSlot = slot + 1;
            if (nextSlot < TreeSize && freq[nextSlot] < newFreq)
            {
                do
                {
                    nextSlot++;
                } while (nextSlot < TreeSize && freq[nextSlot] < newFreq);

                nextSlot--;

                freq[slot] = freq[nextSlot];
                freq[nextSlot] = newFreq;
                (tree[slot], tree[nextSlot]) = (tree[nextSlot], tree[slot]);

                int moved = tree[nextSlot];
                parent[moved] = (ushort)nextSlot;
                if (moved < TreeSize)
                {
                    parent[moved + 1] = (ushort)nextSlot;
                }

                moved = tree[slot];
                parent[moved] = (ushort)slot;
                if (moved < TreeSize)
                {
                    parent[moved + 1] = (ushort)slot;
                }

                slot = nextSlot;
            }

            slot = parent[slot];
        } while (slot != 0);
    }

    /// <summary>
    ///     MSB-first bit reader over a 16-bit reservoir, mirroring the reference: it tops the
    ///     reservoir up to at least 9 valid bits before every read, appending phantom zero bits
    ///     once the input is exhausted. Unlike the reference it counts how many reservoir bits
    ///     are real and throws instead of handing out a phantom bit.
    /// </summary>
    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _input;
        private int _pos;
        private int _bitBuffer;
        private int _validBits;
        private int _realBits;

        public BitReader(ReadOnlySpan<byte> input)
        {
            _input = input;
        }

        /// <summary>Read one bit.</summary>
        public int ReadBit()
        {
            Refill();
            var bit = (_bitBuffer >> 15) & 1;
            Consume(1);
            return bit;
        }

        /// <summary>Read 8 bits (the match-offset table index).</summary>
        public int ReadTableIndex()
        {
            Refill();
            var value = (_bitBuffer >> 8) & 0xFF;
            Consume(8);
            return value;
        }

        private void Refill()
        {
            while (_validBits < 9)
            {
                if (_pos < _input.Length)
                {
                    _bitBuffer = (_bitBuffer | (_input[_pos++] << (8 - _validBits))) & 0xFFFF;
                    _realBits += 8;
                }

                _validBits += 8;
            }
        }

        private void Consume(int count)
        {
            if (_realBits < count)
            {
                throw new InvalidDataException(
                    $"LZHUF stream truncated: needed {count} bit(s) at input offset {_pos} but the input is exhausted.");
            }

            _bitBuffer = (_bitBuffer << count) & 0xFFFF;
            _validBits -= count;
            _realBits -= count;
        }
    }
}
