using System.Buffers.Binary;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Builds synthetic NIF animation bytes in either byte order, one field at a time, so a test states each stored word
///     (usually as raw bits) and the reader under test must find it at the right place.
/// </summary>
internal sealed class NifAnimationByteWriter
{
    private readonly List<byte> _bytes = [];

    /// <summary>Creates a writer for the given byte order.</summary>
    public NifAnimationByteWriter(bool bigEndian)
    {
        BigEndian = bigEndian;
    }

    /// <summary>True when words are written big-endian.</summary>
    public bool BigEndian { get; }

    /// <summary>The number of bytes written so far (the next field's offset).</summary>
    public int Length => _bytes.Count;

    /// <summary>Appends one byte.</summary>
    public NifAnimationByteWriter U8(byte value)
    {
        _bytes.Add(value);
        return this;
    }

    /// <summary>Appends an unsigned 16-bit word.</summary>
    public NifAnimationByteWriter U16(ushort value)
    {
        Span<byte> buffer = stackalloc byte[2];
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt16BigEndian(buffer, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt16LittleEndian(buffer, value);
        }

        _bytes.AddRange(buffer.ToArray());
        return this;
    }

    /// <summary>Appends a signed 16-bit word.</summary>
    public NifAnimationByteWriter I16(short value)
    {
        return U16(unchecked((ushort)value));
    }

    /// <summary>Appends an unsigned 32-bit word (a count, a key type, or a float's raw bits).</summary>
    public NifAnimationByteWriter U32(uint value)
    {
        Span<byte> buffer = stackalloc byte[4];
        if (BigEndian)
        {
            BinaryPrimitives.WriteUInt32BigEndian(buffer, value);
        }
        else
        {
            BinaryPrimitives.WriteUInt32LittleEndian(buffer, value);
        }

        _bytes.AddRange(buffer.ToArray());
        return this;
    }

    /// <summary>Appends a signed 32-bit word (a ref or a string index).</summary>
    public NifAnimationByteWriter I32(int value)
    {
        return U32(unchecked((uint)value));
    }

    /// <summary>Appends a float by its bits.</summary>
    public NifAnimationByteWriter F32(float value)
    {
        return U32(BitConverter.SingleToUInt32Bits(value));
    }

    /// <summary>Appends several 32-bit words in order.</summary>
    public NifAnimationByteWriter Words(params uint[] values)
    {
        foreach (var value in values)
        {
            U32(value);
        }

        return this;
    }

    /// <summary>Appends raw bytes as they are.</summary>
    public NifAnimationByteWriter Raw(params byte[] values)
    {
        _bytes.AddRange(values);
        return this;
    }

    /// <summary>A copy of the bytes written so far.</summary>
    public byte[] ToArray()
    {
        return [.. _bytes];
    }
}
