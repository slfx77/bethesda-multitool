// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' Smacker decoder as statically linked
// into two shipped games — Redguard's RG.EXE ("*** Smacker Version: 3.2b***") and Battlespire's
// GAME.EXE ("*** Smacker Version: 3.0k***") — via our Ghidra decompilations at
// tools/GhidraProject/ClassicRE/RG.EXE.decompiled.txt and GAME.EXE.decompiled.txt and our own
// capstone disassembly of the unpacked LE images, as written up in the specification document
//   scratchpad .../gap2/smacker/SPEC.md  ("Smacker Video (SMK2) - Format Specification"),
//   sections 3.1 (FUN_00110a00, the 8-bit tree), 3.2 (FUN_00110e90 + FUN_00110b40, the 16-bit
//   tree with its three escape codes) and 3.3 (FUN_001144b0, the per-frame reset).
//
// NO FFmpeg- or libav-derived code, and no other third-party Smacker implementation, was consulted,
// read, copied or paraphrased.

namespace BethesdaMultitool.Core.Formats.Smacker;

/// <summary>
///     An 8-bit Smacker Huffman tree: <c>[1 present][pre-order nodes: 1 = branch, 0 + 8 bits =
///     leaf][0]</c>. A tree whose present bit is 0 is ABSENT and decodes to 0 without consuming a
///     bit.
///     <para>
///         Storage is one <c>int</c> per node in pre-order: a branch holds the index of its RIGHT
///         child (the left child is always the next node), a leaf holds <c>~value</c>. That is the
///         game's own layout — its parser (<c>0x110a00</c>) keeps a stack of open branches and,
///         when a leaf closes one, patches the branch with the distance to the node written next.
///     </para>
/// </summary>
internal sealed class SmackerTree8
{
    private readonly int[] _nodes;

    private SmackerTree8(int[] nodes)
    {
        _nodes = nodes;
    }

    /// <summary>The tree that is absent from the stream: every decode is 0 and reads nothing.</summary>
    internal static SmackerTree8 Empty { get; } = new([]);

    /// <summary>True when the stream carried no tree.</summary>
    internal bool IsEmpty => _nodes.Length == 0;

    /// <summary>Number of leaves.</summary>
    internal int LeafCount => _nodes.Count(n => n < 0);

    /// <summary>Reads a tree (present bit included) from <paramref name="reader" />.</summary>
    internal static SmackerTree8 Read(SmackerBitReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.ReadBit() == 0)
        {
            return Empty;
        }

        var nodes = new List<int>(64);
        ReadNodes(reader, nodes, 0);
        if (reader.ReadBit() != 0)
        {
            throw new InvalidDataException("An 8-bit Smacker tree was not terminated by a 0 bit.");
        }

        return new SmackerTree8([.. nodes]);
    }

    private static void ReadNodes(SmackerBitReader reader, List<int> nodes, int depth)
    {
        // A Huffman tree over 256 symbols is never deeper than 255; anything past that is a
        // corrupt stream reading 1-bits forever.
        if (depth > 256)
        {
            throw new InvalidDataException("An 8-bit Smacker tree is deeper than 256 levels.");
        }

        if (reader.ReadBit() == 1)
        {
            var index = nodes.Count;
            nodes.Add(0); // patched below with the right child's index
            ReadNodes(reader, nodes, depth + 1);
            nodes[index] = nodes.Count;
            ReadNodes(reader, nodes, depth + 1);
            return;
        }

        nodes.Add(~(int)reader.Read(8));
    }

    /// <summary>Decodes one symbol.</summary>
    internal int Decode(SmackerBitReader reader)
    {
        if (_nodes.Length == 0)
        {
            return 0;
        }

        var index = 0;
        var node = _nodes[0];
        while (node >= 0)
        {
            index = reader.ReadBit() == 0 ? index + 1 : node;
            node = _nodes[index];
        }

        return ~node;
    }
}

/// <summary>
///     A 16-bit Smacker Huffman tree (MMAP, MCLR, FULL or TYPE): <c>[1 present][8-bit low tree]
///     [8-bit high tree][3 x 16-bit escape codes][pre-order nodes: 1 = branch, 0 = leaf whose
///     value is low-tree symbol | high-tree symbol &lt;&lt; 8][0]</c>.
///     <para>
///         A leaf whose value equals escape code <c>k</c> does not carry that value: it stands for
///         the <c>k</c>-th MOST RECENT value this tree produced. The game keeps the three "last"
///         slots as node pointers (<c>0x110b40</c> stores them into the tree header; <c>0x1144b0</c>
///         writes 0 into all three at the start of EVERY frame) and, after each decode, shifts the
///         slots down when the value differs from slot 0 (<c>0x1113e0</c>: <c>cmp [ecx],edx</c>
///         then the three-move rotate).
///     </para>
///     <para>
///         ⚠ A tree whose present bit is 0 is absent: every decode returns 0 and reads no bits.
///         The corpus never ships one, so that path is inferred from the code alone.
///     </para>
/// </summary>
internal sealed class SmackerTree16
{
    private const int EscapeFlag = 1 << 16;
    private readonly int[] _nodes;
    private readonly int[] _last = new int[3];

    private SmackerTree16(int[] nodes, SmackerTree8 low, SmackerTree8 high, ushort[] escapes)
    {
        _nodes = nodes;
        LowTree = low;
        HighTree = high;
        EscapeCodes = escapes;
    }

    /// <summary>The tree that is absent from the stream.</summary>
    internal static SmackerTree16 Empty { get; } = new([], SmackerTree8.Empty, SmackerTree8.Empty, new ushort[3]);

    /// <summary>True when the stream carried no tree.</summary>
    internal bool IsEmpty => _nodes.Length == 0;

    /// <summary>The 8-bit tree the low byte of each leaf was read through.</summary>
    internal SmackerTree8 LowTree { get; }

    /// <summary>The 8-bit tree the high byte of each leaf was read through.</summary>
    internal SmackerTree8 HighTree { get; }

    /// <summary>The three escape codes, in the order read.</summary>
    internal IReadOnlyList<ushort> EscapeCodes { get; }

    /// <summary>Number of leaves, escape leaves included.</summary>
    internal int LeafCount => _nodes.Count(n => n < 0);

    /// <summary>Number of leaves that stand for a "last value" slot.</summary>
    internal int EscapeLeafCount => _nodes.Count(n => n < 0 && (~n & EscapeFlag) != 0);

    /// <summary>Reads a tree (present bit included) from <paramref name="reader" />.</summary>
    internal static SmackerTree16 Read(SmackerBitReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);

        if (reader.ReadBit() == 0)
        {
            return Empty;
        }

        var low = SmackerTree8.Read(reader);
        var high = SmackerTree8.Read(reader);
        var escapes = new ushort[3];
        for (var i = 0; i < 3; i++)
        {
            escapes[i] = (ushort)reader.Read(16);
        }

        var nodes = new List<int>(1024);
        ReadNodes(reader, nodes, low, high, escapes, 0);
        if (reader.ReadBit() != 0)
        {
            throw new InvalidDataException("A 16-bit Smacker tree was not terminated by a 0 bit.");
        }

        return new SmackerTree16([.. nodes], low, high, escapes);
    }

    private static void ReadNodes(
        SmackerBitReader reader, List<int> nodes, SmackerTree8 low, SmackerTree8 high, ushort[] escapes, int depth)
    {
        if (depth > 65_536)
        {
            throw new InvalidDataException("A 16-bit Smacker tree is deeper than its symbol count allows.");
        }

        if (reader.ReadBit() == 1)
        {
            var index = nodes.Count;
            nodes.Add(0);
            ReadNodes(reader, nodes, low, high, escapes, depth + 1);
            nodes[index] = nodes.Count;
            ReadNodes(reader, nodes, low, high, escapes, depth + 1);
            return;
        }

        var value = low.Decode(reader) | (high.Decode(reader) << 8);
        var payload = value;
        for (var slot = 0; slot < 3; slot++)
        {
            if (value == escapes[slot])
            {
                payload = EscapeFlag | (slot << 17);
                break;
            }
        }

        nodes.Add(~payload);
    }

    /// <summary>Zeroes the three "last value" slots — done by the game at the start of every frame.</summary>
    internal void ResetLastValues()
    {
        _last[0] = 0;
        _last[1] = 0;
        _last[2] = 0;
    }

    /// <summary>Decodes one 16-bit value, resolving escapes and rotating the "last value" slots.</summary>
    internal int Decode(SmackerBitReader reader)
    {
        if (_nodes.Length == 0)
        {
            return 0;
        }

        var index = 0;
        var node = _nodes[0];
        while (node >= 0)
        {
            index = reader.ReadBit() == 0 ? index + 1 : node;
            node = _nodes[index];
        }

        var payload = ~node;
        var value = (payload & EscapeFlag) != 0 ? _last[payload >> 17] : payload;
        if (value != _last[0])
        {
            _last[2] = _last[1];
            _last[1] = _last[0];
            _last[0] = value;
        }

        return value;
    }
}
