namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Shared helpers for script decompiler integration tests.
/// </summary>
internal static class ScriptTestHelpers
{
    /// <summary>
    ///     Walk up from test bin directory to find a sample file relative to repo root.
    /// </summary>
    public static string? FindSamplePath(string relativePath)
    {
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = Path.GetDirectoryName(dir)!;
        }

        return null;
    }

    /// <summary>
    ///     Extract flow-control keywords from script text for structural comparison.
    /// </summary>
    public static List<string> ExtractStructuralKeywords(string scriptText)
    {
        var keywords = new List<string>();
        string[] structuralKeywords =
            ["ScriptName", "Begin", "End", "If", "ElseIf", "Else", "EndIf", "While", "EndWhile", "Return"];

        foreach (var rawLine in scriptText.Split('\n'))
        {
            var line = rawLine.Trim().TrimEnd('\r');

            // Skip comments and empty lines
            if (string.IsNullOrEmpty(line) || line.StartsWith(';'))
            {
                continue;
            }

            // Extract the first word
            var firstSpace = line.IndexOf(' ');
            var firstParen = line.IndexOf('(');
            var endIdx = firstSpace >= 0 ? firstSpace : line.Length;
            if (firstParen >= 0 && firstParen < endIdx)
            {
                endIdx = firstParen;
            }

            var keyword = line[..endIdx];

            if (structuralKeywords.Contains(keyword, StringComparer.OrdinalIgnoreCase))
            {
                keywords.Add(keyword.ToLowerInvariant() switch
                {
                    "scriptname" => "ScriptName",
                    "begin" => "Begin",
                    "end" => "End",
                    "if" => "If",
                    "elseif" => "ElseIf",
                    "else" => "Else",
                    "endif" => "EndIf",
                    "while" => "While",
                    "endwhile" => "EndWhile",
                    "return" => "Return",
                    _ => keyword
                });
            }
        }

        return keywords;
    }

    /// <summary>
    ///     Check if two keyword lists represent equivalent block structure.
    ///     Compares Begin/End, If/ElseIf/Else/EndIf, While/EndWhile only.
    /// </summary>
    public static bool StructurallyEquivalent(List<string> source, List<string> decompiled)
    {
        string[] blockKeywords = ["Begin", "End", "If", "ElseIf", "Else", "EndIf", "While", "EndWhile"];

        var sourceBlocks = source.Where(k => blockKeywords.Contains(k)).ToList();
        var decompiledBlocks = decompiled.Where(k => blockKeywords.Contains(k)).ToList();

        return sourceBlocks.SequenceEqual(decompiledBlocks);
    }

    /// <summary>
    ///     Find the first error/decompilation-error line in decompiled output.
    /// </summary>
    public static string GetFirstErrorLine(string result)
    {
        foreach (var line in result.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("; Decompilation error", StringComparison.Ordinal) ||
                trimmed.StartsWith("; Error decoding", StringComparison.Ordinal))
            {
                return trimmed.Length > 120 ? trimmed[..120] + "..." : trimmed;
            }
        }

        return "(no error line found)";
    }
}
