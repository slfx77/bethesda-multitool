using System.Buffers.Binary;

namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     Utility methods for endian conversion.
/// </summary>
internal static class NifEndianUtils
{
    /// <summary>
    ///     Swaps a 16-bit value in-place from big-endian to little-endian.
    /// </summary>
    public static void SwapUInt16InPlace(byte[] buf, int pos)
    {
        if (pos + 2 > buf.Length)
        {
            return;
        }

        (buf[pos], buf[pos + 1]) = (buf[pos + 1], buf[pos]);
    }

    /// <summary>
    ///     Swaps a 32-bit value in-place from big-endian to little-endian.
    /// </summary>
    public static void SwapUInt32InPlace(byte[] buf, int pos)
    {
        if (pos + 4 > buf.Length)
        {
            return;
        }

        (buf[pos], buf[pos + 1], buf[pos + 2], buf[pos + 3]) =
            (buf[pos + 3], buf[pos + 2], buf[pos + 1], buf[pos]);
    }

    /// <summary>
    ///     Swaps a 64-bit value in-place from big-endian to little-endian.
    /// </summary>
    public static void SwapUInt64InPlace(byte[] buf, int pos)
    {
        if (pos + 8 > buf.Length)
        {
            return;
        }

        (buf[pos], buf[pos + 1], buf[pos + 2], buf[pos + 3], buf[pos + 4], buf[pos + 5], buf[pos + 6], buf[pos + 7]) =
            (buf[pos + 7], buf[pos + 6], buf[pos + 5], buf[pos + 4], buf[pos + 3], buf[pos + 2], buf[pos + 1],
                buf[pos]);
    }

    /// <summary>
    ///     Reads a little-endian uint16 from the buffer.
    /// </summary>
    public static ushort ReadUInt16LE(byte[] buf, int pos)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(pos, 2));
    }

    /// <summary>
    ///     Reads a little-endian uint32 from the buffer.
    /// </summary>
    public static uint ReadUInt32LE(byte[] buf, int pos)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(pos, 4));
    }

    /// <summary>
    ///     Reads a uint16 with endianness support.
    /// </summary>
    public static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    }

    /// <summary>
    ///     Reads a uint16 with endianness support from a span.
    /// </summary>
    public static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    /// <summary>
    ///     Reads an int16 with endianness support from a span.
    /// </summary>
    public static short ReadInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
    }

    /// <summary>
    ///     Reads a uint32 with endianness support.
    /// </summary>
    public static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }

    /// <summary>
    ///     Reads a uint32 with endianness support from a span.
    /// </summary>
    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    /// <summary>
    ///     Reads an int32 with endianness support from a span.
    /// </summary>
    public static int ReadInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    }

    /// <summary>
    ///     Reads a float with endianness support from a span.
    /// </summary>
    public static float ReadFloat(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        if (bigEndian)
        {
            Span<byte> temp = stackalloc byte[4];
            data.Slice(offset, 4).CopyTo(temp);
            temp.Reverse();
            return BitConverter.ToSingle(temp);
        }

        return BitConverter.ToSingle(data.Slice(offset, 4));
    }

    /// <summary>
    ///     Converts IEEE 754 half-precision float to single-precision.
    /// </summary>
    public static float HalfToFloat(ushort half)
    {
        var sign = (half >> 15) & 1;
        var exp = (half >> 10) & 0x1F;
        var mant = half & 0x3FF;

        if (exp == 0)
        {
            if (mant == 0)
            {
                return sign == 1 ? -0.0f : 0.0f;
            }

            var value = (float)Math.Pow(2, -14) * (mant / 1024.0f);
            return sign == 1 ? -value : value;
        }

        if (exp == 31)
        {
            if (mant == 0)
            {
                return sign == 1 ? float.NegativeInfinity : float.PositiveInfinity;
            }

            return float.NaN;
        }

        var normalizedValue = (float)Math.Pow(2, exp - 15) * (1 + mant / 1024.0f);
        return sign == 1 ? -normalizedValue : normalizedValue;
    }

    /// <summary>
    ///     Formats a NIF version number (e.g., 0x14020007 -> "20.2.0.7").
    /// </summary>
    public static string FormatVersion(uint v)
    {
        return $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";
    }

    /// <summary>
    ///     Prints a formatted hex dump to console.
    /// </summary>
    public static void HexDump(byte[] data, int offset, int length)
    {
        const int bytesPerLine = 16;
        for (var i = 0; i < length; i += bytesPerLine)
        {
            var lineOffset = offset + i;
            Console.Write($"{lineOffset:X8}  ");

            for (var j = 0; j < bytesPerLine; j++)
            {
                if (i + j < length)
                {
                    Console.Write($"{data[offset + i + j]:X2} ");
                }
                else
                {
                    Console.Write("   ");
                }

                if (j == 7)
                {
                    Console.Write(" ");
                }
            }

            Console.Write(" ");

            for (var j = 0; j < bytesPerLine && i + j < length; j++)
            {
                var b = data[offset + i + j];
                Console.Write(b >= 32 && b < 127 ? (char)b : '.');
            }

            Console.WriteLine();
        }
    }
}
