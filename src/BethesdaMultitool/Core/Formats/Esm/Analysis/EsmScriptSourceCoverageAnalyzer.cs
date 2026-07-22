using System.Buffers.Binary;
using System.Security.Cryptography;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;
using EsmStringUtils = BethesdaMultitool.Core.Utils.EsmStringUtils;

namespace BethesdaMultitool.Core.Formats.Esm.Analysis;

/// <summary>
///     Audits source text exactly as serialized in emitted script blocks. It does not
///     infer source provenance or borrow source text from another record/dump.
/// </summary>
internal static class EsmScriptSourceCoverageAnalyzer
{
    private static readonly Dictionary<string, string> FunctionNameMap =
        ScriptComparer.BuildFunctionNameNormalizationMap();

    internal static List<EsmScriptSourceCoverageRow> Analyze(IReadOnlyList<ParsedMainRecord> records)
    {
        var editorIds = BuildEditorIdIndex(records);
        var externalVariables = BuildExternalVariableIndex(records);
        var rows = new List<EsmScriptSourceCoverageRow>();

        foreach (var record in records)
        {
            var subrecords = record.Subrecords;
            var blockIndex = 0;
            var hasSchr = false;
            for (var i = 0; i < subrecords.Count; i++)
            {
                if (subrecords[i].Signature != "SCHR")
                {
                    continue;
                }

                hasSchr = true;
                blockIndex++;
                var end = EsmScriptBlockReader.FindScriptBlockEnd(subrecords, i + 1);
                rows.Add(BuildRow(
                    record,
                    blockIndex,
                    "SCHR",
                    i + 1,
                    end,
                    editorIds,
                    externalVariables));
            }

            // A standalone SCPT can carry recovered source without a serialized header or
            // bytecode. Keep it out of script_bytecode_coverage.csv, but expose it here so
            // executable source-only scripts cannot disappear from coverage.
            if (!hasSchr
                && record.Header.Signature == "SCPT"
                && subrecords.Any(static subrecord => subrecord.Signature == "SCTX"))
            {
                rows.Add(BuildRow(
                    record,
                    1,
                    "SCPT-SCTX-only",
                    0,
                    subrecords.Count,
                    editorIds,
                    externalVariables));
            }
        }

        return rows
            .OrderBy(static row => row.RecordType, StringComparer.Ordinal)
            .ThenBy(static row => row.FormId)
            .ThenBy(static row => row.BlockIndex)
            .ToList();
    }

    private static EsmScriptSourceCoverageRow BuildRow(
        ParsedMainRecord record,
        int blockIndex,
        string block,
        int blockStart,
        int blockEnd,
        IReadOnlyDictionary<uint, string> editorIds,
        IReadOnlyDictionary<(uint FormId, ushort VariableIndex), string> externalVariables)
    {
        var subrecords = record.Subrecords;
        var scda = SubrecordsInRange(subrecords, "SCDA", blockStart, blockEnd);
        var sctx = SubrecordsInRange(subrecords, "SCTX", blockStart, blockEnd);
        var locals = ReadLocalTable(subrecords, blockStart, blockEnd);
        var references = ReadReferences(subrecords, blockStart, blockEnd);

        var decodedSources = sctx.Select(static subrecord => DecodeSource(subrecord.Data)).ToList();
        var sourceText = string.Join('\n', decodedSources);
        var sourceScan = ScanSource(sourceText, sctx.Count > 0);
        var declarationVerdict = sctx.Count == 0
            ? "not-applicable:no-sctx"
            : CompareDeclarationsToLocals(sourceScan.Declarations, locals);

        ScriptComparisonResult? comparison = null;
        if (sctx.Count > 0 && scda.Count > 0)
        {
            var decompiler = new ScriptDecompiler(
                locals.ValidVariables,
                references,
                formId => editorIds.GetValueOrDefault(formId),
                resolveExternalVariable: (formId, variableIndex) =>
                    externalVariables.GetValueOrDefault((formId, variableIndex)));
            var decompiledText = decompiler.Decompile(scda[0].Data);
            comparison = ScriptComparer.CompareScripts(sourceText, decompiledText, FunctionNameMap);
        }

        var contradictionReasons = new List<string>();
        if (scda.Count > 1)
        {
            contradictionReasons.Add($"one SCHR block contains {scda.Count} SCDA payloads");
        }

        if (sctx.Count > 1)
        {
            contradictionReasons.Add($"one SCHR block contains {sctx.Count} SCTX payloads");
        }

        if (sourceScan.Classification == "executable" && scda.Count == 0)
        {
            contradictionReasons.Add("executable SCTX has no SCDA payload");
        }

        if (declarationVerdict.StartsWith("mismatch:", StringComparison.Ordinal))
        {
            contradictionReasons.Add($"declaration/SLSD {declarationVerdict}");
        }

        return new EsmScriptSourceCoverageRow(
            record.Header.Signature,
            record.Header.FormId,
            blockIndex,
            block,
            scda.Count > 0,
            scda.Count,
            scda.Sum(static subrecord => subrecord.Data.Length),
            HashPayloads(scda),
            sctx.Count > 0,
            sctx.Count,
            sctx.Sum(static subrecord => subrecord.Data.Length),
            decodedSources.Sum(static source => source.Length),
            HashPayloads(sctx),
            DescribeNulTermination(sctx),
            sourceScan.Classification,
            comparison != null,
            comparison?.TotalLines ?? 0,
            comparison?.MatchCount ?? 0,
            comparison?.TotalMismatches ?? 0,
            comparison?.TotalTolerated ?? 0,
            comparison == null ? string.Empty : FormatCategories(comparison.MismatchesByCategory),
            comparison == null ? string.Empty : FormatCategories(comparison.ToleratedDifferences),
            sourceScan.Declarations.Count,
            locals.Entries.Count,
            declarationVerdict,
            contradictionReasons.Count > 0,
            string.Join("; ", contradictionReasons));
    }

    private static List<ParsedSubrecord> SubrecordsInRange(
        List<ParsedSubrecord> subrecords,
        string signature,
        int start,
        int end)
    {
        var result = new List<ParsedSubrecord>();
        for (var i = start; i < end; i++)
        {
            if (subrecords[i].Signature == signature)
            {
                result.Add(subrecords[i]);
            }
        }

        return result;
    }

    private static LocalTable ReadLocalTable(
        List<ParsedSubrecord> subrecords,
        int start,
        int end)
    {
        var entries = new List<LocalEntry>();
        var pendingEntry = -1;
        for (var i = start; i < end; i++)
        {
            var subrecord = subrecords[i];
            if (subrecord.Signature == "SLSD")
            {
                uint? index = subrecord.Data.Length >= 4
                    ? BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data)
                    : null;
                var type = subrecord.Data.Length > ScriptLocalVariableLayout.IsIntegerOffset
                    ? ScriptLocalVariableLayout.ReadType(subrecord.Data)
                    : (byte?)null;
                entries.Add(new LocalEntry(index, null, type));
                pendingEntry = entries.Count - 1;
            }
            else if (subrecord.Signature == "SCVR" && pendingEntry >= 0)
            {
                entries[pendingEntry] = entries[pendingEntry] with
                {
                    Name = DecodeSource(subrecord.Data)
                };
                pendingEntry = -1;
            }
        }

        var variables = entries
            .Where(static entry => entry.Index.HasValue && entry.Type.HasValue)
            .Select(static entry => new ScriptVariableInfo(
                entry.Index!.Value,
                string.IsNullOrWhiteSpace(entry.Name) ? null : entry.Name,
                entry.Type!.Value))
            .ToList();
        return new LocalTable(entries, variables);
    }

    private static List<uint> ReadReferences(
        List<ParsedSubrecord> subrecords,
        int start,
        int end)
    {
        var references = new List<uint>();
        for (var i = start; i < end; i++)
        {
            var subrecord = subrecords[i];
            if (subrecord.Data.Length < 4)
            {
                continue;
            }

            if (subrecord.Signature == "SCRO")
            {
                references.Add(BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data));
            }
            else if (subrecord.Signature == "SCRV")
            {
                references.Add(0x80000000u | BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data));
            }
        }

        return references;
    }

    private static Dictionary<uint, string> BuildEditorIdIndex(IReadOnlyList<ParsedMainRecord> records)
    {
        return records
            .Select(record => new
            {
                record.Header.FormId,
                EditorIds = record.Subrecords
                    .Where(static subrecord => subrecord.Signature == "EDID")
                    .Select(static subrecord => DecodeSource(subrecord.Data))
                    .Where(static editorId => !string.IsNullOrWhiteSpace(editorId))
                    .Distinct(StringComparer.Ordinal)
                    .ToList()
            })
            .Where(static item => item.FormId != 0 && item.EditorIds.Count == 1)
            .GroupBy(static item => item.FormId)
            .Where(static group => group.SelectMany(item => item.EditorIds).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(
                static group => group.Key,
                static group => group.SelectMany(item => item.EditorIds).First());
    }

    private static Dictionary<(uint FormId, ushort VariableIndex), string> BuildExternalVariableIndex(
        IReadOnlyList<ParsedMainRecord> records)
    {
        return records
            .Where(static record => record.Header.Signature == "SCPT")
            .SelectMany(record => ReadLocalTable(record.Subrecords, 0, record.Subrecords.Count).Entries
                .Where(static entry => entry.Index is <= ushort.MaxValue && !string.IsNullOrWhiteSpace(entry.Name))
                .Select(entry => new
                {
                    record.Header.FormId,
                    Index = checked((ushort)entry.Index!.Value),
                    entry.Name
                }))
            .GroupBy(static item => (item.FormId, item.Index))
            .Where(static group => group.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() == 1)
            .ToDictionary(
                static group => (group.Key.FormId, group.Key.Index),
                static group => group.First().Name!);
    }

    private static SourceScan ScanSource(string sourceText, bool sourcePresent)
    {
        if (!sourcePresent || string.IsNullOrWhiteSpace(sourceText))
        {
            return new SourceScan(sourcePresent ? "declarations-only" : "none", []);
        }

        var declarations = new List<SourceDeclaration>();
        var hasExecutableText = false;
        foreach (var rawLine in NormalizeNewlines(sourceText).Split('\n'))
        {
            var commentIndex = rawLine.IndexOf(';');
            var line = (commentIndex >= 0 ? rawLine[..commentIndex] : rawLine).Trim();
            if (line.Length == 0
                || (line.Length >= 4
                    && (line.All(static character => character == '=')
                        || line.All(static character => character == '-')))
                || line.All(static character => character == '`'))
            {
                continue;
            }

            var tokens = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length == 0)
            {
                continue;
            }

            if (tokens[0].Equals("scn", StringComparison.OrdinalIgnoreCase)
                || tokens[0].Equals("scriptname", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            // Debug-era source can place exact local declarations inside a Begin/End
            // block. The declaration grammar is unambiguous, and ScriptComparer likewise
            // excludes these lines from executable semantic comparison regardless of
            // position, so preserve their exact identity wherever they occur.
            if (tokens.Length == 2
                && TryParseDeclarationKind(tokens[0], out var kind))
            {
                declarations.Add(new SourceDeclaration(tokens[1], kind));
                continue;
            }

            hasExecutableText = true;
        }

        return new SourceScan(hasExecutableText ? "executable" : "declarations-only", declarations);
    }

    private static bool TryParseDeclarationKind(string keyword, out DeclarationKind kind)
    {
        if (keyword.Equals("short", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("long", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("int", StringComparison.OrdinalIgnoreCase))
        {
            kind = DeclarationKind.Integer;
            return true;
        }

        if (keyword.Equals("float", StringComparison.OrdinalIgnoreCase))
        {
            kind = DeclarationKind.Float;
            return true;
        }

        if (keyword.Equals("ref", StringComparison.OrdinalIgnoreCase))
        {
            kind = DeclarationKind.Reference;
            return true;
        }

        kind = default;
        return false;
    }

    private static string CompareDeclarationsToLocals(
        IReadOnlyList<SourceDeclaration> declarations,
        LocalTable locals)
    {
        if (declarations.Count == 0 && locals.Entries.Count == 0)
        {
            return "exact-empty";
        }

        var issues = new List<string>();
        var duplicateDeclarations = declarations
            .GroupBy(static declaration => declaration.Name, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicateDeclarations.Count > 0)
        {
            issues.Add($"duplicate-source={string.Join('|', duplicateDeclarations)}");
        }

        var malformedLocals = locals.Entries.Count(static entry => !entry.Index.HasValue || !entry.Type.HasValue);
        if (malformedLocals > 0)
        {
            issues.Add($"malformed-slsd={malformedLocals}");
        }

        var unnamedLocals = locals.Entries.Count(static entry => string.IsNullOrWhiteSpace(entry.Name));
        if (unnamedLocals > 0)
        {
            issues.Add($"unnamed-slsd={unnamedLocals}");
        }

        var duplicateLocalNames = locals.Entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Name))
            .GroupBy(static entry => entry.Name!, StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (duplicateLocalNames.Count > 0)
        {
            issues.Add($"duplicate-slsd-name={string.Join('|', duplicateLocalNames)}");
        }

        var duplicateLocalIndexes = locals.Entries
            .Where(static entry => entry.Index.HasValue)
            .GroupBy(static entry => entry.Index!.Value)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .Order()
            .ToList();
        if (duplicateLocalIndexes.Count > 0)
        {
            issues.Add($"duplicate-slsd-index={string.Join('|', duplicateLocalIndexes)}");
        }

        var missing = new List<string>();
        var typeMismatches = new List<string>();
        foreach (var declaration in declarations)
        {
            var matches = locals.Entries
                .Where(entry => string.Equals(entry.Name, declaration.Name, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matches.Count != 1)
            {
                if (matches.Count == 0)
                {
                    missing.Add(declaration.Name);
                }

                continue;
            }

            var expectedInteger = declaration.Kind == DeclarationKind.Integer;
            var actualType = matches[0].Type;
            if (actualType is not { } type
                || (expectedInteger ? type != 1 : type != 0))
            {
                typeMismatches.Add(declaration.Name);
            }
        }

        if (missing.Count > 0)
        {
            issues.Add($"missing-slsd={string.Join('|', missing.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))}");
        }

        if (typeMismatches.Count > 0)
        {
            issues.Add($"type={string.Join('|', typeMismatches.OrderBy(static name => name, StringComparer.OrdinalIgnoreCase))}");
        }

        var declarationNames = declarations
            .Select(static declaration => declaration.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var extra = locals.Entries
            .Where(static entry => !string.IsNullOrWhiteSpace(entry.Name))
            .Select(static entry => entry.Name!)
            .Where(name => !declarationNames.Contains(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (extra.Count > 0)
        {
            issues.Add($"extra-slsd={string.Join('|', extra)}");
        }

        if (declarations.Count != locals.Entries.Count)
        {
            issues.Add($"count={declarations.Count}/{locals.Entries.Count}");
        }

        return issues.Count == 0
            ? "exact"
            : $"mismatch:{string.Join(';', issues.Distinct(StringComparer.Ordinal))}";
    }

    private static string DescribeNulTermination(List<ParsedSubrecord> sctx)
    {
        if (sctx.Count == 0)
        {
            return "not-present";
        }

        var statuses = sctx.Select(static subrecord => DescribeNulTermination(subrecord.Data)).ToList();
        return statuses.Count == 1
            ? statuses[0]
            : $"multiple:{string.Join('|', statuses)}";
    }

    private static string DescribeNulTermination(byte[] data)
    {
        if (data.Length == 0)
        {
            return "empty-payload";
        }

        var firstNul = Array.IndexOf(data, (byte)0);
        if (firstNul < 0)
        {
            return "unterminated";
        }

        if (firstNul == data.Length - 1)
        {
            return "terminated";
        }

        return data.AsSpan(firstNul + 1).IndexOfAnyExcept((byte)0) < 0
            ? "terminated-padded"
            : "embedded-nul-trailing-data";
    }

    private static string DecodeSource(byte[] data)
    {
        var length = Array.IndexOf(data, (byte)0);
        if (length < 0)
        {
            length = data.Length;
        }

        return EsmStringUtils.DecodeGameText(data.AsSpan(0, length));
    }

    private static string HashPayloads(IReadOnlyList<ParsedSubrecord> subrecords)
    {
        return string.Join('|', subrecords.Select(static subrecord =>
            Convert.ToHexString(SHA256.HashData(subrecord.Data)).ToLowerInvariant()));
    }

    private static string FormatCategories(IEnumerable<KeyValuePair<string, int>> categories)
    {
        return string.Join(';', categories
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    private static string NormalizeNewlines(string value) => value
        .Replace("\r\n", "\n", StringComparison.Ordinal)
        .Replace('\r', '\n');

    private enum DeclarationKind
    {
        Integer,
        Float,
        Reference
    }

    private sealed record SourceDeclaration(string Name, DeclarationKind Kind);
    private sealed record SourceScan(string Classification, List<SourceDeclaration> Declarations);
    private sealed record LocalEntry(uint? Index, string? Name, byte? Type);
    private sealed record LocalTable(List<LocalEntry> Entries, List<ScriptVariableInfo> ValidVariables);
}
