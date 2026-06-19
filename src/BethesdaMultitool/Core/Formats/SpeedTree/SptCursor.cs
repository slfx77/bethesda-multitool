using System.Numerics;
using System.Text;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.SpeedTree;

/// <summary>
///     Forward-only reader over a SpeedTree <c>.spt</c> ("__IdvSpt_02_") little-endian token stream.
///     Mirrors the SpeedTreeRT primitives reverse-engineered from Geck.exe (x86):
///     <list type="bullet">
///         <item><c>ReadToken</c> = uint32 LE, advance 4 (FUN_008E81C0).</item>
///         <item><c>PeekToken</c> = uint32 LE, no advance (FUN_008E8420).</item>
///         <item><c>ReadFloat</c> = float32 LE, advance 4 (FUN_008E8160).</item>
///         <item><c>ReadVec3</c> = three consecutive floats (FUN_008E83C0).</item>
///         <item><c>ReadByte</c> = one raw byte, advance 1 (FUN_008B2550).</item>
///         <item><c>ReadString</c> = length token then N raw bytes, byte-aligned, NOT padded (FUN_008E8280).</item>
///     </list>
///     Sections are <c>begin = N</c> / <c>end = N + 1</c> token pairs.
/// </summary>
internal sealed class SptCursor
{
    private readonly byte[] _data;

    public SptCursor(byte[] data, int offset = 0)
    {
        _data = data ?? throw new ArgumentNullException(nameof(data));
        Position = offset;
    }

    /// <summary>Current absolute byte offset into the backing buffer.</summary>
    public int Position { get; private set; }

    /// <summary>Total length of the backing buffer.</summary>
    public int Length => _data.Length;

    /// <summary>True when at least <paramref name="count" /> more bytes are available.</summary>
    public bool HasBytes(int count) => Position + count <= _data.Length;

    /// <summary>Read a 4-byte little-endian token and advance.</summary>
    public uint ReadToken()
    {
        EnsureBytes(4);
        var value = BinaryUtils.ReadUInt32LE(_data, Position);
        Position += 4;
        return value;
    }

    /// <summary>Read a 4-byte little-endian token WITHOUT advancing.</summary>
    public uint PeekToken()
    {
        EnsureBytes(4);
        return BinaryUtils.ReadUInt32LE(_data, Position);
    }

    /// <summary>Read a signed 4-byte little-endian int and advance.</summary>
    public int ReadInt32()
    {
        EnsureBytes(4);
        var value = BinaryUtils.ReadInt32LE(_data, Position);
        Position += 4;
        return value;
    }

    /// <summary>Read a 4-byte little-endian IEEE float and advance.</summary>
    public float ReadFloat()
    {
        EnsureBytes(4);
        var value = BinaryUtils.ReadFloatLE(_data, Position);
        Position += 4;
        return value;
    }

    /// <summary>Read three consecutive little-endian floats and advance 12 bytes.</summary>
    public Vector3 ReadVec3()
    {
        EnsureBytes(12);
        var x = BinaryUtils.ReadFloatLE(_data, Position);
        var y = BinaryUtils.ReadFloatLE(_data, Position + 4);
        var z = BinaryUtils.ReadFloatLE(_data, Position + 8);
        Position += 12;
        return new Vector3(x, y, z);
    }

    /// <summary>Read a single raw byte and advance 1.</summary>
    public byte ReadByte()
    {
        EnsureBytes(1);
        return _data[Position++];
    }

    /// <summary>Read a boolean stored as a single byte and advance 1.</summary>
    public bool ReadBool() => ReadByte() != 0;

    /// <summary>
    ///     Read a SpeedTree string: a length token (uint32 LE) followed by that many raw ASCII bytes,
    ///     byte-aligned with no padding. Returns an empty string for a zero length.
    /// </summary>
    public string ReadString()
    {
        var length = (int)ReadToken();
        if (length == 0)
        {
            return string.Empty;
        }

        if (length < 0)
        {
            throw new InvalidDataException($"SPT: negative string length {length} at offset {Position - 4}.");
        }

        EnsureBytes(length);
        var value = Encoding.ASCII.GetString(_data, Position, length);
        Position += length;
        return value;
    }

    /// <summary>Skip <paramref name="count" /> bytes.</summary>
    public void Skip(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        EnsureBytes(count);
        Position += count;
    }

    /// <summary>
    ///     Read the next token and assert it equals <paramref name="expected" />; otherwise throw with
    ///     <paramref name="what" /> in the message.
    /// </summary>
    public void Expect(uint expected, string what)
    {
        var actual = ReadToken();
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"SPT: expected {what} token 0x{expected:X} but read 0x{actual:X} at offset {Position - 4}.");
        }
    }

    private void EnsureBytes(int count)
    {
        if (Position + count > _data.Length)
        {
            throw new InvalidDataException(
                $"SPT: unexpected end of stream (need {count} bytes at offset {Position}, length {_data.Length}).");
        }
    }
}
