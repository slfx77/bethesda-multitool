using System.Text;

namespace FalloutXbox360Utils.Core.Utils;

/// <summary>
///     String reading utilities for ESM/BSA files and Xbox 360 memory dumps.
/// </summary>
public static class EsmStringUtils
{
    /// <summary>
    ///     Default threshold for printable ASCII validation (80%).
    /// </summary>
    public const float DefaultPrintableThreshold = 0.8f;

    /// <summary>
    ///     Maximum reasonable string length for BSStringT validation.
    /// </summary>
    public const int MaxBSStringLength = 4096;

    /// <summary>
    ///     Read a null-terminated string from a span of bytes using UTF-8 encoding.
    /// </summary>
    public static string ReadNullTermString(ReadOnlySpan<byte> data)
    {
        var length = data.IndexOf((byte)0);
        if (length < 0)
        {
            length = data.Length;
        }

        return Encoding.UTF8.GetString(data[..length]);
    }

    /// <summary>
    ///     Read a null-terminated string from a byte array using ASCII encoding.
    /// </summary>
    public static string ReadNullTermString(byte[] data, int offset, int maxLen)
    {
        var end = offset;
        while (end < offset + maxLen && end < data.Length && data[end] != 0)
        {
            end++;
        }

        return Encoding.ASCII.GetString(data, offset, end - offset);
    }

    /// <summary>
    ///     Check if data contains mostly printable ASCII characters.
    /// </summary>
    public static bool IsPrintableAscii(ReadOnlySpan<byte> data, float threshold = DefaultPrintableThreshold)
    {
        if (data.IsEmpty)
        {
            return false;
        }

        var printable = 0;
        foreach (var b in data)
        {
            if (b is >= 32 and <= 126 or (byte)'\n' or (byte)'\r' or (byte)'\t')
            {
                printable++;
            }
        }

        return printable >= data.Length * threshold;
    }

    /// <summary>
    ///     Validate and decode a string buffer as ASCII if it meets the printable threshold.
    /// </summary>
    public static string? ValidateAndDecodeAscii(byte[] buffer, int length, float threshold = DefaultPrintableThreshold)
    {
        if (length <= 0 || length > buffer.Length)
        {
            return null;
        }

        if (!IsPrintableAscii(buffer.AsSpan(0, length), threshold))
        {
            return null;
        }

        return Encoding.ASCII.GetString(buffer, 0, length);
    }
}
