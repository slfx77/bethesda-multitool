// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"), section 5
//   (FUN_30009F00 at 0x30009F00, merge at 0x3000A380).
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased.

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>
///     One of Bink's Huffman "trees", which is really a PAIR: one of the sixteen fixed code books
///     built into <c>binkw32.dll</c> (<see cref="BinkTables.CodeBooks" />) plus a 16-entry
///     permutation read from the stream that maps code-book slot to symbol. Every tree in the
///     format is over exactly 16 symbols.
///     <para>
///         ⚠ A stream that has desynced usually fails FIRST in <see cref="Read" />'s explicit-symbol
///         branch, because a repeated symbol overflows the 16-entry map. That makes this class a
///         useful desync alarm: the DLL stack-smashes there, we throw.
///     </para>
/// </summary>
internal sealed class BinkHuffmanTree
{
    private readonly byte[] _codeBook;
    private readonly int _maxBits;
    private readonly byte[] _symbolMap;

    private BinkHuffmanTree(byte[] codeBook, int maxBits, byte[] symbolMap)
    {
        _codeBook = codeBook;
        _maxBits = maxBits;
        _symbolMap = symbolMap;
    }

    /// <summary>Reads one tree header from the bitstream (spec §5.1).</summary>
    internal static BinkHuffmanTree Read(BinkBitReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        var index = (int)reader.Read(4);
        var codeBook = BinkTables.CodeBooks[index];
        var maxBits = BinkTables.CodeBookMaxBits[index];

        // Book 0 is sixteen 4-bit codes with slot == code, so an identity map turns the whole
        // decode into a raw 4-bit read. The DLL special-cases index 0 by skipping the map entirely.
        if (index == 0)
        {
            return new BinkHuffmanTree(codeBook, maxBits, Identity());
        }

        if (reader.ReadFlag())
        {
            return new BinkHuffmanTree(codeBook, maxBits, ReadExplicitSymbolList(reader));
        }

        var level = (int)reader.Read(2);
        var pairs = new byte[16];
        for (var i = 0; i < 16; i += 2)
        {
            if (reader.ReadFlag())
            {
                pairs[i] = (byte)(i + 1);
                pairs[i + 1] = (byte)i;
            }
            else
            {
                pairs[i] = (byte)i;
                pairs[i + 1] = (byte)(i + 1);
            }
        }

        var map = level switch
        {
            0 => pairs,
            _ => Shuffle(reader, pairs, level)
        };

        return new BinkHuffmanTree(codeBook, maxBits, map);
    }

    /// <summary>Decodes one symbol (0..15).</summary>
    internal int Decode(BinkBitReader reader)
    {
        var entry = _codeBook[(int)reader.Peek(_maxBits)];
        reader.Consume(entry >> 4);
        return _symbolMap[entry & 0x0F];
    }

    private static byte[] Identity()
    {
        var map = new byte[16];
        for (var i = 0; i < 16; i++)
        {
            map[i] = (byte)i;
        }

        return map;
    }

    private static byte[] ReadExplicitSymbolList(BinkBitReader reader)
    {
        var map = new byte[16];
        var listed = 0;
        var count = (int)reader.Read(3);
        var seen = 0;
        for (var i = 0; i <= count; i++)
        {
            var symbol = (int)reader.Read(4);
            if ((seen & (1 << symbol)) != 0)
            {
                // ⚠ Divergence 6 of the list on BinkVideoDecoder.
                // The DLL appends the unlisted symbols after the listed ones and would run off the
                // end of its 16-byte map here. A well-formed stream never repeats a symbol, so this
                // is the earliest reliable sign that the bit position has drifted.
                throw new InvalidDataException(
                    $"Bink Huffman header lists symbol {symbol} twice — the bitstream has desynced.");
            }

            seen |= 1 << symbol;
            map[listed++] = (byte)symbol;
        }

        for (var symbol = 0; symbol < 16; symbol++)
        {
            if ((seen & (1 << symbol)) == 0)
            {
                map[listed++] = (byte)symbol;
            }
        }

        return map;
    }

    /// <summary>
    ///     The "how thoroughly is the alphabet shuffled" ladder: level 1 merges adjacent pairs into
    ///     fours, level 2 merges those into eights, level 3 into the whole sixteen.
    /// </summary>
    private static byte[] Shuffle(BinkBitReader reader, byte[] pairs, int level)
    {
        var quads = new byte[4][];
        for (var i = 0; i < 4; i++)
        {
            quads[i] = Merge(reader, pairs, i * 4, 2, pairs, i * 4 + 2, 2);
        }

        if (level == 1)
        {
            return Concat(quads[0], quads[1], quads[2], quads[3]);
        }

        var octA = Merge(reader, quads[0], 0, 4, quads[1], 0, 4);
        var octB = Merge(reader, quads[2], 0, 4, quads[3], 0, 4);
        if (level == 2)
        {
            return Concat(octA, octB);
        }

        return Merge(reader, octA, 0, 8, octB, 0, 8);
    }

    /// <summary>
    ///     The bit-driven merge of two equal-length lists (<c>FUN_3000A380</c>): one bit per output
    ///     element, 0 takes the next element of the first list, 1 the next of the second; when
    ///     either list runs out the remainder of the other is appended with NO further bits.
    /// </summary>
    private static byte[] Merge(
        BinkBitReader reader, byte[] a, int aStart, int aLength, byte[] b, int bStart, int bLength)
    {
        var result = new byte[aLength + bLength];
        int i = 0, j = 0, k = 0;
        while (i < aLength && j < bLength)
        {
            if (reader.ReadFlag())
            {
                result[k++] = b[bStart + j++];
            }
            else
            {
                result[k++] = a[aStart + i++];
            }
        }

        while (i < aLength)
        {
            result[k++] = a[aStart + i++];
        }

        while (j < bLength)
        {
            result[k++] = b[bStart + j++];
        }

        return result;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = 0;
        foreach (var part in parts)
        {
            total += part.Length;
        }

        var result = new byte[total];
        var at = 0;
        foreach (var part in parts)
        {
            part.CopyTo(result, at);
            at += part.Length;
        }

        return result;
    }
}
