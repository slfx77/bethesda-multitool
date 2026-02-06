namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Endian-aware binary reader over a byte array for script bytecode decoding.
///     Xbox 360 bytecode is big-endian; PC ESM bytecode is little-endian.
/// </summary>
public sealed class BytecodeReader
{
    private readonly byte[] _data;
    private readonly bool _isBigEndian;

    public BytecodeReader(byte[] data, bool isBigEndian)
    {
        _data = data;
        _isBigEndian = isBigEndian;
        Position = 0;
    }

    public int Position { get; set; }

    public int Length => _data.Length;
    public int Remaining => _data.Length - Position;
    public bool HasData => Position < _data.Length;

    public byte ReadByte()
    {
        if (Position >= _data.Length)
        {
            throw new EndOfStreamException($"BytecodeReader: read past end at offset {Position}");
        }

        return _data[Position++];
    }

    public byte PeekByte()
    {
        if (Position >= _data.Length)
        {
            throw new EndOfStreamException($"BytecodeReader: peek past end at offset {Position}");
        }

        return _data[Position];
    }

    /// <summary>
    ///     Peek at a byte at Position + <paramref name="offset"/> without advancing.
    /// </summary>
    public byte PeekByteAt(int offset)
    {
        var idx = Position + offset;
        if (idx < 0 || idx >= _data.Length)
        {
            throw new EndOfStreamException($"BytecodeReader: peek at offset {idx} out of range");
        }

        return _data[idx];
    }

    public byte[] ReadBytes(int count)
    {
        if (Position + count > _data.Length)
        {
            throw new EndOfStreamException(
                $"BytecodeReader: read {count} bytes past end at offset {Position}");
        }

        var result = new byte[count];
        Array.Copy(_data, Position, result, 0, count);
        Position += count;
        return result;
    }

    public ushort ReadUInt16()
    {
        if (Position + 2 > _data.Length)
        {
            throw new EndOfStreamException($"BytecodeReader: read UInt16 past end at offset {Position}");
        }

        ushort value;
        if (_isBigEndian)
        {
            value = (ushort)((_data[Position] << 8) | _data[Position + 1]);
        }
        else
        {
            value = (ushort)(_data[Position] | (_data[Position + 1] << 8));
        }

        Position += 2;
        return value;
    }

    public short ReadInt16()
    {
        return (short)ReadUInt16();
    }

    public uint ReadUInt32()
    {
        if (Position + 4 > _data.Length)
        {
            throw new EndOfStreamException($"BytecodeReader: read UInt32 past end at offset {Position}");
        }

        uint value;
        if (_isBigEndian)
        {
            value = ((uint)_data[Position] << 24) |
                    ((uint)_data[Position + 1] << 16) |
                    ((uint)_data[Position + 2] << 8) |
                    _data[Position + 3];
        }
        else
        {
            value = _data[Position] |
                    ((uint)_data[Position + 1] << 8) |
                    ((uint)_data[Position + 2] << 16) |
                    ((uint)_data[Position + 3] << 24);
        }

        Position += 4;
        return value;
    }

    public int ReadInt32()
    {
        return (int)ReadUInt32();
    }

    public double ReadDouble()
    {
        if (Position + 8 > _data.Length)
        {
            throw new EndOfStreamException($"BytecodeReader: read Double past end at offset {Position}");
        }

        // Read 8 bytes and convert respecting endianness
        var bytes = new byte[8];
        Array.Copy(_data, Position, bytes, 0, 8);
        Position += 8;

        if (_isBigEndian != !BitConverter.IsLittleEndian)
        {
            // Need to reverse — platform and data endianness differ
            Array.Reverse(bytes);
        }

        return BitConverter.ToDouble(bytes, 0);
    }

    public void Skip(int count)
    {
        Position += count;
    }

    /// <summary>
    ///     Check if there are at least <paramref name="count"/> bytes remaining.
    /// </summary>
    public bool CanRead(int count)
    {
        return Position + count <= _data.Length;
    }
}
