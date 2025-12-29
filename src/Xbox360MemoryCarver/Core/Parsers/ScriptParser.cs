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
        if (data.Length < offset + 10) return null;

        try
        {
            var maxEnd = Math.Min(offset + 100000, data.Length);
            var scriptData = data[offset..maxEnd];

            // Find first line end
            var firstLineEnd = FindLineEnd(scriptData);
            if (firstLineEnd == -1) return null;

            var firstLine = Encoding.ASCII.GetString(scriptData[..firstLineEnd]).Trim();

            // Extract script name
            var scriptName = ExtractScriptName(firstLine);
            if (string.IsNullOrEmpty(scriptName)) return null;

            // Clean script name - remove comments, whitespace
            var invalidChar = scriptName.IndexOfAny([';', '\r', '\t', ' ']);
            if (invalidChar >= 0) scriptName = scriptName[..invalidChar];

            // Validate script name contains only valid characters
            if (string.IsNullOrEmpty(scriptName) || !scriptName.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return null;

            // Find script end
            var endPos = FindScriptEnd(scriptData, firstLineEnd);

            // Look for leading comment text before the signature
            var leadingCommentSize = FindLeadingCommentText(data, offset);

            // Create safe filename
            var safeName = new string([.. scriptName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')]);

            var result = new ParseResult
            {
                Format = "Script",
                EstimatedSize = endPos,
                Metadata = new Dictionary<string, object>
                {
                    ["scriptName"] = scriptName,
                    ["fileName"] = scriptName,
                    ["safeName"] = safeName
                }
            };

            if (leadingCommentSize > 0) result.Metadata["leadingCommentSize"] = leadingCommentSize;

            return result;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScriptParser] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    ///     Look backwards from the signature for comment text (starting with ';').
    ///     This finds comments even if they're on the same line as garbage data.
    /// </summary>
    private static int FindLeadingCommentText(ReadOnlySpan<byte> data, int signatureOffset)
    {
        if (signatureOffset <= 0) return 0;

        // Skip whitespace before signature (CR, LF, space, tab)
        var pos = signatureOffset - 1;
        while (pos >= 0 && (data[pos] == ' ' || data[pos] == '\t' || data[pos] == '\r' || data[pos] == '\n')) pos--;

        if (pos < 0) return 0;

        // Now we're at the last non-whitespace character before the signature
        // Look backwards for a semicolon that starts a comment
        // The comment should end here (at pos) and we need to find where it starts

        // First, find the start of the current "text run" by going back to find ';'
        // We limit the search to avoid going too far back into binary data
        var searchLimit = Math.Max(0, pos - 200); // Max 200 chars of comment
        var semicolonPos = -1;

        for (var i = pos; i >= searchLimit; i--)
        {
            var b = data[i];

            // Stop if we hit binary/control characters (except common ones in comments)
            if (b == 0 || (b < 32 && b != '\t') || b > 126) break;

            if (b == ';') semicolonPos = i;
            // Don't break - keep looking for an earlier semicolon on this text run
        }

        if (semicolonPos < 0) return 0;

        // Verify the text from semicolon to pos is mostly readable
        var commentLength = signatureOffset - semicolonPos;
        if (commentLength < 3) return 0; // Too short to be meaningful

        // Include any whitespace after the semicolon that we skipped
        return commentLength;
    }

    private static int FindLineEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
            if (data[i] == '\n')
                return i;

        return -1;
    }

    private static string? ExtractScriptName(string firstLine)
    {
        var lowerLine = firstLine.ToLowerInvariant();

        if (lowerLine.StartsWith("scn ", StringComparison.Ordinal) ||
            lowerLine.StartsWith("scn\t", StringComparison.Ordinal))
            return firstLine[4..].Trim();

        if (lowerLine.StartsWith("scriptname ", StringComparison.Ordinal) ||
            lowerLine.StartsWith("scriptname\t", StringComparison.Ordinal))
            return firstLine[11..].Trim();

        return null;
    }

    private static int FindScriptEnd(ReadOnlySpan<byte> scriptData, int firstLineEnd)
    {
        var endPos = scriptData.Length;
        var searchStart = firstLineEnd + 1;

        // Find next script header (indicates end of current script)
        foreach (var header in ScriptHeaders)
        {
            if (searchStart >= scriptData.Length) continue;

            var searchSlice = scriptData[searchStart..];
            var nextScript = BinaryUtils.FindPattern(searchSlice, header);
            if (nextScript >= 0)
            {
                var absolutePos = searchStart + nextScript;

                // Find previous newline to get clean boundary
                var boundary = absolutePos;
                for (var i = absolutePos - 1; i >= 0; i--)
                    if (scriptData[i] == '\n')
                    {
                        boundary = i;
                        break;
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
            if (b != 9 && b != 10 && b != 13 && b != 32) break;
            endPos--;
        }

        return Math.Max(endPos, 1);
    }
}
