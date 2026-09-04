// Token grammar ported from daggerfall-unity's DaggerfallConnect API (MIT License),
//   https://github.com/Interkarma/daggerfall-unity — Assets/Scripts/API/TextFile.cs
//   (TextFile.Formatting + ReadTokens). License texts are collected centrally in
//   THIRD_PARTY_LICENSES.

using System.Text;

namespace BethesdaMultitool.Core.Formats.Daggerfall;

/// <summary>
///     The byte grammar shared by <c>TEXT.RSC</c> records and <c>BOOKS/*.TXT</c> pages: printable
///     bytes 0x20-0x7F are text, everything else is a control. Two controls carry a one-byte
///     argument (font and position prefixes), so a splitter or renderer must step over the
///     argument or it will mistake an argument byte for a separator.
///     <para>
///         Retail usage (measured 2026-09-03): TEXT.RSC uses only justify-left/-center as line
///         terminators (never 0x00), subrecord separators and a handful of position/cursor
///         prefixes; books use 0x00 newlines, font and position prefixes and justify-center as a
///         line PREFIX. The plain rendering here treats every line control as "end the current
///         line" so both conventions read naturally.
///     </para>
/// </summary>
internal static class DaggerfallTextTokens
{
    public const byte NewLine = 0x00;
    public const byte SameLineOffset = 0x01;
    public const byte PullPreceding = 0x02;
    public const byte FirstCharacter = 0x20;
    public const byte LastCharacter = 0x7F;
    public const byte EndOfPage = 0xF6;
    public const byte InputCursor = 0xF8;
    public const byte FontPrefix = 0xF9;
    public const byte PositionPrefix = 0xFB;
    public const byte JustifyLeft = 0xFC;
    public const byte JustifyCenter = 0xFD;
    public const byte EndOfRecord = 0xFE;
    public const byte SubrecordSeparator = 0xFF;

    /// <summary>
    ///     Code page 437's upper half (0x80-0xFF). Bytes above the printable range that are not
    ///     controls are DOS-era glyph codes; mapping them through the code page the game ran under
    ///     is the honest reading (the reference drops them as unknown formatting).
    /// </summary>
    internal const string CodePage437High =
        "ÇüéâäàåçêëèïîìÄÅ" +
        "ÉæÆôöòûùÿÖÜ¢£¥₧ƒ" +
        "áíóúñÑªº¿⌐¬½¼¡«»" +
        "░▒▓│┤╡╢╖╕╣║╗╝╜╛┐" +
        "└┴┬├─┼╞╟╚╔╩╦╠═╬╧" +
        "╨╤╥╙╘╒╓╫╪┘┌█▄▌▐▀" +
        "αßΓπΣσµτΦΘΩδ∞φε∩" +
        "≡±≥≤⌠⌡÷≈°∙·√ⁿ²■ ";

    /// <summary>True for a printable text byte.</summary>
    public static bool IsText(byte value)
    {
        return value is >= FirstCharacter and <= LastCharacter;
    }

    /// <summary>True for the two controls followed by a one-byte argument.</summary>
    public static bool TakesArgument(byte value)
    {
        return value is FontPrefix or PositionPrefix;
    }

    /// <summary>
    ///     Splits a record at subrecord separators, stopping at an end-of-record byte. A record
    ///     without separators is one subrecord; an empty record is one empty subrecord.
    /// </summary>
    public static IReadOnlyList<ReadOnlyMemory<byte>> SplitSubrecords(ReadOnlyMemory<byte> record)
    {
        var result = new List<ReadOnlyMemory<byte>>();
        var span = record.Span;
        var start = 0;
        var i = 0;
        while (i < span.Length)
        {
            var value = span[i];
            if (value == EndOfRecord)
            {
                break;
            }

            if (value == SubrecordSeparator)
            {
                result.Add(record[start..i]);
                start = i + 1;
                i++;
                continue;
            }

            i += TakesArgument(value) ? 2 : 1;
        }

        result.Add(record[start..Math.Min(i, span.Length)]);
        return result;
    }

    /// <summary>
    ///     Renders a byte run as plain text: lines end at newline, justify, end-of-page and
    ///     subrecord bytes; prefix arguments are skipped and a position prefix becomes a space;
    ///     each line is trimmed; runs of blank lines collapse to one and none survive at either end.
    /// </summary>
    public static string RenderPlain(ReadOnlySpan<byte> bytes)
    {
        var lines = new List<string>();
        var line = new StringBuilder();

        // A while loop, not a for: two controls consume an argument byte and the end-of-record byte
        // stops the walk, so the cursor advances by more than one in places.
        var i = 0;
        while (i < bytes.Length)
        {
            var value = bytes[i];
            i++;
            if (IsText(value))
            {
                line.Append((char)value);
                continue;
            }

            switch (value)
            {
                case NewLine:
                    EndLine(lines, line, force: true);
                    break;
                case JustifyLeft:
                case JustifyCenter:
                case EndOfPage:
                case SubrecordSeparator:
                    EndLine(lines, line, force: false);
                    break;
                case EndOfRecord:
                    i = bytes.Length;
                    break;
                case FontPrefix:
                    i++;
                    break;
                case PositionPrefix:
                    i++;
                    if (line.Length > 0 && line[^1] != ' ')
                    {
                        line.Append(' ');
                    }

                    break;
                case SameLineOffset:
                case PullPreceding:
                case InputCursor:
                    // Layout-only controls: no text.
                    break;
                case >= 0x80:
                    line.Append(CodePage437High[value - 0x80]);
                    break;
                default:
                    // Any stray low byte carries no text.
                    break;
            }
        }

        EndLine(lines, line, force: false);
        return Collapse(lines);
    }

    private static void EndLine(List<string> lines, StringBuilder line, bool force)
    {
        if (!force && line.Length == 0)
        {
            return;
        }

        lines.Add(line.ToString().Trim());
        line.Clear();
    }

    private static string Collapse(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        foreach (var candidate in lines)
        {
            var blank = candidate.Length == 0;
            if (blank && (result.Count == 0 || result[^1].Length == 0))
            {
                continue;
            }

            result.Add(candidate);
        }

        while (result.Count > 0 && result[^1].Length == 0)
        {
            result.RemoveAt(result.Count - 1);
        }

        return string.Join('\n', result);
    }
}
