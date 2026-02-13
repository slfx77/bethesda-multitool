using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace FalloutXbox360Utils.Core.Utils;

/// <summary>
///     Binary reading utilities for little-endian and big-endian data.
///     This is the single source of truth for endian-aware binary operations.
/// </summary>
public static class BinaryUtils
{
    #region UInt16

    /// <summary>
    ///     Read a 16-bit unsigned integer in little-endian format.
    /// </summary>
    public static ushort ReadUInt16LE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 16-bit unsigned integer in big-endian format.
    /// </summary>
    public static ushort ReadUInt16BE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadUInt16BigEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 16-bit unsigned integer with runtime endianness selection.
    /// </summary>
    public static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? ReadUInt16BE(data, offset) : ReadUInt16LE(data, offset);
    }

    /// <summary>
    ///     Read a 16-bit unsigned integer from a byte array with runtime endianness selection.
    /// </summary>
    public static ushort ReadUInt16(byte[] data, int offset, bool bigEndian)
    {
        return ReadUInt16(data.AsSpan(), offset, bigEndian);
    }

    #endregion

    #region Int16

    /// <summary>
    ///     Read a 16-bit signed integer in little-endian format.
    /// </summary>
    public static short ReadInt16LE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadInt16LittleEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 16-bit signed integer in big-endian format.
    /// </summary>
    public static short ReadInt16BE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadInt16BigEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 16-bit signed integer with runtime endianness selection.
    /// </summary>
    public static short ReadInt16(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? ReadInt16BE(data, offset) : ReadInt16LE(data, offset);
    }

    /// <summary>
    ///     Read a 16-bit signed integer from a byte array with runtime endianness selection.
    /// </summary>
    public static short ReadInt16(byte[] data, int offset, bool bigEndian)
    {
        return ReadInt16(data.AsSpan(), offset, bigEndian);
    }

    #endregion

    #region UInt32

    /// <summary>
    ///     Read a 32-bit unsigned integer in little-endian format.
    /// </summary>
    public static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 32-bit unsigned integer in big-endian format.
    /// </summary>
    public static uint ReadUInt32BE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadUInt32BigEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 32-bit unsigned integer with runtime endianness selection.
    /// </summary>
    public static uint ReadUInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? ReadUInt32BE(data, offset) : ReadUInt32LE(data, offset);
    }

    /// <summary>
    ///     Read a 32-bit unsigned integer from a byte array with runtime endianness selection.
    /// </summary>
    public static uint ReadUInt32(byte[] data, int offset, bool bigEndian)
    {
        return ReadUInt32(data.AsSpan(), offset, bigEndian);
    }

    #endregion

    #region Int32

    /// <summary>
    ///     Read a 32-bit signed integer in little-endian format.
    /// </summary>
    public static int ReadInt32LE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 32-bit signed integer in big-endian format.
    /// </summary>
    public static int ReadInt32BE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadInt32BigEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 32-bit signed integer with runtime endianness selection.
    /// </summary>
    public static int ReadInt32(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? ReadInt32BE(data, offset) : ReadInt32LE(data, offset);
    }

    /// <summary>
    ///     Read a 32-bit signed integer from a byte array with runtime endianness selection.
    /// </summary>
    public static int ReadInt32(byte[] data, int offset, bool bigEndian)
    {
        return ReadInt32(data.AsSpan(), offset, bigEndian);
    }

    #endregion

    #region UInt64

    /// <summary>
    ///     Read a 64-bit unsigned integer in little-endian format.
    /// </summary>
    public static ulong ReadUInt64LE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadUInt64LittleEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 64-bit unsigned integer in big-endian format.
    /// </summary>
    public static ulong ReadUInt64BE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadUInt64BigEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 64-bit unsigned integer with runtime endianness selection.
    /// </summary>
    public static ulong ReadUInt64(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? ReadUInt64BE(data, offset) : ReadUInt64LE(data, offset);
    }

    /// <summary>
    ///     Read a 64-bit unsigned integer from a byte array with runtime endianness selection.
    /// </summary>
    public static ulong ReadUInt64(byte[] data, int offset, bool bigEndian)
    {
        return ReadUInt64(data.AsSpan(), offset, bigEndian);
    }

    #endregion

    #region Int64

    /// <summary>
    ///     Read a 64-bit signed integer in little-endian format.
    /// </summary>
    public static long ReadInt64LE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadInt64LittleEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 64-bit signed integer in big-endian format.
    /// </summary>
    public static long ReadInt64BE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BinaryPrimitives.ReadInt64BigEndian(data.Slice(offset));
    }

    /// <summary>
    ///     Read a 64-bit signed integer with runtime endianness selection.
    /// </summary>
    public static long ReadInt64(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? ReadInt64BE(data, offset) : ReadInt64LE(data, offset);
    }

    /// <summary>
    ///     Read a 64-bit signed integer from a byte array with runtime endianness selection.
    /// </summary>
    public static long ReadInt64(byte[] data, int offset, bool bigEndian)
    {
        return ReadInt64(data.AsSpan(), offset, bigEndian);
    }

    #endregion

    #region Float

    /// <summary>
    ///     Read a 32-bit float in little-endian format.
    /// </summary>
    public static float ReadFloatLE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BitConverter.UInt32BitsToSingle(ReadUInt32LE(data, offset));
    }

    /// <summary>
    ///     Read a 32-bit float in big-endian format.
    /// </summary>
    public static float ReadFloatBE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BitConverter.UInt32BitsToSingle(ReadUInt32BE(data, offset));
    }

    /// <summary>
    ///     Read a 32-bit float with runtime endianness selection.
    /// </summary>
    public static float ReadFloat(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? ReadFloatBE(data, offset) : ReadFloatLE(data, offset);
    }

    /// <summary>
    ///     Read a 32-bit float from a byte array with runtime endianness selection.
    /// </summary>
    public static float ReadFloat(byte[] data, int offset, bool bigEndian)
    {
        return ReadFloat(data.AsSpan(), offset, bigEndian);
    }

    #endregion

    #region Double

    /// <summary>
    ///     Read a 64-bit double in little-endian format.
    /// </summary>
    public static double ReadDoubleLE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BitConverter.Int64BitsToDouble(ReadInt64LE(data, offset));
    }

    /// <summary>
    ///     Read a 64-bit double in big-endian format.
    /// </summary>
    public static double ReadDoubleBE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return BitConverter.Int64BitsToDouble(ReadInt64BE(data, offset));
    }

    /// <summary>
    ///     Read a 64-bit double with runtime endianness selection.
    /// </summary>
    public static double ReadDouble(ReadOnlySpan<byte> data, int offset, bool bigEndian)
    {
        return bigEndian ? ReadDoubleBE(data, offset) : ReadDoubleLE(data, offset);
    }

    /// <summary>
    ///     Read a 64-bit double from a byte array with runtime endianness selection.
    /// </summary>
    public static double ReadDouble(byte[] data, int offset, bool bigEndian)
    {
        return ReadDouble(data.AsSpan(), offset, bigEndian);
    }

    #endregion

    #region HalfFloat

    /// <summary>
    ///     Converts IEEE 754 half-precision (16-bit) float to single-precision (32-bit).
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

    #endregion

    #region Byte Swapping

    /// <summary>
    ///     Swap bytes for 16-bit values (Xbox 360 big-endian to little-endian).
    /// </summary>
    public static void SwapBytes16(Span<byte> data)
    {
        var ulongSpan = MemoryMarshal.Cast<byte, ulong>(data);
        for (var i = 0; i < ulongSpan.Length; i++)
        {
            var v = ulongSpan[i];
            ulongSpan[i] = ((v & 0xFF00FF00FF00FF00UL) >> 8) | ((v & 0x00FF00FF00FF00FFUL) << 8);
        }

        var remainder = data.Length % 8;
        var remainderStart = data.Length - remainder;
        for (var i = remainderStart; i < data.Length - 1; i += 2)
        {
            (data[i], data[i + 1]) = (data[i + 1], data[i]);
        }
    }

    /// <summary>
    ///     Swap bytes for 32-bit values.
    /// </summary>
    public static void SwapBytes32(Span<byte> data)
    {
        for (var i = 0; i < data.Length - 3; i += 4)
        {
            (data[i], data[i + 3]) = (data[i + 3], data[i]);
            (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
        }
    }

    #endregion

    #region Utilities

    /// <summary>
    ///     Check if data contains mostly printable ASCII text.
    /// </summary>
    public static bool IsPrintableText(ReadOnlySpan<byte> data, double minRatio = 0.8)
    {
        if (data.IsEmpty)
        {
            return false;
        }

#pragma warning disable S3267 // Loops should be simplified - intentionally avoiding LINQ for Span<T> performance
        var printableCount = 0;
        foreach (var b in data)
        {
            if (b is >= 32 and < 127 or 9 or 10 or 13)
            {
                printableCount++;
            }
        }
#pragma warning restore S3267

        return (double)printableCount / data.Length >= minRatio;
    }

    /// <summary>
    ///     Sanitize filename by removing/replacing invalid characters.
    /// </summary>
    public static string SanitizeFilename(string filename)
    {
        ArgumentNullException.ThrowIfNull(filename);

        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
        {
            filename = filename.Replace(c, '_');
        }

        return filename;
    }

    /// <summary>
    ///     Format byte size to human-readable string.
    /// </summary>
    public static string FormatSize(long sizeBytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = sizeBytes;
        var unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F2} {units[unitIndex]}";
    }

    /// <summary>
    ///     Find the next occurrence of a pattern in data.
    /// </summary>
    public static int FindPattern(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern, int start = 0)
    {
        if (pattern.IsEmpty || data.Length < pattern.Length || start + pattern.Length > data.Length)
        {
            return -1;
        }

        if (pattern.Length == 1)
        {
            var idx = data[start..].IndexOf(pattern[0]);
            return idx >= 0 ? start + idx : -1;
        }

        var firstByte = pattern[0];
        var searchSpan = data[start..];
        var searchOffset = 0;

        while (searchOffset <= searchSpan.Length - pattern.Length)
        {
            var idx = searchSpan[searchOffset..].IndexOf(firstByte);
            if (idx < 0)
            {
                return -1;
            }

            var candidateOffset = searchOffset + idx;

            // Bounds check before slicing
            if (candidateOffset + pattern.Length > searchSpan.Length)
            {
                return -1;
            }

            if (searchSpan.Slice(candidateOffset, pattern.Length).SequenceEqual(pattern))
            {
                return start + candidateOffset;
            }

            searchOffset = candidateOffset + 1;
        }

        return -1;
    }

    /// <summary>
    ///     Extract a null-terminated string from data.
    /// </summary>
    public static string? ExtractNullTerminatedString(ReadOnlySpan<byte> data, int offset = 0, int maxLength = 256)
    {
        if (offset >= data.Length)
        {
            return null;
        }

        var endOffset = Math.Min(offset + maxLength, data.Length);
        var searchSpan = data[offset..endOffset];
        var nullPos = searchSpan.IndexOf((byte)0);

        if (nullPos < 0)
        {
            return null;
        }

        var stringBytes = data.Slice(offset, nullPos);
        return !IsPrintableText(stringBytes, 0.9) ? null : Encoding.ASCII.GetString(stringBytes);
    }

    /// <summary>
    ///     Align an offset to a specific boundary.
    /// </summary>
    public static long AlignOffset(long offset, int alignment)
    {
        var remainder = offset % alignment;
        return remainder == 0 ? offset : offset + (alignment - remainder);
    }

    #endregion
}
