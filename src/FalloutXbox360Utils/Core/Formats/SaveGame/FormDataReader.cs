using System.Buffers.Binary;
using System.Text;

namespace FalloutXbox360Utils.Core.Formats.SaveGame;

// ────────────────────────────────────────────────────────────────
//  FormDataReader — sequential reader for changed form Data[] bytes
// ────────────────────────────────────────────────────────────────

/// <summary>
///     Sequential reader for changed form Data[] bytes.
///     All multi-byte values are little-endian (matching the save file format).
/// </summary>
internal ref struct FormDataReader
{
    private readonly ReadOnlySpan<byte> _data;

    /// <summary>FormID array from the save file, for resolving RefIDs to full FormIDs.</summary>
    public ReadOnlySpan<uint> FormIdArray { get; }

    public FormDataReader(ReadOnlySpan<byte> data, ReadOnlySpan<uint> formIdArray)
    {
        _data = data;
        FormIdArray = formIdArray;
        Position = 0;
    }

    public int Position { get; private set; }

    public int Remaining => _data.Length - Position;

    public bool HasData(int count)
    {
        return Position + count <= _data.Length;
    }

    /// <summary>
    ///     Skips a pipe terminator (0x7C) if present at the current position.
    ///     The FO3/NV save format terminates each typed value written via
    ///     BGSSaveGameBuffer::SaveData with a 0x7C byte.
    /// </summary>
    public void TrySkipPipe()
    {
        if (Position < _data.Length && _data[Position] == 0x7C)
        {
            Position++;
        }
    }

    public byte PeekByte()
    {
        return _data[Position];
    }

    public void Seek(int position)
    {
        Position = position;
    }

    /// <summary>
    ///     Reads a vsval (variable-sized value) from the save buffer.
    ///     Low 2 bits of first byte = size tag: 0b00 → 1 byte, 0b01 → 2 bytes, 0b10 → 4 bytes.
    ///     Actual value = decoded integer >> 2.
    /// </summary>
    public uint ReadVsval()
    {
        var first = _data[Position];
        var tag = first & 3;
        if (tag == 0)
        {
            Position++;
            return (uint)(first >> 2);
        }

        if (tag == 1)
        {
            if (!HasData(2))
            {
                Position++;
                return 0;
            }

            var raw = ReadUInt16();
            return (uint)(raw >> 2);
        }

        // tag == 2 (or 3 — treat as 4-byte)
        if (!HasData(4))
        {
            Position++;
            return 0;
        }

        var raw32 = ReadUInt32();
        return raw32 >> 2;
    }

    public byte ReadByte()
    {
        return _data[Position++];
    }

    public ushort ReadUInt16()
    {
        var value = BinaryPrimitives.ReadUInt16LittleEndian(_data[Position..]);
        Position += 2;
        return value;
    }

    public short ReadInt16()
    {
        var value = BinaryPrimitives.ReadInt16LittleEndian(_data[Position..]);
        Position += 2;
        return value;
    }

    public uint ReadUInt32()
    {
        var value = BinaryPrimitives.ReadUInt32LittleEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public int ReadInt32()
    {
        var value = BinaryPrimitives.ReadInt32LittleEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public float ReadFloat()
    {
        var value = BinaryPrimitives.ReadSingleLittleEndian(_data[Position..]);
        Position += 4;
        return value;
    }

    public SaveRefId ReadRefId()
    {
        var refId = SaveRefId.Read(_data, Position);
        Position += 3;
        return refId;
    }

    public byte[] ReadBytes(int count)
    {
        var result = _data.Slice(Position, count).ToArray();
        Position += count;
        return result;
    }

    public string ReadString(int length)
    {
        var result = Encoding.UTF8.GetString(_data.Slice(Position, length));
        Position += length;
        return result;
    }
}
