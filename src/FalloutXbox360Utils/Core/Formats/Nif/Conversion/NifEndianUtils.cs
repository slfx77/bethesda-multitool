namespace FalloutXbox360Utils.Core.Formats.Nif.Conversion;

/// <summary>
///     NIF-specific endian utilities: in-place byte swapping, bulk swap, version formatting, and hex dump.
///     Generic read methods live in <see cref="Core.Utils.BinaryUtils" />.
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
    ///     Bulk-swaps 32-bit values in-place from big-endian to little-endian.
    /// </summary>
    public static void BulkSwap32(byte[] buf, int start, int size)
    {
        var end = Math.Min(start + size, buf.Length - 3);
        for (var i = start; i < end; i += 4)
        {
            SwapUInt32InPlace(buf, i);
        }
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
