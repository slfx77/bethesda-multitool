using System.Buffers.Binary;
using System.Text;

namespace BethesdaMultitool.Core.Formats.Esm.Tes3;

/// <summary>
///     A forward-only little-endian reader over a TES3 subrecord's bytes. Every read is
///     bounds-checked; reading past the end yields a zero/empty value and leaves the cursor at the
///     end, so a malformed or short subrecord degrades gracefully instead of throwing.
/// </summary>
internal ref struct Tes3Cursor(ReadOnlySpan<byte> data)
{
    private readonly ReadOnlySpan<byte> _data = data;
    private int _pos;

    public readonly int Remaining => _data.Length - _pos;

    public readonly bool AtEnd => _pos >= _data.Length;

    public int ReadInt32()
    {
        if (_pos + 4 > _data.Length)
        {
            _pos = _data.Length;
            return 0;
        }

        var v = BinaryPrimitives.ReadInt32LittleEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public uint ReadUInt32()
    {
        if (_pos + 4 > _data.Length)
        {
            _pos = _data.Length;
            return 0;
        }

        var v = BinaryPrimitives.ReadUInt32LittleEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public short ReadInt16()
    {
        if (_pos + 2 > _data.Length)
        {
            _pos = _data.Length;
            return 0;
        }

        var v = BinaryPrimitives.ReadInt16LittleEndian(_data[_pos..]);
        _pos += 2;
        return v;
    }

    public ushort ReadUInt16()
    {
        if (_pos + 2 > _data.Length)
        {
            _pos = _data.Length;
            return 0;
        }

        var v = BinaryPrimitives.ReadUInt16LittleEndian(_data[_pos..]);
        _pos += 2;
        return v;
    }

    public byte ReadByte()
    {
        if (_pos + 1 > _data.Length)
        {
            _pos = _data.Length;
            return 0;
        }

        return _data[_pos++];
    }

    public sbyte ReadInt8()
    {
        return (sbyte)ReadByte();
    }

    public float ReadFloat()
    {
        if (_pos + 4 > _data.Length)
        {
            _pos = _data.Length;
            return 0f;
        }

        var v = BinaryPrimitives.ReadSingleLittleEndian(_data[_pos..]);
        _pos += 4;
        return v;
    }

    public void Skip(int count)
    {
        _pos = Math.Min(_data.Length, _pos + count);
    }

    /// <summary>Read a fixed-width char field, trimming at the first NUL and of trailing whitespace.</summary>
    public string ReadFixedString(int length)
    {
        if (length <= 0 || _pos >= _data.Length)
        {
            _pos = Math.Min(_data.Length, _pos + Math.Max(0, length));
            return string.Empty;
        }

        var avail = Math.Min(length, _data.Length - _pos);
        var slice = _data.Slice(_pos, avail);
        _pos += length; // advance by the declared width even if it overruns the buffer
        _pos = Math.Min(_pos, _data.Length);
        return DecodeAscii(slice);
    }

    /// <summary>Read everything remaining as a NUL-trimmed string (variable-length text fields).</summary>
    public string ReadRemainingString()
    {
        var slice = _data[_pos..];
        _pos = _data.Length;
        return DecodeAscii(slice);
    }

    private static string DecodeAscii(ReadOnlySpan<byte> bytes)
    {
        var nul = bytes.IndexOf((byte)0);
        if (nul >= 0)
        {
            bytes = bytes[..nul];
        }

        return Encoding.ASCII.GetString(bytes).TrimEnd();
    }
}
