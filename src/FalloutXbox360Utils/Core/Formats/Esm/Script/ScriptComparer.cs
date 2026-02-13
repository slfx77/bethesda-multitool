using System.Text.RegularExpressions;

namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Semantic comparison utilities for script decompilation validation.
///     Compares SCTX (original source) against decompiled SCDA (bytecode) output,
///     categorizing differences by type: function names, parenthesization, number formatting, etc.
/// </summary>
public static class ScriptComparer
{
    /// <summary>
    ///     Build a bidirectional case-insensitive map that normalizes all function names to a canonical form.
    ///     Maps both ShortName → canonical and LongName → canonical (using ShortName as canonical when available).
    ///     This handles GECK source using either form interchangeably.
    /// </summary>
    public static Dictionary<string, string> BuildFunctionNameNormalizationMap()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (ushort opcode = ScriptOpcodes.MinFunctionOpcode; opcode < 0x2000; opcode++)
        {
            var def = ScriptFunctionTable.Get(opcode);
            if (def == null)
            {
                continue;
            }

            // Use short name as canonical when available, otherwise long name
            var canonical = !string.IsNullOrEmpty(def.ShortName) ? def.ShortName : def.Name;
            map.TryAdd(def.Name, canonical);
            if (!string.IsNullOrEmpty(def.ShortName))
            {
                map.TryAdd(def.ShortName, canonical);
            }
        }

        return map;
    }

    /// <summary>
    ///     Normalize a script line for comparison: trim, strip comments, collapse whitespace.
    /// </summary>
    public static string NormalizeScriptLine(string line)
    {
        var trimmed = line.Trim().TrimEnd('\r');

        // Strip trailing inline comments ("; ...")
        var commentIdx = trimmed.IndexOf(';');
        if (commentIdx >= 0)
        {
            trimmed = trimmed[..commentIdx].TrimEnd();
        }

        // Collapse multiple spaces to single
        while (trimmed.Contains("  "))
        {
            trimmed = trimmed.Replace("  ", " ");
        }

        // Normalize tabs to spaces
        trimmed = trimmed.Replace('\t', ' ');

        return trimmed;
    }

    /// <summary>
    ///     Extract meaningful (non-empty, non-comment) lines from script text.
    ///     Skips variable declarations (short/int/long/float/ref) which are present in SCTX source
    ///     but omitted by the decompiler (handled implicitly by variable definitions).
    ///     Normalizes "scn" to "ScriptName" to match the decompiler output.
    /// </summary>
    public static List<string> ExtractMeaningfulLines(string scriptText)
    {
        var lines = new List<string>();
        foreach (var rawLine in scriptText.Split('\n'))
        {
            var normalized = NormalizeScriptLine(rawLine);
            if (string.IsNullOrEmpty(normalized))
            {
                continue;
            }

            // Skip decorative separator lines (e.g., "====...") — present in some SCTX source
            if (normalized.Length >= 4 && normalized.All(c => c == '='))
            {
                continue;
            }

            // Skip backtick-only lines (formatting artifacts in some SCTX source)
            if (normalized.All(c => c == '`'))
            {
                continue;
            }

            // Skip variable declarations — present in source but not in decompiled output
            var firstWord = GetFirstWord(normalized);
            if (firstWord.Equals("short", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("int", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("long", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("float", StringComparison.OrdinalIgnoreCase) ||
                firstWord.Equals("ref", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Normalize "scn" to "ScriptName"
            if (firstWord.Equals("scn", StringComparison.OrdinalIgnoreCase))
            {
                normalized = "ScriptName" + normalized[3..];
            }

            lines.Add(normalized);
        }

        return lines;
    }

    /// <summary>
    ///     Categorize the difference between a source line and a decompiled line.
    /// </summary>
    public static string CategorizeLineDifference(
        string sourceLine,
        string decompiledLine,
        Dictionary<string, string> nameMap)
    {
        // Normalize both lines' function names to canonical form
        var sourceCanonical = NormalizeFunctionNames(sourceLine, nameMap);
        var decompiledCanonical = NormalizeFunctionNames(decompiledLine, nameMap);

        // After normalizing function names, exact match means only names differed
        if (string.Equals(sourceCanonical, decompiledCanonical, StringComparison.OrdinalIgnoreCase))
        {
            return "FunctionName";
        }

        // Normalize: strip parens/quotes/commas, collapse whitespace, then re-normalize function names
        // (function names may have been adjacent to parens on first pass, e.g., "(IsDLCInstalled")
        var sourceNorm = NormalizeWords(NormalizeFunctionNames(NormalizeParensAndSpaces(sourceCanonical), nameMap));
        var decompiledNorm = NormalizeWords(NormalizeFunctionNames(NormalizeParensAndSpaces(decompiledCanonical), nameMap));
        if (string.Equals(sourceNorm, decompiledNorm, StringComparison.OrdinalIgnoreCase))
        {
            return "Parenthesization";
        }

        // Check number formatting with all normalizations applied
        if (IsNumberFormatDifference(sourceNorm, decompiledNorm))
        {
            return "NumberFormat";
        }

        // Check if decompiled is a prefix of source — GECK source has extra trailing
        // parameters/decorators that the compiler strips (e.g., StopCombat Player → StopCombat,
        // RemoveScriptPackage PkgName → RemoveScriptPackage, End OnAdd → End)
        if (sourceNorm.StartsWith(decompiledNorm + " ", StringComparison.OrdinalIgnoreCase) ||
            decompiledNorm.StartsWith(sourceNorm + " ", StringComparison.OrdinalIgnoreCase))
        {
            return "DroppedParameter";
        }

        // Check for unresolved FormIDs in decompiled output (0x00XXXXXX hex where source has EditorIDs)
        if (Regex.IsMatch(decompiledNorm, @"0x[0-9A-Fa-f]{4,}") &&
            !Regex.IsMatch(sourceNorm, @"0x[0-9A-Fa-f]{4,}"))
        {
            return "UnresolvedFormId";
        }

        // Check for unresolved variable references (var0, var1, etc.) or SCRO[N]
        if (Regex.IsMatch(decompiledLine, @"\bvar\d+\b") ||
            Regex.IsMatch(decompiledLine, @"SCRO\[\d+\]"))
        {
            return "UnresolvedVariable";
        }

        return "Other";
    }

    /// <summary>
    ///     Compare two scripts line-by-line and return match statistics.
    ///     Function name variants (short/long) are normalized before comparison,
    ///     so GetAV vs GetActorValue is treated as a match.
    /// </summary>
    public static ScriptComparisonResult CompareScripts(
        string sourceText,
        string decompiledText,
        Dictionary<string, string> nameMap)
    {
        var sourceLines = ExtractMeaningfulLines(sourceText);
        var decompiledLines = ExtractMeaningfulLines(decompiledText);

        var result = new ScriptComparisonResult();

        var si = 0;
        var di = 0;
        while (si < sourceLines.Count && di < decompiledLines.Count)
        {
            var sLine = sourceLines[si];
            var dLine = decompiledLines[di];

            // Normalize function names to canonical form before comparing
            var sNorm = NormalizeFunctionNames(sLine, nameMap);
            var dNorm = NormalizeFunctionNames(dLine, nameMap);

            if (string.Equals(sNorm, dNorm, StringComparison.OrdinalIgnoreCase))
            {
                result.MatchCount++;
                si++;
                di++;
                continue;
            }

            var category = CategorizeLineDifference(sLine, dLine, nameMap);

            // DroppedParameter and NumberFormat are semantically correct decompilations:
            // - DroppedParameter: compiler strips trailing params the decompiler correctly omits
            // - NumberFormat: IEEE 754 can't preserve original float formatting (1 vs 1.0)
            if (category is "DroppedParameter" or "NumberFormat")
            {
                result.MatchCount++;
                result.ToleratedDifferences.TryGetValue(category, out var toleratedCount);
                result.ToleratedDifferences[category] = toleratedCount + 1;
            }
            else
            {
                result.MismatchesByCategory.TryGetValue(category, out var count);
                result.MismatchesByCategory[category] = count + 1;
            }

            if (result.Examples.Count < 10)
            {
                result.Examples.Add((sLine, dLine, category));
            }

            si++;
            di++;
        }

        // Count remaining lines as missing/extra
        result.MismatchesByCategory.TryGetValue("MissingLine", out var missing);
        result.MismatchesByCategory["MissingLine"] = missing + Math.Abs(
            (sourceLines.Count - si) - (decompiledLines.Count - di));

        return result;
    }

    /// <summary>
    ///     Normalize function names in a line to their canonical form using the normalization map.
    /// </summary>
    public static string NormalizeFunctionNames(string line, Dictionary<string, string> nameMap)
    {
        var words = line.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            // Handle ref.Function patterns
            var dotIdx = words[i].IndexOf('.');
            if (dotIdx >= 0 && dotIdx < words[i].Length - 1)
            {
                var afterDot = words[i][(dotIdx + 1)..];
                if (nameMap.TryGetValue(afterDot, out var canonical))
                {
                    words[i] = words[i][..(dotIdx + 1)] + canonical;
                }
            }
            else if (nameMap.TryGetValue(words[i], out var canonical))
            {
                words[i] = canonical;
            }
        }

        return string.Join(" ", words);
    }

    private static string GetFirstWord(string line)
    {
        var spaceIdx = line.IndexOf(' ');
        return spaceIdx >= 0 ? line[..spaceIdx] : line;
    }

    /// <summary>
    ///     Strip all parentheses, commas, quotes, normalize operator spacing, and normalize whitespace.
    ///     Commas are optional in GECK script syntax (parameter separators).
    ///     Operator spacing and string quoting vary between source and decompiled output.
    /// </summary>
    private static string NormalizeParensAndSpaces(string line)
    {
        var result = line.Replace("(", " ").Replace(")", " ").Replace(",", " ");

        // Strip string quotes — decompiler quotes string params, GECK source often doesn't
        result = result.Replace("\"", " ");

        // Normalize operator spacing using regex to handle all operators cleanly.
        // Process two-char operators first (replace with placeholders), then single-char.
        result = result.Replace("==", " \x01EQ\x01 ").Replace("!=", " \x01NE\x01 ")
            .Replace(">=", " \x01GE\x01 ").Replace("<=", " \x01LE\x01 ")
            .Replace("&&", " \x01AND\x01 ").Replace("||", " \x01OR\x01 ");

        // Single-char operators: +, -, *, /, <, >
        result = result.Replace("+", " + ").Replace("-", " - ")
            .Replace("*", " * ").Replace("/", " / ")
            .Replace("<", " < ").Replace(">", " > ");

        // Restore two-char operator placeholders
        result = result.Replace("\x01EQ\x01", "==").Replace("\x01NE\x01", "!=")
            .Replace("\x01GE\x01", ">=").Replace("\x01LE\x01", "<=")
            .Replace("\x01AND\x01", "&&").Replace("\x01OR\x01", "||");

        while (result.Contains("  "))
        {
            result = result.Replace("  ", " ");
        }

        return result.Trim();
    }

    /// <summary>
    ///     Normalize well-known word aliases to canonical form.
    ///     Handles PlayerRef → player, block type aliases, etc.
    /// </summary>
    private static string NormalizeWords(string line)
    {
        var words = line.Split(' ');
        for (var i = 0; i < words.Length; i++)
        {
            // PlayerRef / playerREF → player (FormID 0x14 is always "player" in decompiled output)
            if (words[i].Equals("PlayerRef", StringComparison.OrdinalIgnoreCase))
            {
                words[i] = "player";
            }
            // Block type aliases
            else if (words[i].Equals("OnPackageEND", StringComparison.OrdinalIgnoreCase))
            {
                words[i] = "OnPackageDone";
            }
            // Actor value aliases — GECK source uses abbreviated forms
            else if (words[i].Equals("DamageResist", StringComparison.OrdinalIgnoreCase))
            {
                words[i] = "DamageResistance";
            }
            // Skill renames between Fallout 3 and NV (SCTX source may use FO3 names)
            else if (words[i].Equals("SmallGuns", StringComparison.OrdinalIgnoreCase))
            {
                words[i] = "Guns";
            }
        }

        return string.Join(" ", words);
    }

    /// <summary>
    ///     Check if two lines differ only in number formatting (e.g., "1" vs "1.0").
    /// </summary>
    private static bool IsNumberFormatDifference(string source, string decompiled)
    {
        var sourceTokens = source.Split(' ');
        var decompiledTokens = decompiled.Split(' ');
        if (sourceTokens.Length != decompiledTokens.Length)
        {
            return false;
        }

        var hasDiff = false;
        for (var i = 0; i < sourceTokens.Length; i++)
        {
            if (string.Equals(sourceTokens[i], decompiledTokens[i], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Check if both parse to the same numeric value
            if (double.TryParse(sourceTokens[i], out var sv) &&
                double.TryParse(decompiledTokens[i], out var dv) &&
                Math.Abs(sv - dv) < 0.001)
            {
                hasDiff = true;
                continue;
            }

            return false;
        }

        return hasDiff;
    }
}

/// <summary>
///     Result of comparing SCTX source vs decompiled script text.
/// </summary>
public sealed class ScriptComparisonResult
{
    public int MatchCount { get; set; }
    public Dictionary<string, int> MismatchesByCategory { get; } = new();

    /// <summary>
    ///     Differences that are semantically correct but worth tracking for diagnostics.
    ///     DroppedParameter: compiler strips trailing params the decompiler correctly omits.
    ///     NumberFormat: IEEE 754 representation prevents exact float formatting recovery.
    ///     These count toward MatchCount, not TotalMismatches.
    /// </summary>
    public Dictionary<string, int> ToleratedDifferences { get; } = new();

    public List<(string Source, string Decompiled, string Category)> Examples { get; } = [];

    public int TotalMismatches => MismatchesByCategory.Values.Sum();
    public int TotalTolerated => ToleratedDifferences.Values.Sum();
    public int TotalLines => MatchCount + TotalMismatches;

    public double MatchRate => TotalLines > 0 ? 100.0 * MatchCount / TotalLines : 0;
}
