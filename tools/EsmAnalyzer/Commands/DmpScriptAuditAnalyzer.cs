using System.Security.Cryptography;
using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Script;

namespace EsmAnalyzer.Commands;

internal static class DmpScriptAuditAnalyzer
{
    private static readonly Dictionary<string, string> FunctionNames =
        ScriptComparer.BuildFunctionNameNormalizationMap();

    internal static DmpScriptAuditReport Build(RecordCollection collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        var runtimeByFormId = collection.RuntimeScripts
            .GroupBy(static script => script.FormId)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(s => s.DumpOffset).ToList());
        var mergedByFormId = collection.Scripts
            .GroupBy(static script => script.FormId)
            .ToDictionary(static group => group.Key, static group => group.OrderBy(s => s.Offset).ToList());
        var formIds = runtimeByFormId.Keys
            .Concat(mergedByFormId.Keys)
            .Distinct()
            .OrderBy(static formId => formId);

        var rows = new List<DmpScriptAuditRow>();
        foreach (var formId in formIds)
        {
            var runtimeCopies = runtimeByFormId.GetValueOrDefault(formId) ?? [];
            var mergedCopies = mergedByFormId.GetValueOrDefault(formId) ?? [];
            var copyStatus = GetRuntimeCopyStatus(runtimeCopies);
            for (var index = 0; index < runtimeCopies.Count; index++)
            {
                rows.Add(BuildRuntimeRow(runtimeCopies[index], index + 1, runtimeCopies, copyStatus));
            }

            for (var index = 0; index < mergedCopies.Count; index++)
            {
                rows.Add(BuildMergedRow(mergedCopies[index], index + 1, runtimeCopies, copyStatus));
            }
        }

        var hardCount = rows
            .SelectMany(static row => SplitList(row.HardContradictions)
                .Select(reason => (row.FormId, Reason: reason)))
            .Distinct()
            .Count();
        return new DmpScriptAuditReport(rows, hardCount);
    }

    private static DmpScriptAuditRow BuildRuntimeRow(
        RuntimeScriptData script,
        int ordinal,
        List<RuntimeScriptData> copies,
        string copyStatus)
    {
        var source = script.SourceText;
        var scda = script.CompiledData;
        var decompiled = DecompileRuntime(script);
        var declaration = ScriptDeclarationIdentityAudit.Compare(source, script.Variables);
        var comparison = Compare(source, scda, decompiled);
        var diagnostics = new List<string>();
        var hard = new List<string>();

        AddCountDiagnostics(
            diagnostics,
            script.DataSize,
            scda?.Length ?? 0,
            script.HeaderVariableCount,
            script.VariableCount,
            script.Variables.Count,
            script.RefObjectCount,
            script.ReferencedObjects.Count);
        if (scda is { Length: > 0 })
        {
            var references = script.ReferencedObjects.Select(static item => item.FormId).ToList();
            diagnostics.AddRange(ScriptBytecodeAnalyzer
                .AnalyzeEmissionSafety(scda, true, script.Variables, references)
                .Diagnostics);
        }

        AddRuntimeHardContradictions(script, copyStatus, hard);
        var trivial = script.DataSize == 4 && IsProvenTrivialScda(scda, isBigEndian: true);
        if (trivial && comparison.SourceStatementCount > 0)
        {
            hard.Add("executable-source-with-trivial-scda");
        }

        return CreateRow(
            "runtime",
            script.FormId,
            ordinal,
            script.DumpOffset,
            script.EditorId,
            copies.Count,
            copyStatus,
            source,
            source is null ? null : "runtime-nul-proven",
            scda,
            script.DataSize,
            script.HeaderVariableCount,
            script.VariableCount,
            script.Variables.Count,
            script.VariableMetadataComplete,
            script.VariablesComplete,
            script.RefObjectCount,
            script.ReferencedObjects.Count,
            script.ReferencedObjectsComplete,
            declaration,
            comparison,
            trivial,
            diagnostics,
            hard);
    }

    private static DmpScriptAuditRow BuildMergedRow(
        ScriptRecord script,
        int ordinal,
        List<RuntimeScriptData> runtimeCopies,
        string copyStatus)
    {
        var source = script.SourceText;
        var scda = script.CompiledData;
        var decompiled = script.DecompiledText ?? DecompileMerged(script);
        var declaration = ScriptDeclarationIdentityAudit.Compare(source, script.Variables);
        var comparison = Compare(source, scda, decompiled);
        var diagnostics = new List<string>();
        var hard = new List<string>();

        AddCountDiagnostics(
            diagnostics,
            script.CompiledSize,
            scda?.Length ?? 0,
            script.VariableCount,
            script.VariableCount,
            script.Variables.Count,
            script.RefObjectCount,
            script.ReferencedObjects.Count);
        if (scda is { Length: > 0 })
        {
            diagnostics.AddRange(ScriptBytecodeAnalyzer
                .AnalyzeEmissionSafety(
                    scda,
                    script.IsBigEndian,
                    script.Variables,
                    script.ReferencedObjects)
                .Diagnostics);
        }

        var trivial = script.CompiledSize == 4 && IsProvenTrivialScda(scda, script.IsBigEndian);
        if (trivial && comparison.SourceStatementCount > 0)
        {
            hard.Add("executable-source-with-trivial-scda");
        }

        var selectedRuntime = runtimeCopies.Count == 0
            ? null
            : ScriptRuntimeMerger.SelectConsistentRuntimeData(runtimeCopies);
        var sourceProof = source is null
            ? null
            : script.SourceTextOrigin == ScriptSourceTextOrigin.RuntimeSameObject
              && selectedRuntime is not null
              && string.Equals(selectedRuntime.SourceText, source, StringComparison.Ordinal)
                ? "same-dump-runtime-nul-proven"
                : "fragment-termination-unrepresented";
        return CreateRow(
            "merged",
            script.FormId,
            ordinal,
            script.Offset,
            script.EditorId,
            runtimeCopies.Count,
            copyStatus,
            source,
            sourceProof,
            scda,
            script.CompiledSize,
            script.VariableCount,
            script.VariableCount,
            script.Variables.Count,
            null,
            null,
            script.RefObjectCount,
            script.ReferencedObjects.Count,
            null,
            declaration,
            comparison,
            trivial,
            diagnostics,
            hard);
    }

    private static DmpScriptAuditRow CreateRow(
        string rowKind,
        uint formId,
        int ordinal,
        long offset,
        string? editorId,
        int runtimeCopyCount,
        string runtimeCopyStatus,
        string? source,
        string? sourceProof,
        byte[]? scda,
        uint declaredScdaLength,
        uint headerVariableCount,
        uint effectiveVariableCount,
        int variableListCount,
        bool? variableMetadataComplete,
        bool? variablesComplete,
        uint headerReferenceCount,
        int referenceListCount,
        bool? referencesComplete,
        ScriptDeclarationIdentityResult declaration,
        ScriptAuditComparison comparison,
        bool trivial,
        IEnumerable<string> diagnostics,
        IEnumerable<string> hard)
    {
        var sourceBytes = source is null ? [] : Encoding.UTF8.GetBytes(source);
        var scdaLength = scda?.Length ?? 0;
        return new DmpScriptAuditRow
        {
            RowKind = rowKind,
            FormId = formId,
            CopyOrdinal = ordinal,
            DumpOffset = offset,
            EditorId = editorId,
            RuntimeCopyCount = runtimeCopyCount,
            RuntimeCopyStatus = runtimeCopyStatus,
            ContentClassification = Classify(source, scda),
            SourceCharLength = source?.Length ?? 0,
            SourceUtf8Length = sourceBytes.Length,
            SourceSha256 = source is null ? null : Hash(sourceBytes),
            SourceTerminatedProof = sourceProof,
            ScdaLength = scdaLength,
            ScdaSha256 = scda is null ? null : Hash(scda),
            DeclaredScdaLength = declaredScdaLength,
            DeclaredScdaLengthMatches = declaredScdaLength == scdaLength,
            HeaderVariableCount = headerVariableCount,
            EffectiveVariableCount = effectiveVariableCount,
            VariableListCount = variableListCount,
            VariableMetadataComplete = variableMetadataComplete,
            VariablesComplete = variablesComplete,
            HeaderVariableCountMatchesList = headerVariableCount == variableListCount,
            EffectiveVariableCountMatchesList = effectiveVariableCount == variableListCount,
            HeaderReferenceCount = headerReferenceCount,
            ReferenceListCount = referenceListCount,
            ReferencesComplete = referencesComplete,
            HeaderReferenceCountMatchesList = headerReferenceCount == referenceListCount,
            DeclarationIdentityVerdict = declaration.Verdict,
            DeclarationCount = declaration.DeclarationCount,
            DeclarationIdentities = declaration.DeclarationIdentities,
            SlsdIdentities = declaration.SlsdIdentities,
            DeclarationIdentityDetails = declaration.Details,
            SourceStatementCount = comparison.SourceStatementCount,
            DecompiledStatementCount = comparison.DecompiledStatementCount,
            ComparisonStatus = comparison.Status,
            ComparisonMatchCount = comparison.Result?.MatchCount ?? 0,
            ComparisonMismatchCount = comparison.Result?.TotalMismatches ?? 0,
            ComparisonToleratedCount = comparison.Result?.TotalTolerated ?? 0,
            ComparisonMismatchCategories = FormatCounts(comparison.Result?.MismatchesByCategory),
            ComparisonToleratedCategories = FormatCounts(comparison.Result?.ToleratedDifferences),
            ComparisonMatchRate = comparison.Result is { TotalLines: > 0 } result ? result.MatchRate : null,
            ProvenTrivialScda = trivial,
            StructuralDiagnostics = JoinList(diagnostics),
            HardContradictions = JoinList(hard)
        };
    }

    private static void AddRuntimeHardContradictions(
        RuntimeScriptData script,
        string copyStatus,
        List<string> hard)
    {
        if (copyStatus == "conflicting")
        {
            hard.Add("runtime-copy-conflict");
        }

        if (script.VariablesComplete && !script.VariableMetadataComplete)
        {
            hard.Add("variables-complete-with-incomplete-metadata");
        }

        if (script.VariablesComplete && script.VariableCount != script.Variables.Count)
        {
            hard.Add("variables-complete-effective-count-mismatch");
        }

        if (script.VariablesComplete
            && script.HeaderVariableCount != 0
            && script.HeaderVariableCount != script.Variables.Count)
        {
            hard.Add("variables-complete-header-count-mismatch");
        }

        if (script.VariablesComplete && HasInvalidCompleteVariableTable(script))
        {
            hard.Add("variables-complete-invalid-identity-table");
        }

        if (script.ReferencedObjectsComplete && script.RefObjectCount != script.ReferencedObjects.Count)
        {
            hard.Add("references-complete-count-mismatch");
        }
    }

    private static bool HasInvalidCompleteVariableTable(RuntimeScriptData script)
    {
        var ids = new HashSet<uint>();
        return script.Variables.Any(variable =>
            variable.Index == 0
            || variable.Index > script.LastVariableId
            || variable.Type > 1
            || string.IsNullOrWhiteSpace(variable.Name)
            || !ids.Add(variable.Index));
    }

    private static void AddCountDiagnostics(
        List<string> diagnostics,
        uint dataSize,
        int scdaLength,
        uint headerVariableCount,
        uint effectiveVariableCount,
        int variableListCount,
        uint referenceCount,
        int referenceListCount)
    {
        if (dataSize != scdaLength)
        {
            diagnostics.Add("declared-scda-length-mismatch");
        }

        if (headerVariableCount != variableListCount)
        {
            diagnostics.Add("header-variable-count-mismatch");
        }

        if (effectiveVariableCount != variableListCount)
        {
            diagnostics.Add("effective-variable-count-mismatch");
        }

        if (referenceCount != referenceListCount)
        {
            diagnostics.Add("reference-count-mismatch");
        }
    }

    private static ScriptAuditComparison Compare(string? source, byte[]? scda, string? decompiled)
    {
        var sourceStatements = CountExecutableStatements(source);
        var decompiledStatements = CountExecutableStatements(decompiled);
        if (string.IsNullOrEmpty(source))
        {
            return new ScriptAuditComparison(
                sourceStatements, decompiledStatements, "not-comparable-no-source", null);
        }

        if (scda is not { Length: > 0 })
        {
            return new ScriptAuditComparison(
                sourceStatements, decompiledStatements, "not-comparable-no-scda", null);
        }

        var result = ScriptComparer.CompareScripts(source, decompiled ?? string.Empty, FunctionNames);
        return new ScriptAuditComparison(sourceStatements, decompiledStatements, "compared", result);
    }

    private static string? DecompileRuntime(RuntimeScriptData script)
    {
        if (script.CompiledData is not { Length: > 0 } scda)
        {
            return null;
        }

        var references = script.ReferencedObjects.Select(static item => item.FormId).ToList();
        var names = script.ReferencedObjects
            .Where(static item => !string.IsNullOrEmpty(item.EditorId))
            .GroupBy(static item => item.FormId)
            .ToDictionary(static group => group.Key, static group => group.First().EditorId);
        return new ScriptDecompiler(
            script.Variables,
            references,
            formId => names.GetValueOrDefault(formId),
            isBigEndian: true,
            script.EditorId).Decompile(scda);
    }

    private static string? DecompileMerged(ScriptRecord script)
    {
        if (script.CompiledData is not { Length: > 0 } scda)
        {
            return null;
        }

        return new ScriptDecompiler(
            script.Variables,
            script.ReferencedObjects,
            _ => null,
            script.IsBigEndian,
            script.EditorId).Decompile(scda);
    }

    private static int CountExecutableStatements(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return ScriptComparer.ExtractMeaningfulLines(text).Count(static line =>
        {
            var firstSpace = line.IndexOf(' ');
            var firstWord = firstSpace < 0 ? line : line[..firstSpace];
            return !firstWord.Equals("ScriptName", StringComparison.OrdinalIgnoreCase)
                   && !firstWord.Equals("scn", StringComparison.OrdinalIgnoreCase);
        });
    }

    private static bool IsProvenTrivialScda(byte[]? scda, bool isBigEndian) =>
        scda is { Length: 4 }
        && (isBigEndian
            ? scda[0] == 0x00 && scda[1] == 0x1D && scda[2] == 0x00 && scda[3] == 0x00
            : scda[0] == 0x1D && scda[1] == 0x00 && scda[2] == 0x00 && scda[3] == 0x00);

    private static string GetRuntimeCopyStatus(List<RuntimeScriptData> copies)
    {
        if (copies.Count == 0)
        {
            return "none";
        }

        if (copies.Count == 1)
        {
            return "unique";
        }

        return ScriptRuntimeMerger.SelectConsistentRuntimeData(copies) is null
            ? "conflicting"
            : "equivalent";
    }

    private static string Classify(string? source, byte[]? scda) =>
        (!string.IsNullOrEmpty(source), scda is { Length: > 0 }) switch
        {
            (true, true) => "both",
            (true, false) => "source-only",
            (false, true) => "scda-only",
            _ => "neither"
        };

    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));

    private static string FormatCounts(IReadOnlyDictionary<string, int>? counts) => counts is null
        ? string.Empty
        : string.Join(
            '|',
            counts.OrderBy(static item => item.Key, StringComparer.Ordinal)
                .Select(static item => $"{item.Key}={item.Value}"));

    private static string JoinList(IEnumerable<string> values) => string.Join(
        '|',
        values.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal));

    private static string[] SplitList(string value) =>
        value.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private sealed record ScriptAuditComparison(
        int SourceStatementCount,
        int DecompiledStatementCount,
        string Status,
        ScriptComparisonResult? Result);
}
