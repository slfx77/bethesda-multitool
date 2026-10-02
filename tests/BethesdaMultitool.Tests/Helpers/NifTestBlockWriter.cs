using System.Text;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     Byte sink for <see cref="NifTestFileBuilder" /> block bodies. The plain writers use the file's body order; the
///     <c>Raw*</c> / <c>*Le</c> / <c>*Be</c> writers pin an order explicitly (for the header's little-endian segment and
///     for quirk fixtures such as the little-endian BSPartFlag inside a big-endian file).
/// </summary>
internal sealed class NifTestBlockWriter
{
    private readonly List<byte> _bytes = [];

    /// <summary>Creates a writer for the given body order.</summary>
    public NifTestBlockWriter(bool bigEndian)
    {
        BigEndian = bigEndian;
    }

    /// <summary>True when the plain writers emit big-endian.</summary>
    public bool BigEndian { get; }

    /// <summary>The bytes written so far.</summary>
    public int Length => _bytes.Count;

    /// <summary>A byte.</summary>
    public NifTestBlockWriter U8(byte value)
    {
        _bytes.Add(value);
        return this;
    }

    /// <summary>A NIF bool (one byte at 20.2.0.7).</summary>
    public NifTestBlockWriter Bool(bool value)
    {
        return U8(value ? (byte)1 : (byte)0);
    }

    /// <summary>A ushort in body order.</summary>
    public NifTestBlockWriter U16(ushort value)
    {
        return BigEndian ? U16Be(value) : U16Le(value);
    }

    /// <summary>A ushort, little-endian regardless of body order.</summary>
    public NifTestBlockWriter U16Le(ushort value)
    {
        _bytes.Add((byte)value);
        _bytes.Add((byte)(value >> 8));
        return this;
    }

    /// <summary>A ushort, big-endian regardless of body order.</summary>
    public NifTestBlockWriter U16Be(ushort value)
    {
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)value);
        return this;
    }

    /// <summary>A uint in body order.</summary>
    public NifTestBlockWriter U32(uint value)
    {
        if (BigEndian)
        {
            _bytes.Add((byte)(value >> 24));
            _bytes.Add((byte)(value >> 16));
            _bytes.Add((byte)(value >> 8));
            _bytes.Add((byte)value);
        }
        else
        {
            RawU32Le(value);
        }

        return this;
    }

    /// <summary>An int in body order.</summary>
    public NifTestBlockWriter I32(int value)
    {
        return U32(unchecked((uint)value));
    }

    /// <summary>A float in body order, from its value.</summary>
    public NifTestBlockWriter F32(float value)
    {
        return U32(BitConverter.SingleToUInt32Bits(value));
    }

    /// <summary>Several floats in body order.</summary>
    public NifTestBlockWriter F32s(params float[] values)
    {
        foreach (var value in values)
        {
            F32(value);
        }

        return this;
    }

    /// <summary>A block reference (Ref/Ptr) in body order; -1 for none.</summary>
    public NifTestBlockWriter Ref(int blockIndex)
    {
        return I32(blockIndex);
    }

    /// <summary>A header string-table index (NiFixedString / string) in body order; -1 for none.</summary>
    public NifTestBlockWriter StringIndex(int index)
    {
        return I32(index);
    }

    /// <summary>A SizedString: uint length in body order, then the ASCII bytes (no terminator).</summary>
    public NifTestBlockWriter SizedString(string value)
    {
        U32((uint)value.Length);
        _bytes.AddRange(Encoding.ASCII.GetBytes(value));
        return this;
    }

    /// <summary>Raw bytes.</summary>
    public NifTestBlockWriter Bytes(byte[] value)
    {
        _bytes.AddRange(value);
        return this;
    }

    /// <summary>ASCII bytes with no length prefix.</summary>
    public NifTestBlockWriter RawAscii(string value)
    {
        _bytes.AddRange(Encoding.ASCII.GetBytes(value));
        return this;
    }

    /// <summary>A byte (header segment).</summary>
    public NifTestBlockWriter RawU8(byte value)
    {
        return U8(value);
    }

    /// <summary>A uint, little-endian regardless of body order.</summary>
    public NifTestBlockWriter RawU32Le(uint value)
    {
        _bytes.Add((byte)value);
        _bytes.Add((byte)(value >> 8));
        _bytes.Add((byte)(value >> 16));
        _bytes.Add((byte)(value >> 24));
        return this;
    }

    /// <summary>An empty BSStreamHeader ExportString: length byte 1 (the terminator) then NUL.</summary>
    public NifTestBlockWriter RawExportString()
    {
        _bytes.Add(1);
        _bytes.Add(0);
        return this;
    }

    /// <summary>The bytes written.</summary>
    public byte[] ToArray()
    {
        return [.. _bytes];
    }
}
