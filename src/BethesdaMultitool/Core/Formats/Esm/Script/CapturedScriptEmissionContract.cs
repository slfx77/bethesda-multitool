using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Same-dump proof for recovered SCTX/SCDA bundles. Source text is optional diagnostic
///     material; executable bytes are authoritative and may be emitted only when their
///     header/tables are structurally complete. Clean on-disk ESM source is outside this
///     recovery policy and is preserved verbatim.
/// </summary>
internal static class CapturedScriptEmissionContract
{
    private static readonly Dictionary<string, string> FunctionNameNormalizationMap =
        ScriptComparer.BuildFunctionNameNormalizationMap();

    internal sealed record SourceDecision(
        bool ExecutableBundleSafe,
        string? SourceText,
        string? BundleIssue,
        string? SourceIssue);

    internal sealed record StandaloneDecision(
        ScriptRecord Script,
        string? BundleIssue,
        string? SourceIssue);

    internal static SourceDecision EvaluateInline(
        bool isDmpDerived,
        ScriptSourceTextOrigin sourceTextOrigin,
        byte[]? compiledData,
        string? sourceText,
        string? decompiledText,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects,
        bool isBigEndian)
    {
        if (!isDmpDerived)
        {
            return new SourceDecision(true, sourceText, null, null);
        }

        if (compiledData is not { Length: > 0 })
        {
            // A same-dump source-only capture remains useful recovery evidence. It is
            // serialized with no SCDA and therefore is not treated as executable proof.
            return new SourceDecision(true, sourceText, null, null);
        }

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            compiledData,
            isBigEndian,
            variables,
            referencedObjects);
        if (!safety.IsSafeForEmission)
        {
            return new SourceDecision(
                false,
                null,
                $"unsafe SCDA bundle ({FormatSafetyDiagnostics(safety)})",
                string.IsNullOrEmpty(sourceText)
                    ? null
                    : "SCTX cannot be proven against an unsafe SCDA bundle");
        }

        if (string.IsNullOrEmpty(sourceText))
        {
            return new SourceDecision(true, null, null, null);
        }

        if (sourceTextOrigin == ScriptSourceTextOrigin.None)
        {
            return new SourceDecision(
                true,
                null,
                null,
                "DMP-derived SCTX has no same-dump source provenance");
        }

        if (string.IsNullOrEmpty(decompiledText))
        {
            return new SourceDecision(
                true,
                null,
                null,
                "DMP-derived SCTX has no full-context SCDA decompilation proof");
        }

        var comparison = ScriptComparer.CompareScripts(
            sourceText,
            decompiledText,
            FunctionNameNormalizationMap);
        if (comparison.TotalMismatches == 0)
        {
            var declarationIssue = FindSourceLocalDeclarationIssue(sourceText, variables);
            if (declarationIssue is not null)
            {
                // Local declarations are not present in decompiled SCDA text, so the
                // statement comparer alone cannot prove them. Keep the structurally safe
                // executable bundle, but never serialize an SCTX whose declaration table
                // disagrees with the same block's SLSD/SCVR table.
                return new SourceDecision(true, null, null, declarationIssue);
            }

            return new SourceDecision(true, sourceText, null, null);
        }

        return new SourceDecision(
            true,
            null,
            null,
            $"non-tolerated-mismatches={comparison.TotalMismatches} "
            + $"[{FormatCategories(comparison.MismatchesByCategory)}]");
    }

    internal static StandaloneDecision EvaluateStandalone(ScriptRecord script)
    {
        ArgumentNullException.ThrowIfNull(script);

        if (script.CompiledData is not { Length: > 0 } compiledData)
        {
            var hasExecutableMetadata = script.CompiledSize != 0
                                        || script.VariableCount != 0
                                        || script.RefObjectCount != 0
                                        || script.IsCompiled
                                        || script.Variables.Count != 0
                                        || script.ReferencedObjects.Count != 0
                                        || script.HasMalformedSerializedHeader
                                        || script.HasMalformedSerializedTable
                                        || script.IsIncompleteExecutableBundle;
            if (hasExecutableMetadata)
            {
                const string issue =
                    "SCPT carries SCHR/SLSD/SCRO/SCRV executable metadata without SCDA";
                return new StandaloneDecision(
                    script with { IsIncompleteExecutableBundle = true },
                    issue,
                    null);
            }

            if (!string.IsNullOrEmpty(script.SourceText))
            {
                // Make the recovery classification explicit: SCTX is retained for
                // inspection, but there is no executable SCDA bundle.
                return new StandaloneDecision(
                    script with
                    {
                        IsCompiled = false,
                        CompiledSize = 0,
                        IsIncompleteExecutableBundle = false,
                    },
                    null,
                    null);
            }

            return new StandaloneDecision(script, null, null);
        }

        var consistencyIssue = FindStandaloneBundleConsistencyIssue(script, compiledData);
        if (consistencyIssue is not null)
        {
            return new StandaloneDecision(
                script with
                {
                    SourceText = null,
                    SourceTextOrigin = ScriptSourceTextOrigin.None,
                    IsIncompleteExecutableBundle = true,
                },
                consistencyIssue,
                null);
        }

        var sourceDecision = EvaluateInline(
            isDmpDerived: true,
            script.SourceTextOrigin,
            compiledData,
            script.SourceText,
            script.DecompiledText,
            script.Variables,
            script.ReferencedObjects,
            script.IsBigEndian);
        if (!sourceDecision.ExecutableBundleSafe)
        {
            return new StandaloneDecision(
                script with
                {
                    SourceText = null,
                    SourceTextOrigin = ScriptSourceTextOrigin.None,
                    IsIncompleteExecutableBundle = true,
                },
                sourceDecision.BundleIssue,
                sourceDecision.SourceIssue);
        }

        var sourceIssue = sourceDecision.SourceIssue;
        if (sourceIssue is null && sourceDecision.SourceText is not null)
        {
            sourceIssue = FindStandaloneSourceDeclarationIssue(script, sourceDecision.SourceText);
        }

        return new StandaloneDecision(
            script with
            {
                SourceText = sourceIssue is null ? sourceDecision.SourceText : null,
                SourceTextOrigin = sourceIssue is null
                    ? script.SourceTextOrigin
                    : ScriptSourceTextOrigin.None,
                IsIncompleteExecutableBundle = false,
            },
            null,
            sourceIssue);
    }

    internal static string DecompileInline(
        byte[]? compiledData,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects,
        bool isBigEndian,
        string? scriptName,
        Func<uint, string?> resolveFormName,
        ScriptFunctionSet? functions = null)
    {
        if (compiledData is not { Length: > 0 })
        {
            return string.Empty;
        }

        try
        {
            var decompiler = new ScriptDecompiler(
                [.. variables],
                [.. referencedObjects],
                resolveFormName,
                isBigEndian,
                scriptName,
                functions: functions);
            return decompiler.Decompile(compiledData);
        }
        catch
        {
            // A failed decompilation is not correspondence proof. The caller retains the
            // bytecode only when structural analysis passed and omits captured SCTX.
            return string.Empty;
        }
    }

    internal static bool InferBytecodeEndian(
        byte[]? compiledData,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects,
        bool fallbackIsBigEndian)
    {
        if (compiledData is not { Length: >= 4 } compiled)
        {
            return fallbackIsBigEndian;
        }

        var littleEndian = ScriptBytecodeAnalyzer.Analyze(
            compiled,
            isBigEndian: false,
            variables,
            referencedObjects);
        var bigEndian = ScriptBytecodeAnalyzer.Analyze(
            compiled,
            isBigEndian: true,
            variables,
            referencedObjects);
        var littleClean = littleEndian.WalkedToEnd && !littleEndian.HasDiagnostics;
        var bigClean = bigEndian.WalkedToEnd && !bigEndian.HasDiagnostics;
        if (littleClean != bigClean)
        {
            return bigClean;
        }

        return fallbackIsBigEndian;
    }

    private static string? FindStandaloneBundleConsistencyIssue(
        ScriptRecord script,
        byte[] compiledData)
    {
        if (script.IsIncompleteExecutableBundle
            && !script.HasMalformedSerializedHeader
            && !script.HasMalformedSerializedTable)
        {
            return "SCPT executable bundle was previously marked incomplete";
        }

        if (script.HasMalformedSerializedHeader)
        {
            return "SCPT has a short/malformed SCHR";
        }

        if (script.HasMalformedSerializedTable)
        {
            return "SCPT has a short or orphaned SLSD/SCVR/SCRO/SCRV component";
        }

        if (!script.ExecutableBundleFromRuntime && !script.HasSerializedHeader)
        {
            return "DMP-fragment SCPT has SCDA without a complete SCHR";
        }

        if (script.CompiledSize != (uint)compiledData.Length)
        {
            return $"SCHR CompiledSize={script.CompiledSize} does not match SCDA length={compiledData.Length}";
        }

        if (script.VariableCount != (uint)script.Variables.Count)
        {
            return $"SCHR VariableCount={script.VariableCount} does not match SLSD count={script.Variables.Count}";
        }

        if (script.RefObjectCount != (uint)script.ReferencedObjects.Count)
        {
            return $"SCHR RefObjectCount={script.RefObjectCount} does not match SCRO/SCRV count={script.ReferencedObjects.Count}";
        }

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            compiledData,
            script.IsBigEndian,
            script.Variables,
            script.ReferencedObjects);
        return safety.IsSafeForEmission
            ? null
            : $"unsafe SCDA bundle ({FormatSafetyDiagnostics(safety)})";
    }

    private static string? FindStandaloneSourceDeclarationIssue(
        ScriptRecord script,
        string sourceText)
    {
        var scriptNames = new List<string>();
        var declarations = new List<SourceLocalDeclaration>();
        foreach (var rawLine in NormalizeLines(sourceText))
        {
            var tokens = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (tokens[0].Equals("scn", StringComparison.OrdinalIgnoreCase)
                || tokens[0].Equals("ScriptName", StringComparison.OrdinalIgnoreCase))
            {
                if (tokens.Length != 2)
                {
                    return $"malformed {tokens[0]} declaration in compiled SCTX";
                }

                scriptNames.Add(tokens[1]);
                continue;
            }

            if (TryGetDeclarationStorage(tokens[0], out var isInteger))
            {
                if (tokens.Length != 2)
                {
                    return $"malformed {tokens[0]} local declaration in compiled SCTX";
                }

                declarations.Add(new SourceLocalDeclaration(tokens[1], isInteger));
            }
        }

        if (scriptNames.Count != 1)
        {
            return $"compiled SCTX must contain exactly one scn/ScriptName declaration; found {scriptNames.Count}";
        }

        var effectiveEditorId = ScriptRecordEmissionPolicy.ResolveEditorId(script);
        if (string.IsNullOrEmpty(effectiveEditorId)
            || !string.Equals(scriptNames[0], effectiveEditorId, StringComparison.OrdinalIgnoreCase))
        {
            return $"SCTX script identity '{scriptNames[0]}' does not exactly match EDID "
                   + $"'{effectiveEditorId ?? "<none>"}'";
        }

        return FindSourceLocalDeclarationIssue(declarations, script.Variables);
    }

    private static string? FindSourceLocalDeclarationIssue(
        string sourceText,
        IReadOnlyList<ScriptVariableInfo> variables)
    {
        var declarations = new List<SourceLocalDeclaration>();
        foreach (var rawLine in NormalizeLines(sourceText))
        {
            var tokens = rawLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (!TryGetDeclarationStorage(tokens[0], out var isInteger))
            {
                continue;
            }

            if (tokens.Length != 2)
            {
                return $"malformed {tokens[0]} local declaration in compiled SCTX";
            }

            declarations.Add(new SourceLocalDeclaration(tokens[1], isInteger));
        }

        return FindSourceLocalDeclarationIssue(declarations, variables);
    }

    private static string? FindSourceLocalDeclarationIssue(
        IReadOnlyList<SourceLocalDeclaration> declarations,
        IReadOnlyList<ScriptVariableInfo> variables)
    {
        if (declarations.Count != variables.Count)
        {
            return $"SCTX local declaration count={declarations.Count} does not match SLSD count={variables.Count}";
        }

        var duplicateDeclaration = declarations
            .GroupBy(static declaration => declaration.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() != 1);
        if (duplicateDeclaration is not null)
        {
            return $"SCTX local '{duplicateDeclaration.Key}' is declared more than once";
        }

        var unnamedVariable = variables.FirstOrDefault(
            static variable => string.IsNullOrEmpty(variable.Name));
        if (unnamedVariable is not null)
        {
            return $"SLSD local {unnamedVariable.Index} has no exact SCVR name";
        }

        var duplicateVariable = variables
            .GroupBy(static variable => variable.Name!, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() != 1);
        if (duplicateVariable is not null)
        {
            return $"SLSD/SCVR local '{duplicateVariable.Key}' occurs more than once";
        }

        foreach (var variable in variables)
        {
            var matches = declarations
                .Where(declaration => string.Equals(
                    declaration.Name, variable.Name, StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (matches.Length != 1)
            {
                return $"SLSD/SCVR local '{variable.Name}' has no unique exact SCTX declaration";
            }

            var slsdIsInteger = variable.Type != 0;
            if (matches[0].IsInteger != slsdIsInteger)
            {
                return $"SCTX local '{variable.Name}' storage does not match SLSD type {variable.Type}";
            }
        }

        return null;
    }

    private readonly record struct SourceLocalDeclaration(string Name, bool IsInteger);

    private static IEnumerable<string> NormalizeLines(string sourceText)
    {
        foreach (var rawLine in sourceText
                     .Replace("\r\n", "\n", StringComparison.Ordinal)
                     .Replace('\r', '\n')
                     .Split('\n'))
        {
            var commentIndex = rawLine.IndexOf(';');
            var line = (commentIndex >= 0 ? rawLine[..commentIndex] : rawLine).Trim();
            if (line.Length > 0)
            {
                yield return line;
            }
        }
    }

    private static bool TryGetDeclarationStorage(string keyword, out bool isInteger)
    {
        if (keyword.Equals("float", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("ref", StringComparison.OrdinalIgnoreCase))
        {
            isInteger = false;
            return true;
        }

        if (keyword.Equals("short", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("int", StringComparison.OrdinalIgnoreCase)
            || keyword.Equals("long", StringComparison.OrdinalIgnoreCase))
        {
            isInteger = true;
            return true;
        }

        isInteger = false;
        return false;
    }

    private static string FormatSafetyDiagnostics(ScriptBytecodeEmissionSafetyAnalysis safety) =>
        safety.Diagnostics.Count == 0
            ? "no analyzer detail"
            : string.Join(" | ", safety.Diagnostics);

    private static string FormatCategories(IReadOnlyDictionary<string, int> categories) =>
        string.Join(
            ", ",
            categories
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
}
