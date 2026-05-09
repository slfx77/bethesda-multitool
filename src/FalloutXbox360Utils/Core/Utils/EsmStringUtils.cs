namespace FalloutXbox360Utils.Core.Utils;

/// <summary>
///     String reading utilities for ESM/BSA files and Xbox 360 memory dumps.
/// </summary>
public static class EsmStringUtils
{
    /// <summary>
    ///     Default threshold for printable game-text validation (80%).
    /// </summary>
    public const float DefaultPrintableThreshold = 0.8f;

    /// <summary>
    ///     Maximum reasonable string length for BSStringT validation.
    /// </summary>
    public const int MaxBSStringLength = 4096;

    /// <summary>
    ///     Read a null-terminated game-authored string from a span of bytes.
    ///     Fallout 3/New Vegas ESM and runtime text is Windows-1252, not UTF-8.
    /// </summary>
    public static string ReadNullTermString(ReadOnlySpan<byte> data)
    {
        var length = data.IndexOf((byte)0);
        if (length < 0)
        {
            length = data.Length;
        }

        return DecodeGameText(data[..length]);
    }

    /// <summary>
    ///     Read a null-terminated game-authored string from a byte array.
    /// </summary>
    public static string ReadNullTermString(byte[] data, int offset, int maxLen)
    {
        var end = offset;
        while (end < offset + maxLen && end < data.Length && data[end] != 0)
        {
            end++;
        }

        return DecodeGameText(data.AsSpan(offset, end - offset));
    }

    /// <summary>
    ///     Decode game-authored text bytes as Windows-1252.
    /// </summary>
    public static string DecodeGameText(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var chars = new char[data.Length];
        for (var i = 0; i < data.Length; i++)
        {
            chars[i] = DecodeWindows1252Byte(data[i]);
        }

        return new string(chars);
    }

    /// <summary>
    ///     Check if data contains mostly printable Windows-1252 game-text characters.
    /// </summary>
    public static bool IsPrintableGameText(ReadOnlySpan<byte> data, float threshold = DefaultPrintableThreshold)
    {
        if (data.IsEmpty)
        {
            return false;
        }

        var printable = 0;
        for (var i = 0; i < data.Length; i++)
        {
            if (IsPrintableGameTextByte(data[i]))
            {
                printable++;
            }
        }

        return printable >= data.Length * threshold;
    }

    /// <summary>
    ///     Backward-compatible alias for callers that still mean technical ASCII.
    /// </summary>
    public static bool IsPrintableAscii(ReadOnlySpan<byte> data, float threshold = DefaultPrintableThreshold)
    {
        return IsPrintableGameText(data, threshold);
    }

    /// <summary>
    ///     Validate and decode a string buffer as Windows-1252 game text if it meets the printable threshold.
    /// </summary>
    public static string? ValidateAndDecodeAscii(byte[] buffer, int length, float threshold = DefaultPrintableThreshold)
    {
        return ValidateAndDecodeGameText(buffer, length, threshold);
    }

    /// <summary>
    ///     Validate and decode a string buffer as Windows-1252 game text.
    /// </summary>
    public static string? ValidateAndDecodeGameText(byte[] buffer, int length,
        float threshold = DefaultPrintableThreshold)
    {
        if (length <= 0 || length > buffer.Length)
        {
            return null;
        }

        if (!IsPrintableGameText(buffer.AsSpan(0, length), threshold))
        {
            return null;
        }

        var decoded = DecodeGameText(buffer.AsSpan(0, length));
        return IsPlausibleGameText(decoded) ? decoded : null;
    }

    /// <summary>
    ///     Returns true for bytes that can be safely rendered as game text.
    /// </summary>
    public static bool IsPrintableGameTextByte(byte value)
    {
        return value is >= 0x20 and <= 0x7E
            or >= 0xA0
            or (byte)'\n'
            or (byte)'\r'
            or (byte)'\t'
            or 0x80
            or 0x82
            or 0x83
            or 0x84
            or 0x85
            or 0x86
            or 0x87
            or 0x88
            or 0x89
            or 0x8A
            or 0x8B
            or 0x8C
            or 0x8E
            or 0x91
            or 0x92
            or 0x93
            or 0x94
            or 0x95
            or 0x96
            or 0x97
            or 0x98
            or 0x99
            or 0x9A
            or 0x9B
            or 0x9C
            or 0x9E
            or 0x9F;
    }

    private static char DecodeWindows1252Byte(byte value)
    {
        return value switch
        {
            0x80 => '\u20AC',
            0x82 => '\u201A',
            0x83 => '\u0192',
            0x84 => '\u201E',
            0x85 => '\u2026',
            0x86 => '\u2020',
            0x87 => '\u2021',
            0x88 => '\u02C6',
            0x89 => '\u2030',
            0x8A => '\u0160',
            0x8B => '\u2039',
            0x8C => '\u0152',
            0x8E => '\u017D',
            0x91 => '\u2018',
            0x92 => '\u2019',
            0x93 => '\u201C',
            0x94 => '\u201D',
            0x95 => '\u2022',
            0x96 => '\u2013',
            0x97 => '\u2014',
            0x98 => '\u02DC',
            0x99 => '\u2122',
            0x9A => '\u0161',
            0x9B => '\u203A',
            0x9C => '\u0153',
            0x9E => '\u017E',
            0x9F => '\u0178',
            _ => (char)value
        };
    }

    private static bool IsPlausibleGameText(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var suspicious = 0;
        var lettersOrDigits = 0;
        foreach (var c in value)
        {
            if (c == '\uFFFD' || (char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            {
                return false;
            }

            if (char.IsLetterOrDigit(c))
            {
                lettersOrDigits++;
                continue;
            }

            if (char.IsWhiteSpace(c) || c is '\'' or '"' or '.' or ',' or ':' or ';' or '!' or '?' or '(' or ')'
                    or '[' or ']' or '-' or '_' or '/' or '\\' or '+' or '#' or '&' or '%' or '$' or '*')
            {
                continue;
            }

            suspicious++;
        }

        if (lettersOrDigits == 0)
        {
            return false;
        }

        return suspicious <= Math.Max(2, value.Length / 3);
    }
}
