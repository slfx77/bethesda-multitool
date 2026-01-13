using System.Text;

namespace Xbox360MemoryCarver.Core.Formats.Script;

/// <summary>
///     Bethesda ObScript (source text) format module.
/// </summary>
public sealed class ScriptFormat : FileFormatBase
{
    public override string FormatId => "script";
    public override string DisplayName => "ObScript";
    public override string Extension => ".txt";
    public override FileCategory Category => FileCategory.Script;
    public override string OutputFolder => "scripts";
    public override int MinSize => 20;
    public override int MaxSize => 100 * 1024;

    public override IReadOnlyList<FormatSignature> Signatures { get; } =
    [
        new() { Id = "script_scn", MagicBytes = "scn "u8.ToArray(), Description = "Bethesda ObScript (scn format)" },
        new()
        {
            Id = "script_scn_tab", MagicBytes = "scn\t"u8.ToArray(), Description = "Bethesda ObScript (scn format)"
        },
        new() { Id = "script_Scn", MagicBytes = "Scn "u8.ToArray(), Description = "Bethesda ObScript (Scn format)" },
        new() { Id = "script_SCN", MagicBytes = "SCN "u8.ToArray(), Description = "Bethesda ObScript (SCN format)" },
        new()
        {
            Id = "script_scriptname", MagicBytes = "ScriptName "u8.ToArray(),
            Description = "Bethesda ObScript (ScriptName format)"
        },
        new()
        {
            Id = "script_scriptname_lower", MagicBytes = "scriptname "u8.ToArray(),
            Description = "Bethesda ObScript (scriptname format)"
        }
    ];

    public override ParseResult? Parse(ReadOnlySpan<byte> data, int offset = 0)
    {
        if (data.Length < offset + 10) return null;

        try
        {
            var maxEnd = Math.Min(offset + 100000, data.Length);
            var scriptData = data[offset..maxEnd];

            var firstLineEnd = FindLineEnd(scriptData);
            if (firstLineEnd == -1) return null;

            var firstLine = Encoding.ASCII.GetString(scriptData[..firstLineEnd]).Trim();
            var scriptName = ExtractScriptName(firstLine);
            if (string.IsNullOrEmpty(scriptName)) return null;

            var invalidChar = scriptName.IndexOfAny([';', '\r', '\t', ' ']);
            if (invalidChar >= 0) scriptName = scriptName[..invalidChar];

            if (string.IsNullOrEmpty(scriptName) || !scriptName.All(c => char.IsLetterOrDigit(c) || c == '_'))
                return null;

            var endPos = FindScriptEnd(scriptData, firstLineEnd);
            var safeName = new string([.. scriptName.Where(c => char.IsLetterOrDigit(c) || c == '_' || c == '-')]);

            return new ParseResult
            {
                Format = "Script",
                EstimatedSize = endPos,
                FileName = scriptName + ".txt",
                Metadata = new Dictionary<string, object>
                {
                    ["scriptName"] = scriptName,
                    ["safeName"] = safeName
                }
            };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ScriptFormat] Exception at offset {offset}: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }

    private static int FindLineEnd(ReadOnlySpan<byte> data)
    {
        for (var i = 0; i < data.Length; i++)
        {
            if (data[i] == '\r' || data[i] == '\n')
            {
                return i;
            }
        }

        return -1;
    }

    private static string? ExtractScriptName(string firstLine)
    {
        string? scriptName = null;
        var lower = firstLine.ToLowerInvariant();

        if (lower.StartsWith("scn ", StringComparison.Ordinal) || lower.StartsWith("scn\t", StringComparison.Ordinal))
            scriptName = firstLine[4..].Trim();
        else if (lower.StartsWith("scriptname ", StringComparison.Ordinal) ||
                 lower.StartsWith("scriptname\t", StringComparison.Ordinal))
        {
            scriptName = firstLine[11..].Trim();
        }

        return scriptName;
    }

    private static int FindScriptEnd(ReadOnlySpan<byte> data, int startPos)
    {
        var maxLen = Math.Min(data.Length, 100000);
        var lastValidPos = startPos;
        var consecutiveNonPrintable = 0;

        for (var i = startPos; i < maxLen; i++)
        {
            var b = data[i];

            if (b < 0x20 || b > 0x7E)
            {
                if (b == '\r' || b == '\n' || b == '\t')
                {
                    consecutiveNonPrintable = 0;
                    lastValidPos = i + 1;
                }
                else
                {
                    consecutiveNonPrintable++;
                    if (consecutiveNonPrintable > 3) return lastValidPos;
                }
            }
            else
            {
                consecutiveNonPrintable = 0;
                lastValidPos = i + 1;
            }
        }

        return lastValidPos;
    }
}
