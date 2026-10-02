using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Modeling.Nif;

/// <summary>
///     The bounded header walk behind <see cref="NifModelReader.Probe" /> for the cut-1a key (20.2.0.7, user 11,
///     BS 14 to 34) and, since cut 2, the 20.0.0.4 <c>.kf</c> key. It starts after the BS version, reads the three
///     BSStreamHeader export strings (Author, Process Script, Export Script; BS below 103 has no Max Filepath and BS
///     below 131 no Unknown Int), the block-type table, the per-block type indices, then the sizes (20.2.0.5 and later),
///     the string table (20.1.0.1 and later) and the groups, and reports exactly where a 64 KiB prefix ran out. A
///     20.0.0.4 header has neither the Block Size array nor the string table, so its walk ends after the groups with
///     <see cref="HasBlockSizes" /> false and the footer out of the probe's reach. <c>NifParser.Parse</c> cannot be used on a prefix because it returns null both for a header that does
///     not fit and for a malformed one; this walk tells the two apart and keeps the type table it did read.
/// </summary>
/// <remarks>
///     Byte order follows NifParser and <c>NifHeaderLayout</c>: from Num Block Types on, everything is in the file's
///     body order. Plausibility limits mirror NifParser: at most 100,000 blocks, type names of at most 256 bytes,
///     strings of at most 65,536 bytes, type indices inside the type table, block sizes that fit an int.
/// </remarks>
internal sealed class NifModelProbeHeader
{
    private const uint MaximumBlockCount = 100000;
    private const uint MaximumTypeNameLength = 256;
    private const uint MaximumStringLength = 65536;

    private NifModelProbeHeader()
    {
    }

    /// <summary>How far the walk got.</summary>
    public NifModelProbeHeaderStatus Status { get; private init; }

    /// <summary>What was being read when the prefix ran out, or what is implausible; null when complete.</summary>
    public string? Problem { get; private init; }

    /// <summary>The bytes examined from the start of the content (the header end when complete).</summary>
    public int ExaminedBytes { get; private init; }

    /// <summary>One past the header (valid when <see cref="Status" /> is Complete).</summary>
    public int HeaderEnd { get; private init; }

    /// <summary>The block-type names read so far, as Latin-1 text.</summary>
    public IReadOnlyList<string> BlockTypeNames { get; private init; } = [];

    /// <summary>The per-block type indices read so far (complete only when the walk passed them).</summary>
    public IReadOnlyList<ushort> BlockTypeIndices { get; private init; } = [];

    /// <summary>The per-block sizes (complete only when the walk passed them; empty when the header stores none).</summary>
    public IReadOnlyList<uint> BlockSizes { get; private init; } = [];

    /// <summary>True when the header stores a Block Size array (20.2.0.5 and later), so a complete walk locates the footer.</summary>
    public bool HasBlockSizes { get; private init; }

    /// <summary>Walks the header from <paramref name="start" /> (the first export string) through the groups.</summary>
    /// <param name="content">The bounded prefix, starting at file offset 0.</param>
    /// <param name="start">The offset of the Author export string (one past the BS version).</param>
    /// <param name="blockCount">The header's Num Blocks.</param>
    /// <param name="bigEndian">Whether the body byte order is big-endian (endian byte 0).</param>
    /// <param name="hasBlockSizes">Whether the header stores the Block Size array (20.2.0.5 and later).</param>
    /// <param name="hasStringTable">Whether the header stores the string table (20.1.0.1 and later).</param>
    public static NifModelProbeHeader Read(ReadOnlySpan<byte> content, int start, uint blockCount, bool bigEndian,
        bool hasBlockSizes = true, bool hasStringTable = true)
    {
        var names = new List<string>();
        var indices = new List<ushort>();
        var sizes = new List<uint>();
        var cursor = new Cursor(content, start, bigEndian);

        if (blockCount > MaximumBlockCount)
        {
            return Malformed($"the header declares {blockCount} blocks (more than {MaximumBlockCount})",
                cursor.Position, names, indices, sizes);
        }

        for (var i = 0; i < 3; i++)
        {
            if (!cursor.TryU8(out var length) || !cursor.TrySkip(length))
            {
                return Exhausted("the BSStreamHeader export strings", content.Length, names, indices, sizes);
            }
        }

        if (!cursor.TryU16(out var typeCount))
        {
            return Exhausted("Num Block Types", content.Length, names, indices, sizes);
        }

        for (var i = 0; i < typeCount; i++)
        {
            if (!cursor.TryU32(out var length))
            {
                return Exhausted("the block-type table", content.Length, names, indices, sizes);
            }

            if (length > MaximumTypeNameLength)
            {
                return Malformed($"block type name {i} declares {length} bytes", cursor.Position, names, indices, sizes);
            }

            if (!cursor.TryBytes((int)length, out var name))
            {
                return Exhausted("the block-type table", content.Length, names, indices, sizes);
            }

            names.Add(Encoding.Latin1.GetString(name));
        }

        for (var i = 0; i < blockCount; i++)
        {
            if (!cursor.TryU16(out var index))
            {
                return Exhausted("the block type indices", content.Length, names, indices, sizes);
            }

            if (index >= typeCount)
            {
                return Malformed($"block {i} names type {index} of {typeCount}", cursor.Position, names, indices, sizes);
            }

            indices.Add(index);
        }

        for (var i = 0; hasBlockSizes && i < blockCount; i++)
        {
            if (!cursor.TryU32(out var size))
            {
                return Exhausted("the block sizes", content.Length, names, indices, sizes);
            }

            if (size > int.MaxValue)
            {
                return Malformed($"block {i} declares {size} bytes", cursor.Position, names, indices, sizes);
            }

            sizes.Add(size);
        }

        var stringCount = 0u;
        if (hasStringTable && (!cursor.TryU32(out stringCount) || !cursor.TryU32(out _)))
        {
            return Exhausted("the string table", content.Length, names, indices, sizes);
        }

        for (var i = 0u; i < stringCount; i++)
        {
            if (!cursor.TryU32(out var length))
            {
                return Exhausted("the string table", content.Length, names, indices, sizes);
            }

            if (length > MaximumStringLength)
            {
                return Malformed($"header string {i} declares {length} bytes", cursor.Position, names, indices, sizes);
            }

            if (!cursor.TrySkip((int)length))
            {
                return Exhausted("the string table", content.Length, names, indices, sizes);
            }
        }

        if (!cursor.TryU32(out var groupCount))
        {
            return Exhausted("the groups", content.Length, names, indices, sizes);
        }

        for (var i = 0u; i < groupCount; i++)
        {
            if (!cursor.TryU32(out _))
            {
                return Exhausted("the groups", content.Length, names, indices, sizes);
            }
        }

        return new NifModelProbeHeader
        {
            Status = NifModelProbeHeaderStatus.Complete,
            ExaminedBytes = cursor.Position,
            HeaderEnd = cursor.Position,
            BlockTypeNames = names,
            BlockTypeIndices = indices,
            BlockSizes = sizes,
            HasBlockSizes = hasBlockSizes
        };
    }

    private static NifModelProbeHeader Exhausted(string what, int contentLength, List<string> names,
        List<ushort> indices, List<uint> sizes)
    {
        return new NifModelProbeHeader
        {
            Status = NifModelProbeHeaderStatus.Exhausted,
            Problem = what,
            ExaminedBytes = contentLength,
            BlockTypeNames = names,
            BlockTypeIndices = indices,
            BlockSizes = sizes
        };
    }

    private static NifModelProbeHeader Malformed(string what, int position, List<string> names, List<ushort> indices,
        List<uint> sizes)
    {
        return new NifModelProbeHeader
        {
            Status = NifModelProbeHeaderStatus.Malformed,
            Problem = what,
            ExaminedBytes = Math.Max(1, position),
            BlockTypeNames = names,
            BlockTypeIndices = indices,
            BlockSizes = sizes
        };
    }

    /// <summary>A forward reader that reports exhaustion instead of throwing.</summary>
    private ref struct Cursor
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly bool _bigEndian;

        public Cursor(ReadOnlySpan<byte> data, int position, bool bigEndian)
        {
            _data = data;
            _bigEndian = bigEndian;
            Position = position;
        }

        public int Position { get; private set; }

        public bool TryU8(out byte value)
        {
            value = 0;
            if (Position >= _data.Length)
            {
                return false;
            }

            value = _data[Position];
            Position++;
            return true;
        }

        public bool TryU16(out ushort value)
        {
            value = 0;
            if (!TryBytes(2, out var bytes))
            {
                return false;
            }

            value = _bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes);
            return true;
        }

        public bool TryU32(out uint value)
        {
            value = 0;
            if (!TryBytes(4, out var bytes))
            {
                return false;
            }

            value = _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes);
            return true;
        }

        public bool TrySkip(int count)
        {
            return TryBytes(count, out _);
        }

        public bool TryBytes(int count, out ReadOnlySpan<byte> bytes)
        {
            bytes = default;
            if (count < 0 || count > _data.Length - Position)
            {
                return false;
            }

            bytes = _data.Slice(Position, count);
            Position += count;
            return true;
        }
    }
}
