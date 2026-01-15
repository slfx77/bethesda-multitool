using System.Buffers.Binary;

namespace TextureAnalyzer.Utils;

/// <summary>
///     Shared binary reading helpers with endianness support.
/// </summary>
internal static class BinaryHelpers
{
    public static ushort ReadUInt16BE(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
    }

    public static ushort ReadUInt16LE(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset));
    }

    public static uint ReadUInt32BE(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset));
    }

    public static uint ReadUInt32LE(byte[] data, int offset)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset));
    }

    /// <summary>
    ///     Print formatted hex dump to console.
    /// </summary>
    public static void HexDump(byte[] data, int offset, int length)
    {
        const int bytesPerLine = 16;

        for (var i = 0; i < length; i += bytesPerLine)
        {
            var lineOffset = offset + i;
            var lineLength = Math.Min(bytesPerLine, length - i);

            // Offset
            Console.Write($"{lineOffset:X8}  ");

            // Hex bytes
            for (var j = 0; j < bytesPerLine; j++)
            {
                if (j < lineLength)
                    Console.Write($"{data[lineOffset + j]:X2} ");
                else
                    Console.Write("   ");

                if (j == 7) Console.Write(" ");
            }

            Console.Write(" ");

            // ASCII
            for (var j = 0; j < lineLength; j++)
            {
                var b = data[lineOffset + j];
                Console.Write(b is >= 32 and < 127 ? (char)b : '.');
            }

            Console.WriteLine();
        }
    }
}
