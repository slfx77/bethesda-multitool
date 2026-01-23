using System.Buffers.Binary;

namespace EsmAnalyzer.Core;

/// <summary>
///     Binary reading utilities with endian awareness for ESM file parsing.
/// </summary>
public static class EsmBinary
{
    /// <summary>
    ///     Reads a 16-bit unsigned integer from the specified offset.
    /// </summary>
    public static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    /// <summary>
    ///     Reads a 16-bit unsigned integer from a byte array.
    /// </summary>
    public static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return ReadUInt16(data.AsSpan(), offset, bigEndian);
    }

    /// <summary>
    ///     Reads a 16-bit signed integer from the specified offset.
    /// </summary>
    public static short ReadInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
    }

    /// <summary>
    ///     Reads a 16-bit signed integer from a byte array.
    /// </summary>
    public static short ReadInt16(byte[] data, int offset, bool bigEndian)
    {
        return ReadInt16(data.AsSpan(), offset, bigEndian);
    }

    /// <summary>
    ///     Reads a 32-bit unsigned integer from the specified offset.
    /// </summary>
    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    /// <summary>
    ///     Reads a 32-bit unsigned integer from a byte array.
    /// </summary>
    public static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return ReadUInt32(data.AsSpan(), offset, bigEndian);
    }

    /// <summary>
    ///     Reads a 32-bit signed integer from the specified offset.
    /// </summary>
    public static int ReadInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    }

    /// <summary>
    ///     Reads a 32-bit signed integer from a byte array.
    /// </summary>
    public static int ReadInt32(byte[] data, int offset, bool bigEndian)
    {
        return ReadInt32(data.AsSpan(), offset, bigEndian);
    }

    /// <summary>
    ///     Reads a 32-bit floating point value from the specified offset.
    /// </summary>
    public static float ReadSingle(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        var intValue = ReadInt32(data, offset, bigEndian);
        return BitConverter.Int32BitsToSingle(intValue);
    }

    /// <summary>
    ///     Reads a 32-bit floating point value from a byte array.
    /// </summary>
    public static float ReadSingle(byte[] data, int offset, bool bigEndian)
    {
        return ReadSingle(data.AsSpan(), offset, bigEndian);
    }

    /// <summary>
    ///     Reads a 64-bit unsigned integer from the specified offset.
    /// </summary>
    public static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset, 8))
            : BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset, 8));
    }

    /// <summary>
    ///     Reads a 64-bit unsigned integer from a byte array.
    /// </summary>
    public static ulong ReadUInt64(byte[] data, int offset, bool bigEndian)
    {
        return ReadUInt64(data.AsSpan(), offset, bigEndian);
    }

    /// <summary>
    ///     Reads a 64-bit signed integer from the specified offset.
    /// </summary>
    public static long ReadInt64(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset, 8))
            : BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset, 8));
    }

    /// <summary>
    ///     Reads a 64-bit signed integer from a byte array.
    /// </summary>
    public static long ReadInt64(byte[] data, int offset, bool bigEndian)
    {
        return ReadInt64(data.AsSpan(), offset, bigEndian);
    }

    /// <summary>
    ///     Reads a 64-bit double precision floating point value from the specified offset.
    /// </summary>
    public static double ReadDouble(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        var longValue = ReadInt64(data, offset, bigEndian);
        return BitConverter.Int64BitsToDouble(longValue);
    }

    /// <summary>
    ///     Reads a 64-bit double precision floating point value from a byte array.
    /// </summary>
    public static double ReadDouble(byte[] data, int offset, bool bigEndian)
    {
        return ReadDouble(data.AsSpan(), offset, bigEndian);
    }
}
