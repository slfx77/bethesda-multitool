using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Same-dump proof for recovered SCTX/SCDA bundles. Executable bytes are authoritative and
///     may be emitted only when their header/tables are structurally complete. Clean on-disk ESM
///     source is outside this recovery policy and is preserved verbatim.
///     <para>
///         USER RULING 2026-09-03: "compiled scripts should be preserved (converted to PC format),
///         while their source text should be emitted if present, or as the decompilation if not."
///         So a safe bundle now always carries readable SCTX. Captured text still has to prove
///         itself against the same record's bytecode — an unproven capture is a claim about what
///         the script said, and shipping it beside contradicting SCDA is worse than shipping
///         neither. When the capture is absent or unproven, <see cref="BuildDecompiledSource" />
///         renders the accepted SCDA itself, which cannot contradict the bytecode because it *is*
///         the bytecode. Every such swap is reported through
///         <see cref="SourceDecision.SourceIssue" />, so nothing is silently substituted.
///     </para>
/// </summary>
internal static class CapturedScriptEmissionContract
{
    private static readonly Dictionary<string, string> FunctionNameNormalizationMap =
        ScriptComparer.BuildFunctionNameNormalizationMap();

    internal static SourceDecision EvaluateInline(
        bool isDmpDerived,
        ScriptSourceTextOrigin sourceTextOrigin,
        byte[]? compiledData,
        string? sourceText,
        string? decompiledText,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects,
        bool isBigEndian,
        string? scriptName = null)
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
            // The bundle itself is rejected, so there is no accepted SCDA to decompile from
            // and nothing to attach source to. This is the one path that still emits neither.
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
            return Decompiled(
                decompiledText,
                variables,
                scriptName,
                "no source text was resident in the capture");
        }

        if (sourceTextOrigin == ScriptSourceTextOrigin.None)
        {
            return Decompiled(
                decompiledText,
                variables,
                scriptName,
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
                // statement comparer alone cannot prove them. Never serialize a *captured*
                // SCTX whose declaration table disagrees with the same block's SLSD/SCVR —
                // fall back to the decompilation, whose declaration block is synthesized
                // from that very table and therefore agrees with it by construction.
                return Decompiled(decompiledText, variables, scriptName, declarationIssue);
            }

            return new SourceDecision(true, sourceText, null, null);
        }

        return Decompiled(
            decompiledText,
            variables,
            scriptName,
            $"non-tolerated-mismatches={comparison.TotalMismatches} "
            + $"[{FormatCategories(comparison.MismatchesByCategory)}]");
    }

    /// <summary>
    ///     Wrap <see cref="BuildDecompiledSource" /> as a decision, recording why the captured
    ///     text was not used. When there is no decompilation to fall back on, the bundle still
    ///     ships — bytecode without source is the pre-ruling behaviour and remains the floor.
    /// </summary>
    private static SourceDecision Decompiled(
        string? decompiledText,
        IReadOnlyList<ScriptVariableInfo> variables,
        string? scriptName,
        string reason)
    {
        var synthesized = BuildDecompiledSource(decompiledText, variables, scriptName);
        return synthesized is null
            ? new SourceDecision(true, null, null, reason)
            : new SourceDecision(true, synthesized, null, $"{reason}; emitted SCTX from SCDA decompilation", true);
    }

    /// <summary>
    ///     Render an accepted SCDA decompilation as GECK-shaped SCTX: a provenance banner, the
    ///     <c>scn</c> line, a declaration block synthesized from SLSD/SCVR, then the statements.
    ///     <para>
    ///         The decompiler walks bytecode, and FNV bytecode carries no declaration opcodes
    ///         (measured against retail 2026-09-03: <c>VarShort</c>/<c>VarLong</c>/<c>VarFloat</c>
    ///         never appear in a shipped SCDA), so the declarations have to come from the record's
    ///         own variable table. That is also what makes the result self-consistent: the block is
    ///         generated from the exact SLSD/SCVR pairs that ship beside it.
    ///     </para>
    ///     <para>
    ///         SLSD stores one type bit — 0 = float-or-ref, non-zero = integer — so <c>ref</c> and
    ///         <c>float</c> are genuinely indistinguishable from the table alone. Ref-ness is taken
    ///         from bytecode evidence instead: only a reference variable can appear in member
    ///         position (<c>name.Something</c>) in the decompiled statements. A Type-0 local with no
    ///         such use is declared <c>float</c>, which is the safe default — both are non-integer,
    ///         so either declaration satisfies the SLSD storage-class check, and the banner tells a
    ///         human to trust SCDA over the declaration if they disagree.
    ///     </para>
    /// </summary>
    /// <returns>Synthesized SCTX, or null when there is no decompiled text to render.</returns>
    internal static string? BuildDecompiledSource(
        string? decompiledText,
        IReadOnlyList<ScriptVariableInfo> variables,
        string? scriptName)
    {
        if (string.IsNullOrWhiteSpace(decompiledText))
        {
            return null;
        }

        var lines = NormalizeToLines(decompiledText);
        var builder = new StringBuilder();
        builder.AppendLine("; === Decompiled from captured SCDA — no proven source text in the dump. ===");
        builder.AppendLine("; The compiled bytecode beside this text is authoritative; these statements");
        builder.AppendLine("; are its rendering. Local declarations are synthesized from SLSD/SCVR.");

        // The decompiler emits the ScriptName line from the bytecode's own opcode. Keep that
        // line first (GECK requires it) and insert declarations directly after it, so the
        // emitted text has the shape a hand-authored script would.
        //
        // When the record's own identity is known it OVERRIDES the decompiled name: a script's
        // identity is its EDID, and an SCTX whose scn line names something else is exactly the
        // defect FindStandaloneSourceDeclarationIssue rejects captured text for. Emitting the
        // record's EDID keeps the substitution self-consistent by the same rule.
        var nameIndex = lines.FindIndex(static line =>
            line.TrimStart().StartsWith("ScriptName", StringComparison.OrdinalIgnoreCase)
            || line.TrimStart().StartsWith("scn ", StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrWhiteSpace(scriptName))
        {
            builder.AppendLine($"ScriptName {scriptName}");
            if (nameIndex >= 0)
            {
                lines.RemoveAt(nameIndex);
            }
        }
        else if (nameIndex >= 0)
        {
            builder.AppendLine(lines[nameIndex]);
            lines.RemoveAt(nameIndex);
        }

        var declarations = BuildDeclarationBlock(variables, decompiledText);
        if (declarations.Count > 0)
        {
            builder.AppendLine();
            foreach (var declaration in declarations)
            {
                builder.AppendLine(declaration);
            }
        }
        else if (variables.Count > 0)
        {
            // An unnamed or duplicated local means no declaration block can be written that
            // agrees with SLSD/SCVR. Say so in the text rather than emitting a block that
            // silently disagrees with the table shipping beside it.
            builder.AppendLine(
                $"; {variables.Count} local(s) could not be declared — SLSD/SCVR names are incomplete.");
        }

        builder.AppendLine();
        foreach (var line in lines)
        {
            builder.AppendLine(line);
        }

        return builder.ToString().TrimEnd();
    }

    /// <summary>
    ///     One <c>short</c>/<c>float</c>/<c>ref</c> line per SLSD/SCVR local, in table order.
    ///     Returns an empty list when the table cannot produce a block that would satisfy
    ///     <see cref="FindSourceLocalDeclarationIssue(IReadOnlyList{SourceLocalDeclaration}, IReadOnlyList{ScriptVariableInfo})" />.
    /// </summary>
    private static List<string> BuildDeclarationBlock(
        IReadOnlyList<ScriptVariableInfo> variables,
        string decompiledText)
    {
        if (variables.Count == 0)
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var variable in variables)
        {
            if (string.IsNullOrEmpty(variable.Name) || !names.Add(variable.Name))
            {
                return [];
            }
        }

        var declarations = new List<string>(variables.Count);
        foreach (var variable in variables)
        {
            var storage = variable.Type != 0
                ? "short"
                : UsedAsReference(decompiledText, variable.Name!)
                    ? "ref"
                    : "float";
            declarations.Add($"{storage} {variable.Name}");
        }

        return declarations;
    }

    /// <summary>
    ///     True when the decompiled statements use the local in member position — the only
    ///     syntactic slot a non-reference local cannot occupy. Deliberately narrow: a false
    ///     negative declares a ref as <c>float</c> (harmless, both are non-integer storage),
    ///     while a false positive would declare a float as <c>ref</c>.
    /// </summary>
    private static bool UsedAsReference(string decompiledText, string name)
    {
        var index = decompiledText.IndexOf(name, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            var after = index + name.Length;
            var boundedLeft = index == 0 || !IsIdentifierChar(decompiledText[index - 1]);
            if (boundedLeft && after < decompiledText.Length && decompiledText[after] == '.')
            {
                return true;
            }

            index = decompiledText.IndexOf(name, index + 1, StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsIdentifierChar(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static List<string> NormalizeToLines(string text)
    {
        return
        [
            .. text
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n')
                .Select(static line => line.TrimEnd())
        ];
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
                        IsIncompleteExecutableBundle = false
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
                    IsIncompleteExecutableBundle = true
                },
                consistencyIssue,
                null);
        }

        var sourceDecision = EvaluateInline(
            true,
            script.SourceTextOrigin,
            compiledData,
            script.SourceText,
            script.DecompiledText,
            script.Variables,
            script.ReferencedObjects,
            script.IsBigEndian,
            ScriptRecordEmissionPolicy.ResolveEditorId(script));
        if (!sourceDecision.ExecutableBundleSafe)
        {
            return new StandaloneDecision(
                script with
                {
                    SourceText = null,
                    SourceTextOrigin = ScriptSourceTextOrigin.None,
                    IsIncompleteExecutableBundle = true
                },
                sourceDecision.BundleIssue,
                sourceDecision.SourceIssue);
        }

        if (sourceDecision.SourceIsDecompiled)
        {
            // Synthesized text is generated FROM this record's SLSD/SCVR table and carries the
            // resolved EDID as its scn identity, so re-running the captured-source declaration
            // audit on it would only re-derive its own inputs — and, on a table too incomplete
            // to declare, would discard the very text the ruling exists to guarantee.
            return new StandaloneDecision(
                script with
                {
                    SourceText = sourceDecision.SourceText,
                    SourceTextOrigin = ScriptSourceTextOrigin.DecompiledFromBytecode,
                    IsIncompleteExecutableBundle = false
                },
                null,
                sourceDecision.SourceIssue);
        }

        var sourceIssue = sourceDecision.SourceIssue;
        if (sourceIssue is null && sourceDecision.SourceText is not null)
        {
            sourceIssue = FindStandaloneSourceDeclarationIssue(script, sourceDecision.SourceText);
        }

        if (sourceIssue is not null)
        {
            // The captured text failed the standalone identity/declaration audit. The ruling
            // still applies: fall back to the decompilation rather than shipping no source.
            var fallback = BuildDecompiledSource(
                script.DecompiledText,
                script.Variables,
                ScriptRecordEmissionPolicy.ResolveEditorId(script));
            if (fallback is not null)
            {
                return new StandaloneDecision(
                    script with
                    {
                        SourceText = fallback,
                        SourceTextOrigin = ScriptSourceTextOrigin.DecompiledFromBytecode,
                        IsIncompleteExecutableBundle = false
                    },
                    null,
                    $"{sourceIssue}; emitted SCTX from SCDA decompilation");
            }
        }

        return new StandaloneDecision(
            script with
            {
                SourceText = sourceIssue is null ? sourceDecision.SourceText : null,
                SourceTextOrigin = sourceIssue is null
                    ? script.SourceTextOrigin
                    : ScriptSourceTextOrigin.None,
                IsIncompleteExecutableBundle = false
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
            false,
            variables,
            referencedObjects);
        var bigEndian = ScriptBytecodeAnalyzer.Analyze(
            compiled,
            true,
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
            return
                $"SCHR RefObjectCount={script.RefObjectCount} does not match SCRO/SCRV count={script.ReferencedObjects.Count}";
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

        var unnamedVariable = variables.FirstOrDefault(static variable => string.IsNullOrEmpty(variable.Name));
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

    private static string FormatSafetyDiagnostics(ScriptBytecodeEmissionSafetyAnalysis safety)
    {
        return safety.Diagnostics.Count == 0
            ? "no analyzer detail"
            : string.Join(" | ", safety.Diagnostics);
    }

    private static string FormatCategories(IReadOnlyDictionary<string, int> categories)
    {
        return string.Join(
            ", ",
            categories
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key}={pair.Value}"));
    }

    internal sealed record SourceDecision(
        bool ExecutableBundleSafe,
        string? SourceText,
        string? BundleIssue,
        string? SourceIssue,
        bool SourceIsDecompiled = false);

    internal sealed record StandaloneDecision(
        ScriptRecord Script,
        string? BundleIssue,
        string? SourceIssue);

    private readonly record struct SourceLocalDeclaration(string Name, bool IsInteger);
}
