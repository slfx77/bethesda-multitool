using System.Buffers.Binary;

namespace Xbox360MemoryCarver.Core.Formats.Nif;

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
}
