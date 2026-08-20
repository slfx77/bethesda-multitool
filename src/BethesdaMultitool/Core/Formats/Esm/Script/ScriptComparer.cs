using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

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
        for (var opcode = ScriptOpcodes.MinFunctionOpcode; opcode < 0x2000; opcode++)
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

        // Strip trailing inline comments ("; ..."), but never a semicolon that belongs
        // to a quoted string argument. SCTX is source code, so a blind IndexOf(';') can
        // make two different message/string payloads look identical during the
        // source-to-bytecode correspondence gate.
        var commentIdx = FindCommentStart(trimmed);
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

    private static int FindCommentStart(string line)
    {
        var quoted = false;
        var index = 0;
        while (index < line.Length)
        {
            var current = line[index];
            if (current == '"')
            {
                // GECK source commonly represents a literal quote inside a string as a
                // doubled quote. Keep both bytes inside the quoted region.
                if (quoted && index + 1 < line.Length && line[index + 1] == '"')
                {
                    index += 2;
                    continue;
                }

                quoted = !quoted;
                index++;
                continue;
            }

            if (current == ';' && !quoted)
            {
                return index;
            }

            index++;
        }

        return -1;
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

            // Skip decorative separator lines (e.g., "====..." or "----...") —
            // present in some SCTX source, including VMS12QuestScript. They are comments
            // in practice even when the source omitted a leading semicolon.
            if (normalized.Length >= 4
                && (normalized.All(static c => c == '=')
                    || normalized.All(static c => c == '-')))
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
        var decompiledNorm =
            NormalizeWords(NormalizeFunctionNames(NormalizeParensAndSpaces(decompiledCanonical), nameMap));
        if (string.Equals(sourceNorm, decompiledNorm, StringComparison.OrdinalIgnoreCase))
        {
            return "Parenthesization";
        }

        // Check number formatting with all normalizations applied
        if (IsNumberFormatDifference(
                sourceNorm,
                decompiledNorm,
                sourceCanonical,
                decompiledCanonical))
        {
            return "NumberFormat";
        }

        // GECK discards a small number of known source-only suffixes. This must be
        // directional and shape-whitelisted: a generic prefix comparison made a
        // shorter, stale SCTX look equivalent to SCDA that still had an operand.
        if (IsKnownCompilerDroppedSuffix(sourceNorm, decompiledNorm))
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
        // ScriptName/scn is record identity, not executable script content. Counting a matching
        // header made a source-only stub and a four-byte SCDA ScriptName opcode look like a
        // meaningful 100% (1/1) comparison even though there were no statements to compare.
        var sourceLines = ExtractMeaningfulLines(sourceText)
            .Where(line => !IsScriptHeader(line))
            .ToList();
        var decompiledLines = ExtractMeaningfulLines(decompiledText)
            .Where(line => !IsScriptHeader(line))
            .ToList();

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

        // Preserve which side owns every unpaired statement. The previous absolute-difference
        // bucket could say only that a line was "missing", which is not enough to distinguish
        // incomplete SCDA recovery from decompiler-only output.
        while (si < sourceLines.Count)
        {
            AddUnpairedLine(result, sourceLines[si], string.Empty, "SourceOnly");
            si++;
        }

        while (di < decompiledLines.Count)
        {
            AddUnpairedLine(result, string.Empty, decompiledLines[di], "DecompiledOnly");
            di++;
        }

        return result;
    }

    private static void AddUnpairedLine(
        ScriptComparisonResult result,
        string sourceLine,
        string decompiledLine,
        string category)
    {
        result.MismatchesByCategory.TryGetValue(category, out var count);
        result.MismatchesByCategory[category] = count + 1;

        if (result.Examples.Count < 10)
        {
            result.Examples.Add((sourceLine, decompiledLine, category));
        }
    }

    private static bool IsScriptHeader(string line)
    {
        var firstWord = GetFirstWord(line);
        return firstWord.Equals("ScriptName", StringComparison.OrdinalIgnoreCase) ||
               firstWord.Equals("scn", StringComparison.OrdinalIgnoreCase);
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

        // Preserve signs inside scientific-notation literals before spacing arithmetic
        // operators. Turning 5e-1 into "5e - 1" prevents exact numeric comparison and can
        // make two serially-identical literals appear different.
        result = Regex.Replace(
            result,
            @"(?<=[0-9.])[eE]\+(?=\d)",
            static match => $"{match.Value[0]}\x01EXPPLUS\x01");
        result = Regex.Replace(
            result,
            @"(?<=[0-9.])[eE]-(?=\d)",
            static match => $"{match.Value[0]}\x01EXPMINUS\x01");

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
            .Replace("\x01AND\x01", "&&").Replace("\x01OR\x01", "||")
            .Replace("\x01EXPPLUS\x01", "+").Replace("\x01EXPMINUS\x01", "-");

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
    private static bool IsNumberFormatDifference(
        string source,
        string decompiled,
        string sourceBeforeQuoteRemoval,
        string decompiledBeforeQuoteRemoval)
    {
        var sourceTokens = source.Split(' ');
        var decompiledTokens = decompiled.Split(' ');
        if (sourceTokens.Length != decompiledTokens.Length)
        {
            return false;
        }

        var sourceQuoted = GetQuotedTokenFlags(sourceBeforeQuoteRemoval, sourceTokens.Length);
        var decompiledQuoted = GetQuotedTokenFlags(decompiledBeforeQuoteRemoval, decompiledTokens.Length);

        var hasDiff = false;
        for (var i = 0; i < sourceTokens.Length; i++)
        {
            if (string.Equals(sourceTokens[i], decompiledTokens[i], StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // A textual formatting difference is safe only when both tokens parse to
            // the exact same IEEE-754 value. An epsilon can equate distinct literals
            // (for example 0 and 0.0005) and is therefore unsuitable as evidence that
            // captured source corresponds to compiled bytecode.
            if (double.TryParse(
                    sourceTokens[i],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var sv) &&
                double.TryParse(
                    decompiledTokens[i],
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var dv) &&
                BitConverter.DoubleToInt64Bits(sv) == BitConverter.DoubleToInt64Bits(dv))
            {
                // Quotes are not cosmetic for a numeric-looking token: "1.0" and
                // "1" are distinct string payloads even though the numeric values
                // compare equally. Quote removal remains useful for GECK syntax
                // normalization, but it must never turn string data into numeric
                // equivalence evidence for the SCTX/SCDA correspondence gate.
                if (sourceQuoted[i] || decompiledQuoted[i])
                {
                    return false;
                }

                hasDiff = true;
                continue;
            }

            return false;
        }

        return hasDiff;
    }

    private static bool[] GetQuotedTokenFlags(string line, int expectedTokenCount)
    {
        const char quotedStart = '\u0002';
        const char quotedEnd = '\u0003';

        var marked = new StringBuilder(line.Length + 4);
        var quoted = false;
        var charIndex = 0;
        while (charIndex < line.Length)
        {
            var current = line[charIndex];
            if (current != '"')
            {
                marked.Append(current);
                charIndex++;
                continue;
            }

            // A doubled quote inside a GECK string is literal content, not a
            // quote-state transition. It cannot affect numeric-token identity.
            if (quoted && charIndex + 1 < line.Length && line[charIndex + 1] == '"')
            {
                marked.Append("  ");
                charIndex += 2;
                continue;
            }

            marked.Append(quoted ? quotedEnd : quotedStart);
            quoted = !quoted;
            charIndex++;
        }

        if (quoted)
        {
            marked.Append(quotedEnd);
        }

        var normalized = NormalizeParensAndSpaces(marked.ToString());
        var markedTokens = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (markedTokens.Length != expectedTokenCount)
        {
            // A malformed/unusual quoted line must fail closed. Marking every
            // token as quoted prevents NumberFormat from certifying it while
            // leaving the other comparer diagnostics available.
            return line.Contains('"')
                ? Enumerable.Repeat(true, expectedTokenCount).ToArray()
                : new bool[expectedTokenCount];
        }

        var result = new bool[expectedTokenCount];
        var insideQuotedRegion = false;
        for (var index = 0; index < markedTokens.Length; index++)
        {
            var token = markedTokens[index];
            var startsQuotedRegion = token.Contains(quotedStart);
            result[index] = insideQuotedRegion || startsQuotedRegion;

            if (startsQuotedRegion)
            {
                insideQuotedRegion = true;
            }

            if (token.Contains(quotedEnd))
            {
                insideQuotedRegion = false;
            }
        }

        return result;
    }

    private static bool IsKnownCompilerDroppedSuffix(string source, string decompiled)
    {
        if (!source.StartsWith(decompiled + " ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var sourceTokens = source.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var decompiledTokens = decompiled.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // GECK accepts a block annotation after End but emits only the End opcode.
        if (decompiledTokens.Length == 1
            && decompiledTokens[0].Equals("End", StringComparison.OrdinalIgnoreCase)
            && sourceTokens.Length == 2
            && IsBlockTypeToken(sourceTokens[1]))
        {
            return true;
        }

        // "Else If <condition>" is represented by an Else opcode followed by the
        // compiler's branch structure; the current line decompiler prints only Else.
        if (decompiledTokens.Length == 1
            && decompiledTokens[0].Equals("Else", StringComparison.OrdinalIgnoreCase)
            && sourceTokens.Length > 2
            && sourceTokens[1].Equals("if", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Script-effect block declarations may carry their owning effect name in
        // source; the block header bytecode stores only the block type.
        if (decompiledTokens.Length == 2
            && sourceTokens.Length == 3
            && decompiledTokens[0].Equals("Begin", StringComparison.OrdinalIgnoreCase)
            && sourceTokens[0].Equals("Begin", StringComparison.OrdinalIgnoreCase)
            && sourceTokens[1].Equals(decompiledTokens[1], StringComparison.OrdinalIgnoreCase)
            && sourceTokens[1] is var blockType
            && (blockType.Equals("ScriptEffectStart", StringComparison.OrdinalIgnoreCase)
                || blockType.Equals("ScriptEffectUpdate", StringComparison.OrdinalIgnoreCase)
                || blockType.Equals("ScriptEffectFinish", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // These two GECK commands are observed in retail source with one ignored
        // trailing operand. Limit tolerance to exactly one suffix token and the
        // exact command, retaining an optional reference receiver.
        if (decompiledTokens.Length == 1 && sourceTokens.Length == 2)
        {
            var commandToken = decompiledTokens[0];
            var dotIndex = commandToken.LastIndexOf('.');
            var command = dotIndex >= 0 ? commandToken[(dotIndex + 1)..] : commandToken;
            return command.Equals("StopCombat", StringComparison.OrdinalIgnoreCase)
                   || command.Equals("RemoveScriptPackage", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsBlockTypeToken(string token)
    {
        return token.StartsWith("On", StringComparison.OrdinalIgnoreCase)
               || token.Equals("GameMode", StringComparison.OrdinalIgnoreCase)
               || token.Equals("MenuMode", StringComparison.OrdinalIgnoreCase)
               || token.Equals("Function", StringComparison.OrdinalIgnoreCase)
               || token.Equals("ScriptEffectStart", StringComparison.OrdinalIgnoreCase)
               || token.Equals("ScriptEffectUpdate", StringComparison.OrdinalIgnoreCase)
               || token.Equals("ScriptEffectFinish", StringComparison.OrdinalIgnoreCase);
    }
}
