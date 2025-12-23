using System.Runtime.InteropServices;

namespace Xbox360MemoryCarver.Core.Utils;

/// <summary>
/// Binary reading utilities for little-endian and big-endian data.
/// </summary>
public static class BinaryUtils
{
    /// <summary>
    /// Read a 32-bit unsigned integer in little-endian format.
    /// </summary>
    public static uint ReadUInt32LE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return (uint)(data[offset] | (data[offset + 1] << 8) | (data[offset + 2] << 16) | (data[offset + 3] << 24));
    }

    /// <summary>
    /// Read a 32-bit unsigned integer in big-endian format.
    /// </summary>
    public static uint ReadUInt32BE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return (uint)((data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3]);
    }

    /// <summary>
    /// Read a 16-bit unsigned integer in little-endian format.
    /// </summary>
    public static ushort ReadUInt16LE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return (ushort)(data[offset] | (data[offset + 1] << 8));
    }

    /// <summary>
    /// Read a 16-bit unsigned integer in big-endian format.
    /// </summary>
    public static ushort ReadUInt16BE(ReadOnlySpan<byte> data, int offset = 0)
    {
        return (ushort)((data[offset] << 8) | data[offset + 1]);
    }

    /// <summary>
    /// Check if data contains mostly printable ASCII text.
    /// </summary>
    public static bool IsPrintableText(ReadOnlySpan<byte> data, double minRatio = 0.8)
    {
        if (data.IsEmpty) return false;

        int printableCount = 0;
        foreach (var b in data)
        {
            if ((b >= 32 && b < 127) || b == 9 || b == 10 || b == 13)
            {
                printableCount++;
            }
        }

        return (double)printableCount / data.Length >= minRatio;
    }

    /// <summary>
    /// Sanitize filename by removing/replacing invalid characters.
    /// </summary>
    public static string SanitizeFilename(string filename)
    {
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var c in invalid)
        {
            filename = filename.Replace(c, '_');
        }
        return filename;
    }

    /// <summary>
    /// Format byte size to human-readable string.
    /// </summary>
    public static string FormatSize(long sizeBytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = sizeBytes;
        int unitIndex = 0;

        while (size >= 1024 && unitIndex < units.Length - 1)
        {
            size /= 1024;
            unitIndex++;
        }

        return $"{size:F2} {units[unitIndex]}";
    }

    /// <summary>
    /// Find the next occurrence of a pattern in data.
    /// </summary>
    public static int FindPattern(ReadOnlySpan<byte> data, ReadOnlySpan<byte> pattern, int start = 0)
    {
        if (pattern.IsEmpty || start + pattern.Length > data.Length)
            return -1;

        if (pattern.Length == 1)
        {
            int idx = data[start..].IndexOf(pattern[0]);
            return idx >= 0 ? start + idx : -1;
        }

        byte firstByte = pattern[0];
        var searchSpan = data[start..];
        int searchOffset = 0;

        while (searchOffset <= searchSpan.Length - pattern.Length)
        {
            int idx = searchSpan[searchOffset..].IndexOf(firstByte);
            if (idx < 0)
                return -1;

            int candidateOffset = searchOffset + idx;

            if (searchSpan.Slice(candidateOffset, pattern.Length).SequenceEqual(pattern))
            {
                return start + candidateOffset;
            }

            searchOffset = candidateOffset + 1;
        }

        return -1;
    }

    /// <summary>
    /// Extract a null-terminated string from data.
    /// </summary>
    public static string? ExtractNullTerminatedString(ReadOnlySpan<byte> data, int offset = 0, int maxLength = 256)
    {
        if (offset >= data.Length) return null;

        int endOffset = Math.Min(offset + maxLength, data.Length);
        var searchSpan = data[offset..endOffset];
        int nullPos = searchSpan.IndexOf((byte)0);

        if (nullPos < 0) return null;

        var stringBytes = data.Slice(offset, nullPos);
        if (!IsPrintableText(stringBytes, 0.9)) return null;

        return System.Text.Encoding.ASCII.GetString(stringBytes);
    }

    /// <summary>
    /// Align an offset to a specific boundary.
    /// </summary>
    public static long AlignOffset(long offset, int alignment)
    {
        long remainder = offset % alignment;
        return remainder == 0 ? offset : offset + (alignment - remainder);
    }

    /// <summary>
    /// Swap bytes for 16-bit values (Xbox 360 big-endian to little-endian).
    /// </summary>
    public static void SwapBytes16(Span<byte> data)
    {
        var ulongSpan = MemoryMarshal.Cast<byte, ulong>(data);
        for (int i = 0; i < ulongSpan.Length; i++)
        {
            ulong v = ulongSpan[i];
            ulongSpan[i] = ((v & 0xFF00FF00FF00FF00UL) >> 8) | ((v & 0x00FF00FF00FF00FFUL) << 8);
        }

        int remainder = data.Length % 8;
        int remainderStart = data.Length - remainder;
        for (int i = remainderStart; i < data.Length - 1; i += 2)
        {
            (data[i], data[i + 1]) = (data[i + 1], data[i]);
        }
    }

    /// <summary>
    /// Swap bytes for 32-bit values.
    /// </summary>
    public static void SwapBytes32(Span<byte> data)
    {
        for (int i = 0; i < data.Length - 3; i += 4)
        {
            (data[i], data[i + 3]) = (data[i + 3], data[i]);
            (data[i + 1], data[i + 2]) = (data[i + 2], data[i + 1]);
        }
    }
}
