// ORIGINAL CLEAN-ROOM IMPLEMENTATION.
//
// Written from our own reverse engineering of RAD Game Tools' shipped decoder binkw32.dll
// (Fallout Tactics, md5 ecbd8213e89f8afde368f8eb05ff5a9c), via our Ghidra decompilation at
// tools/GhidraProject/ClassicRE/binkw32.dll.decompiled.txt and our own capstone disassembly of the
// same file, as written up in the specification document
//   scratchpad .../cleanroom/bink/SPEC.md  ("Bink Video (BIKi) - Format Specification"), section 4
//   (FUN_30009E30 at 0x30009E30 and the nine refill routines 0x3000A4A0 .. 0x3000B370).
//
// NO FFmpeg- or libav-derived code, and no other third-party Bink implementation, was consulted,
// read, copied or paraphrased.

namespace BethesdaMultitool.Core.Formats.Bink;

/// <summary>The nine parallel element queues a Bink plane is coded as.</summary>
internal enum BinkBundleKind
{
    BlockTypes = 0,
    SubBlockTypes = 1,
    Colours = 2,
    Pattern = 3,
    XOffset = 4,
    YOffset = 5,
    IntraDc = 6,
    InterDc = 7,
    Run = 8
}

/// <summary>
///     One bundle: a queue of small elements refilled a chunk at a time as the block loop drains it.
///     A plane is NOT coded block by block — it is coded as nine of these in parallel, all nine
///     refilled in a fixed order at the start of every block row.
///     <para>
///         ⚠⚠ A refill count of ZERO permanently retires the bundle for the rest of the plane; its
///         length field is never read again. This is the common case, not an edge case: 6,428 of
///         19,513 length fields in a six-frame corpus sweep were zero. Modelling it as "an empty
///         refill, try again next row" reads a length field that is not there and desyncs the plane.
///     </para>
/// </summary>
internal sealed class BinkBundle
{
    // Runs in the block-type coding may overshoot the declared count by up to 32 elements; the DLL
    // writes them into slack at the end of its buffer rather than clipping, so we allocate the same.
    private const int RunOvershootHeadroom = 64;

    private readonly int[] _data;
    private int _cursor;
    private int _end;

    private BinkBundle(BinkBundleKind kind, int capacity, int lengthBits)
    {
        Kind = kind;
        Capacity = capacity;
        LengthBits = lengthBits;
        _data = new int[capacity + RunOvershootHeadroom];
    }

    internal BinkBundleKind Kind { get; }

    /// <summary>Elements the bundle is sized for: <c>0x200 + (W &gt;&gt; 3) * scale</c>.</summary>
    internal int Capacity { get; }

    /// <summary>Width of the refill count field: <c>bit_length(Capacity - 1)</c>.</summary>
    internal int LengthBits { get; }

    /// <summary>This bundle's own Huffman tree; null for the two DC bundles, which have none.</summary>
    internal BinkHuffmanTree? Tree { get; set; }

    /// <summary>True once a zero-length refill has retired the bundle for this plane.</summary>
    internal bool IsRetired { get; private set; }

    /// <summary>
    ///     Builds the nine bundles for a plane of decoded width <paramref name="planeWidth" />.
    ///     Bundle 1 is the only one sized from half the width.
    /// </summary>
    internal static BinkBundle[] CreateForPlane(int planeWidth)
    {
        // scale per bundle, from the DLL's bundle-init table.
        ReadOnlySpan<int> scales = [1, 1, 0x40, 8, 1, 1, 1, 1, 0x30];
        var bundles = new BinkBundle[9];
        for (var i = 0; i < 9; i++)
        {
            var width = i == (int)BinkBundleKind.SubBlockTypes ? planeWidth >> 1 : planeWidth;
            var capacity = 0x200 + (width >> 3) * scales[i];
            bundles[i] = new BinkBundle((BinkBundleKind)i, capacity, BitLength(capacity - 1));
        }

        return bundles;
    }

    /// <summary>
    ///     Number of bits in <paramref name="value" />, i.e. the position of its highest set bit
    ///     plus one, and zero for zero. The DLL uses a 130-entry lookup at <c>0x30038087</c> plus a
    ///     range ladder above it; we dumped that table and it equals this function on every entry.
    /// </summary>
    internal static int BitLength(int value)
    {
        var bits = 0;
        while (value > 0)
        {
            bits++;
            value >>= 1;
        }

        return bits;
    }

    /// <summary>
    ///     Clears the queue and the retirement flag ready for a new plane. Bundles are sized from
    ///     the plane geometry, which does not change between frames, so they are built once and
    ///     reset rather than reallocated per plane.
    /// </summary>
    internal void Reset()
    {
        _cursor = 0;
        _end = 0;
        IsRetired = false;
        Tree = null;
    }

    /// <summary>
    ///     Takes the next element. A retired or exhausted bundle means the plane has desynced.
    ///     ⚠ Divergence 4 of the list on <see cref="BinkVideoDecoder" />: we refuse, the DLL reads on.
    /// </summary>
    internal int Next()
    {
        if (_cursor >= _end)
        {
            throw new InvalidDataException(
                $"Bink bundle {Kind} was asked for an element it does not have — the plane has desynced.");
        }

        return _data[_cursor++];
    }

    /// <summary>
    ///     Refills the bundle if it is empty, per its own coding. Called for all nine bundles in
    ///     kind order at the start of every block row; a bundle whose queue is not empty contributes
    ///     no bits at all, so one refill routinely serves many rows.
    /// </summary>
    internal void Refill(BinkBitReader reader, BinkHuffmanTree[] colourContextTrees, ref int colourContext)
    {
        if (IsRetired || _cursor != _end)
        {
            return;
        }

        var count = (int)reader.Read(LengthBits);
        if (count == 0)
        {
            IsRetired = true;
            return;
        }

        if (count > Capacity)
        {
            // ⚠ Divergence 5 of the list on BinkVideoDecoder: the DLL has no such bound.
            throw new InvalidDataException(
                $"Bink bundle {Kind} declared {count} elements but is sized for {Capacity}.");
        }

        switch (Kind)
        {
            case BinkBundleKind.BlockTypes:
            case BinkBundleKind.SubBlockTypes:
                ReadBlockTypes(reader, count);
                break;
            case BinkBundleKind.Colours:
                ReadColours(reader, count, colourContextTrees, ref colourContext);
                break;
            case BinkBundleKind.Pattern:
                ReadPatterns(reader, count);
                break;
            case BinkBundleKind.XOffset:
            case BinkBundleKind.YOffset:
                ReadMotionComponents(reader, count);
                break;
            case BinkBundleKind.IntraDc:
            case BinkBundleKind.InterDc:
                ReadDcValues(reader, count);
                break;
            case BinkBundleKind.Run:
                ReadRunLengths(reader, count);
                break;
            default:
                throw new InvalidDataException($"Unknown Bink bundle kind {Kind}.");
        }

        _cursor = 0;
        _end = count;
    }

    private BinkHuffmanTree RequireTree()
    {
        return Tree ?? throw new InvalidOperationException($"Bink bundle {Kind} has no Huffman tree.");
    }

    private void ReadBlockTypes(BinkBitReader reader, int count)
    {
        if (reader.ReadFlag())
        {
            var value = (int)reader.Read(4);
            for (var i = 0; i < count; i++)
            {
                _data[i] = value;
            }

            return;
        }

        var tree = RequireTree();
        var last = 0;
        var at = 0;
        while (at < count)
        {
            var symbol = tree.Decode(reader);
            if (symbol < 12)
            {
                _data[at++] = symbol;
                last = symbol;
                continue;
            }

            // Symbols 12..15 repeat the previous value 4, 8, 12 or 32 times. A run may overshoot
            // the declared count; the DLL writes it into buffer slack, so we do too.
            var runLength = BinkTables.BlockTypeRunLengths[symbol - 12];
            for (var i = 0; i < runLength && at < _data.Length; i++)
            {
                _data[at++] = last;
            }
        }
    }

    /// <summary>
    ///     Colours: each element is a byte coded as two nibbles. The HIGH nibble uses one of sixteen
    ///     context trees selected by the PREVIOUS element's high nibble; the low nibble uses the
    ///     bundle's own tree. The context is reset once per plane and then carries across refills.
    /// </summary>
    private void ReadColours(
        BinkBitReader reader, int count, BinkHuffmanTree[] contextTrees, ref int context)
    {
        var tree = RequireTree();
        var allSame = reader.ReadFlag();

        var high = contextTrees[context].Decode(reader);
        context = high;
        var low = tree.Decode(reader);
        _data[0] = (high << 4) | low;

        if (allSame)
        {
            for (var i = 1; i < count; i++)
            {
                _data[i] = _data[0];
            }

            return;
        }

        for (var i = 1; i < count; i++)
        {
            high = contextTrees[context].Decode(reader);
            context = high;
            low = tree.Decode(reader);
            _data[i] = (high << 4) | low;
        }
    }

    /// <summary>
    ///     Patterns: two nibbles per element with NO mode bit, and — unlike the colour bundle — the
    ///     symbol decoded FIRST is the LOW nibble. Both orders were pinned by bit-exact decode.
    /// </summary>
    private void ReadPatterns(BinkBitReader reader, int count)
    {
        var tree = RequireTree();
        for (var i = 0; i < count; i++)
        {
            var low = tree.Decode(reader);
            var high = tree.Decode(reader);
            _data[i] = (high << 4) | low;
        }
    }

    private void ReadMotionComponents(BinkBitReader reader, int count)
    {
        if (reader.ReadFlag())
        {
            var fill = (int)reader.Read(4);
            if (fill != 0 && reader.ReadFlag())
            {
                fill = -fill;
            }

            for (var i = 0; i < count; i++)
            {
                _data[i] = fill;
            }

            return;
        }

        var tree = RequireTree();
        for (var i = 0; i < count; i++)
        {
            var value = tree.Decode(reader);
            if (value != 0 && reader.ReadFlag())
            {
                value = -value;
            }

            _data[i] = value;
        }
    }

    private void ReadRunLengths(BinkBitReader reader, int count)
    {
        if (reader.ReadFlag())
        {
            var fill = (int)reader.Read(4);
            for (var i = 0; i < count; i++)
            {
                _data[i] = fill;
            }

            return;
        }

        var tree = RequireTree();
        for (var i = 0; i < count; i++)
        {
            _data[i] = tree.Decode(reader);
        }
    }

    /// <summary>
    ///     DC values: no Huffman tree at all, but DPCM in groups of eight with a per-group bit width.
    ///     The accumulator is a 16-BIT value (the DLL stores through a <c>short*</c>) and wraps.
    /// </summary>
    private void ReadDcValues(BinkBitReader reader, int count)
    {
        int value;
        if (Kind == BinkBundleKind.IntraDc)
        {
            value = (int)reader.Read(11);
        }
        else
        {
            value = (int)reader.Read(10);
            if (value != 0 && reader.ReadFlag())
            {
                value = -value;
            }
        }

        _data[0] = value;

        var at = 1;
        var remaining = count - 1;
        while (remaining > 0)
        {
            var group = Math.Min(remaining, 8);
            var bits = (int)reader.Read(4);
            if (bits == 0)
            {
                for (var j = 0; j < group; j++)
                {
                    _data[at + j] = value;
                }
            }
            else
            {
                for (var j = 0; j < group; j++)
                {
                    var delta = (int)reader.Read(bits);
                    if (delta != 0 && reader.ReadFlag())
                    {
                        delta = -delta;
                    }

                    value = (short)(value + delta);
                    _data[at + j] = value;
                }
            }

            at += group;
            remaining -= group;
        }
    }
}
