using System.Buffers.Binary;

namespace BethesdaMultitool.Core.Formats.Nif.Decoding;

/// <summary>
///     A read-only, bounded cursor over one block body (or over a small re-ordered unit, see
///     <see cref="NifDecodeQuirks" />). Every read checks the remaining bytes first and raises a
///     <see cref="NifDecodeFailureKind.Data" /> fault instead of reading past the end. Nothing is ever written.
/// </summary>
internal sealed class NifByteCursor
{
    private readonly ReadOnlyMemory<byte> _data;
    private readonly int _absoluteBase;

    /// <summary>Creates a cursor over <c>data[start..end)</c>.</summary>
    /// <param name="data">The backing bytes.</param>
    /// <param name="start">The first readable index into <paramref name="data" />.</param>
    /// <param name="end">One past the last readable index.</param>
    /// <param name="bigEndian">Whether multi-byte values are stored big-endian.</param>
    /// <param name="absoluteBase">
    ///     The absolute file offset that index 0 of <paramref name="data" /> stands for (0 for the file itself).
    /// </param>
    public NifByteCursor(ReadOnlyMemory<byte> data, int start, int end, bool bigEndian, int absoluteBase = 0)
    {
        if (start < 0 || end < start || end > data.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(end), $"[{start}, {end}) is outside {data.Length} bytes.");
        }

        _data = data;
        Position = start;
        End = end;
        BigEndian = bigEndian;
        _absoluteBase = absoluteBase;
    }

    /// <summary>The next index to read.</summary>
    public int Position { get; private set; }

    /// <summary>One past the last readable index.</summary>
    public int End { get; }

    /// <summary>Whether multi-byte values are big-endian.</summary>
    public bool BigEndian { get; }

    /// <summary>Bytes left before <see cref="End" />.</summary>
    public int Remaining => End - Position;

    /// <summary>The absolute file offset of <see cref="Position" />.</summary>
    public int AbsolutePosition => _absoluteBase + Position;

    /// <summary>Consumes <paramref name="count" /> bytes and returns them.</summary>
    public ReadOnlySpan<byte> Take(int count)
    {
        Require(count);
        var span = _data.Span.Slice(Position, count);
        Position += count;
        return span;
    }

    /// <summary>
    ///     Reads an unsigned integer of 1, 2, 4 or 8 bytes in the cursor's byte order, or little-endian when
    ///     <paramref name="forceLittleEndian" /> is set (the BSPartFlag quirk and <c>ulittle32</c>).
    /// </summary>
    public ulong ReadUnsigned(int width, bool forceLittleEndian = false)
    {
        var bytes = Take(width);
        var bigEndian = BigEndian && !forceLittleEndian;
        return width switch
        {
            1 => bytes[0],
            2 => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(bytes) : BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            4 => bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(bytes) : BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            8 => bigEndian ? BinaryPrimitives.ReadUInt64BigEndian(bytes) : BinaryPrimitives.ReadUInt64LittleEndian(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(width), width, "Integer width must be 1, 2, 4 or 8.")
        };
    }

    /// <summary>Reads a uint16 in the cursor's byte order.</summary>
    public ushort ReadUInt16()
    {
        return (ushort)ReadUnsigned(2);
    }

    /// <summary>Reads a uint32 in the cursor's byte order.</summary>
    public uint ReadUInt32()
    {
        return (uint)ReadUnsigned(4);
    }

    /// <summary>Reads an int32 in the cursor's byte order.</summary>
    public int ReadInt32()
    {
        return unchecked((int)ReadUInt32());
    }

    /// <summary>Raises a data fault unless <paramref name="count" /> bytes remain.</summary>
    public void Require(int count)
    {
        if (count < 0 || count > Remaining)
        {
            throw new NifDecodeFault(NifDecodeFailureKind.Data,
                $"needs {count} byte(s) but only {Remaining} remain in the block");
        }
    }
}
