using System.Text;
using Xbox360MemoryCarver.Core.Utils;

namespace Xbox360MemoryCarver.Core.Parsers;

/// <summary>
///     Parser for Bethesda ObScript files (scn/ScriptName format).
/// </summary>
public class ScriptParser : IFileParser
{
    private static readonly byte[][] ScriptHeaders =
    [
        "scn "u8.ToArray(),
        "scn\t"u8.ToArray(),
        "Scn "u8.ToArray(),
        "Scn\t"u8.ToArray(),
        "SCN "u8.ToArray(),
        "SCN\t"u8.ToArray(),
        "ScriptName "u8.ToArray(),
        "ScriptName\t"u8.ToArray(),
        "scriptname "u8.ToArray(),
        "scriptname\t"u8.ToArray(),
        "SCRIPTNAME "u8.ToArray(),
        "SCRIPTNAME\t"u8.ToArray()
    ];

    public ParseResult? ParseHeader(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 10)
        {
            return null;
        }

        try
        {
            var maxEnd = Math.Min(offset + 100000, data.Length);
            var scriptData = data[offset..maxEnd];

            // Find first line end
            var firstLineEnd = FindLineEnd(scriptData);
            if (firstLineEnd == -1)
            {
                return null;
            }

            var firstLine = Encoding.ASCII.GetString(scriptData[..firstLineEnd]).Trim();

            // Extract script name
            var scriptName = ExtractScriptName(firstLine);
            if (string.IsNullOrEmpty(scriptName))
            {
                return null;
            }

            // Clean script name - remove comments, whitespace
            var invalidChar = scriptName.IndexOfAny([';', '\r', '\t', ' ']);
            if (invalidChar >= 0)
            {
                scriptName = scriptName[..invalidChar];
            }

            // Validate script name contains only valid characters
            if (string.IsNullOrEmpty(scriptName) || !scriptName.All(c => char.IsLetterOrDigit(c) || c == '_'))
            {
                return null;
            }

            // Find script end
            var endPos = FindScriptEnd(scriptData, firstLineEnd);

            // Create safe filename
            var safeName = new string([.. scriptName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')]);

            return new ParseResult
            {
                Format = "Script",
                EstimatedSize = endPos,
                Metadata = new Dictionary<string, object>
                {
                    ["scriptName"] = scriptName,
                    ["safeName"] = safeName
                }
            };
        }
        catch
        {
            return null;
        }
    }

    private static int FindLineEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static string? ExtractScriptName(string firstLine)
    {
        var lowerLine = firstLine.ToLowerInvariant();

        if (lowerLine.StartsWith("scn ", StringComparison.Ordinal) ||
            lowerLine.StartsWith("scn\t", StringComparison.Ordinal))
        {
            return firstLine[4..].Trim();
        }

        if (lowerLine.StartsWith("scriptname ", StringComparison.Ordinal) ||
            lowerLine.StartsWith("scriptname\t", StringComparison.Ordinal))
        {
            return firstLine[11..].Trim();
        }

        return null;
    }

    private static int FindScriptEnd(ReadOnlySpan<byte> scriptData, int firstLineEnd)
    {
        var endPos = scriptData.Length;
        var searchStart = firstLineEnd + 1;

        // Find next script header (indicates end of current script)
        foreach (var header in ScriptHeaders)
        {
            var nextScript = BinaryUtils.FindPattern(scriptData[searchStart..], header);
            if (nextScript >= 0)
            {
                var absolutePos = searchStart + nextScript;

                // Find previous newline to get clean boundary
                var boundary = absolutePos;
                for (var i = absolutePos - 1; i >= 0; i--)
                {
                    if (scriptData[i] == '\n')
                    {
                        boundary = i;
                        break;
                    }
                }

                endPos = Math.Min(endPos, boundary);
            }
        }

        // Stop at garbage/binary data
        for (var i = 0; i < endPos; i++)
        {
            var b = scriptData[i];
            // Allow: tab (9), newline (10), carriage return (13), printable ASCII (32-126)
            if (b == 0 || (b < 32 && b != 9 && b != 10 && b != 13) || b > 126)
            {
                endPos = Math.Min(endPos, i);
                break;
            }
        }

        // Trim trailing whitespace
        while (endPos > 0)
        {
            var b = scriptData[endPos - 1];
            if (b != 9 && b != 10 && b != 13 && b != 32)
            {
                break;
            }

            endPos--;
        }

        return Math.Max(endPos, 1);
    }
}
