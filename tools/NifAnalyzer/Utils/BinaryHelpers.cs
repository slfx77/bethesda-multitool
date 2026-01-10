using System.Buffers.Binary;

namespace NifAnalyzer.Utils;

/// <summary>
/// Shared binary reading helpers with endianness support.
/// </summary>
internal static class BinaryHelpers
{
    public static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    }

    public static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset, 2));
    }

    public static short ReadInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset, 2))
            : BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset, 2));
    }

    public static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }

    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset, 4));
    }

    public static int ReadInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset, 4))
            : BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, 4));
    }

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
    /// Converts IEEE 754 half-precision float to single-precision.
    /// </summary>
    public static float HalfToFloat(ushort half)
    {
        var sign = (half >> 15) & 1;
        var exp = (half >> 10) & 0x1F;
        var mant = half & 0x3FF;

        if (exp == 0)
        {
            if (mant == 0) return sign == 1 ? -0.0f : 0.0f;
            var value = (float)Math.Pow(2, -14) * (mant / 1024.0f);
            return sign == 1 ? -value : value;
        }

        if (exp == 31)
        {
            if (mant == 0) return sign == 1 ? float.NegativeInfinity : float.PositiveInfinity;
            return float.NaN;
        }

        var normalizedValue = (float)Math.Pow(2, exp - 15) * (1 + mant / 1024.0f);
        return sign == 1 ? -normalizedValue : normalizedValue;
    }

    /// <summary>
    /// Formats a NIF version number (e.g., 0x14020007 -> "20.2.0.7").
    /// </summary>
    public static string FormatVersion(uint v) => $"{v >> 24}.{(v >> 16) & 0xFF}.{(v >> 8) & 0xFF}.{v & 0xFF}";

    /// <summary>
    /// Prints a formatted hex dump to console.
    /// </summary>
    public static void HexDump(byte[] data, int offset, int length)
    {
        const int bytesPerLine = 16;
        for (int i = 0; i < length; i += bytesPerLine)
        {
            var lineOffset = offset + i;
            Console.Write($"{lineOffset:X8}  ");

            // Hex bytes
            for (int j = 0; j < bytesPerLine; j++)
            {
                if (i + j < length)
                    Console.Write($"{data[offset + i + j]:X2} ");
                else
                    Console.Write("   ");

                if (j == 7) Console.Write(" ");
            }

            Console.Write(" ");

            // ASCII
            for (int j = 0; j < bytesPerLine && i + j < length; j++)
            {
                var b = data[offset + i + j];
                Console.Write(b >= 32 && b < 127 ? (char)b : '.');
            }

            Console.WriteLine();
        }
    }
}
