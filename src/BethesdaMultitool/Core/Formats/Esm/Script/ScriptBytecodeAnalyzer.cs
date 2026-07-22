using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Script;

/// <summary>
///     Structural analysis for SCDA bytecode using the same decompiler walk that drives
///     endian conversion. This intentionally does not reinterpret or rewrite bytecode; it
///     reports how much of the stream the script model can walk and which multi-byte fields
///     are known.
/// </summary>
public static class ScriptBytecodeAnalyzer
{
    /// <summary>
    ///     Walks the SCDA bytecode (without rewriting it) and reports how much of the stream
    ///     was parsed, how many multi-byte fields it found, and any decode diagnostics.
    /// </summary>
    public static ScriptBytecodeAnalysis Analyze(
        byte[] bytecode,
        bool isBigEndian,
        IReadOnlyList<ScriptVariableInfo>? variables = null,
        IReadOnlyList<uint>? referencedObjects = null,
        string? scriptName = null)
    {
        var walk = Walk(bytecode, isBigEndian, variables, referencedObjects, scriptName);
        var diagnosticLines = ExtractDiagnostics(walk.DecompiledText);
        return new ScriptBytecodeAnalysis(
            bytecode.Length,
            isBigEndian,
            walk.FinalPosition >= bytecode.Length,
            walk.MultiByteReads.Count,
            walk.MultiByteReads.Sum(r => r.Length),
            diagnosticLines.Count > 0,
            string.Join(" | ", diagnosticLines));
    }

    internal static ScriptBytecodeWalk Walk(
        byte[] bytecode,
        bool isBigEndian,
        IReadOnlyList<ScriptVariableInfo>? variables = null,
        IReadOnlyList<uint>? referencedObjects = null,
        string? scriptName = null)
    {
        if (bytecode.Length == 0)
        {
            return new ScriptBytecodeWalk([], [], [], [], 0, false, string.Empty);
        }

        var reader = new BytecodeReader(bytecode, isBigEndian);
        reader.StartTrackingMultiByteReads();
        reader.StartTrackingExternalVariableReads();
        reader.StartTrackingLocalVariableReads();

        var vars = new List<ScriptVariableInfo>(variables ?? []);
        var refs = new List<uint>(referencedObjects ?? []);
        var decompiler = new ScriptDecompiler(vars, refs, _ => null, isBigEndian, scriptName);

        var decompiledText = decompiler.Decompile(bytecode, externalReader: reader);
        var regions = reader.StopTrackingMultiByteReads();
        var externalVariableReads = reader.StopTrackingExternalVariableReads();
        var localVariableReads = reader.StopTrackingLocalVariableReads();
        return new ScriptBytecodeWalk(
            regions,
            externalVariableReads,
            localVariableReads,
            reader.StructuralIssues.ToArray(),
            reader.Position,
            reader.HasStructuralUncertainty,
            decompiledText);
    }

    /// <summary>
    ///     Verifies that every local-variable operand found by the canonical structural
    ///     bytecode walk resolves to an exact SLSD entry. LastVariableId is deliberately
    ///     not consulted: the engine treats it as a high-water mark, not a dense count.
    /// </summary>
    internal static bool HasCompleteLocalVariableBindings(
        byte[] bytecode,
        bool isBigEndian,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects)
    {
        return AnalyzeEmissionSafety(bytecode, isBigEndian, variables, referencedObjects)
            .IsSafeForEmission;
    }

    /// <summary>
    ///     Explains why an SCDA bundle can or cannot be emitted after endian conversion.
    ///     Payload-walk safety and exact local/SCRV binding are reported independently so
    ///     callers do not mistake an unknown function layout for a missing SLSD entry.
    /// </summary>
    internal static ScriptBytecodeEmissionSafetyAnalysis AnalyzeEmissionSafety(
        byte[] bytecode,
        bool isBigEndian,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> referencedObjects)
    {
        var walk = Walk(bytecode, isBigEndian, variables, referencedObjects);
        var decompilerUncertainties = ExtractStructuralUncertainties(walk.DecompiledText);
        var missingLocalIndices = new HashSet<uint>();
        var storageMismatches = new HashSet<string>(StringComparer.Ordinal);

        foreach (var read in walk.LocalVariableReads)
        {
            var matchingVariable = variables.FirstOrDefault(variable =>
                variable.Index == read.VariableIndex);
            if (matchingVariable == null)
            {
                missingLocalIndices.Add(read.VariableIndex);
                continue;
            }

            if (read.Marker == ScriptOpcodes.MarkerIntLocal && matchingVariable.Type == 0
                || read.Marker == ScriptOpcodes.MarkerFloatLocal && matchingVariable.Type != 0)
            {
                storageMismatches.Add(
                    $"{read.VariableIndex}:marker=0x{read.Marker:X2}:slsd={matchingVariable.Type}");
            }
        }

        // An SCDA reference-slot operand can resolve to SCRV rather than SCRO. SCRV carries
        // a local ID in the reference table, so it is part of the same atomic binding proof.
        var danglingScrvIndices = new HashSet<uint>();
        foreach (var referencedObject in referencedObjects)
        {
            if ((referencedObject & 0x80000000) == 0)
            {
                continue;
            }

            var variableIndex = referencedObject & 0x7FFFFFFF;
            if (!variables.Any(variable => variable.Index == variableIndex))
            {
                danglingScrvIndices.Add(variableIndex);
            }
        }

        var diagnostics = new List<string>();
        if (walk.FinalPosition != bytecode.Length)
        {
            diagnostics.Add($"bytecode-walk-incomplete:position={walk.FinalPosition}:length={bytecode.Length}");
        }

        diagnostics.AddRange(walk.StructuralIssues.Select(issue =>
            FormatStructuralIssue(issue, bytecode)));
        diagnostics.AddRange(decompilerUncertainties.Select(static line =>
            $"bytecode-decompiler-uncertainty:{TruncateDiagnostic(line)}"));
        if (walk.HasStructuralUncertainty && walk.StructuralIssues.Count == 0)
        {
            diagnostics.Add("bytecode-structural-uncertainty:unclassified");
        }

        if (missingLocalIndices.Count > 0)
        {
            diagnostics.Add(
                $"bytecode-local-missing:{string.Join(',', missingLocalIndices.Order())}");
        }

        if (storageMismatches.Count > 0)
        {
            diagnostics.Add(
                $"bytecode-local-storage-mismatch:{string.Join(',', storageMismatches.Order(StringComparer.Ordinal))}");
        }

        if (danglingScrvIndices.Count > 0)
        {
            diagnostics.Add(
                $"bytecode-scrv-missing:{string.Join(',', danglingScrvIndices.Order())}");
        }

        var payloadWalkSafe = walk.FinalPosition == bytecode.Length
                              && !walk.HasStructuralUncertainty
                              && decompilerUncertainties.Count == 0;
        var bindingsComplete = missingLocalIndices.Count == 0
                               && storageMismatches.Count == 0
                               && danglingScrvIndices.Count == 0;
        return new ScriptBytecodeEmissionSafetyAnalysis(
            payloadWalkSafe && bindingsComplete,
            payloadWalkSafe,
            bindingsComplete,
            walk.FinalPosition,
            walk.StructuralIssues,
            decompilerUncertainties,
            missingLocalIndices.Order().ToArray(),
            storageMismatches.Order(StringComparer.Ordinal).ToArray(),
            danglingScrvIndices.Order().ToArray(),
            diagnostics);
    }

    private static List<string> ExtractStructuralUncertainties(string decompiledText)
    {
        if (string.IsNullOrEmpty(decompiledText))
        {
            return [];
        }

        return decompiledText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static line =>
                line.StartsWith(';')
                || line.Contains("<truncated", StringComparison.Ordinal)
                || line.Contains("<unknown", StringComparison.Ordinal)
                || line.Contains("<push:", StringComparison.Ordinal)
                || line.Contains("<empty expression>", StringComparison.Ordinal))
            .Take(5)
            .ToList();
    }

    private static string FormatStructuralIssue(
        ScriptBytecodeStructuralIssue issue,
        byte[] bytecode)
    {
        var consumed = Math.Clamp(issue.ConsumedLength, 0, issue.DeclaredLength);
        var opaqueOffset = issue.PayloadOffset + consumed;
        var opaqueLength = Math.Max(0, issue.DeclaredLength - issue.ConsumedLength);
        var availableLength = Math.Clamp(
            Math.Min(opaqueLength, 16),
            0,
            Math.Max(0, bytecode.Length - opaqueOffset));
        var prefix = availableLength == 0
            ? "none"
            : Convert.ToHexString(bytecode.AsSpan(opaqueOffset, availableLength));
        return $"bytecode-{issue.Kind}:opcode=0x{issue.Opcode:X4}:offset=0x{issue.OpcodeOffset:X}:declared={issue.DeclaredLength}:consumed={issue.ConsumedLength}:remaining={opaqueLength}:prefix={prefix}";
    }

    private static string TruncateDiagnostic(string value) =>
        value.Length <= 160 ? value : value[..160];

    private static List<string> ExtractDiagnostics(string decompiledText)
    {
        if (string.IsNullOrWhiteSpace(decompiledText))
        {
            return [];
        }

        return decompiledText
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line =>
                line.StartsWith("; Truncated", StringComparison.Ordinal)
                || line.StartsWith("; Error", StringComparison.Ordinal)
                || line.StartsWith("; Unknown opcode", StringComparison.Ordinal)
                || line.StartsWith("; Decompilation error", StringComparison.Ordinal))
            .Take(5)
            .ToList();
    }
}

/// <summary>Result of analyzing SCDA bytecode: stream coverage, multi-byte field counts, and diagnostics.</summary>
public sealed record ScriptBytecodeAnalysis(
    int ByteLength,
    bool IsBigEndian,
    bool WalkedToEnd,
    int MultiByteReadCount,
    int MultiByteByteCount,
    bool HasDiagnostics,
    string Diagnostics);

internal sealed record ScriptBytecodeWalk(
    IReadOnlyList<(int Offset, int Length)> MultiByteReads,
    IReadOnlyList<ScriptExternalVariableRead> ExternalVariableReads,
    IReadOnlyList<ScriptLocalVariableRead> LocalVariableReads,
    IReadOnlyList<ScriptBytecodeStructuralIssue> StructuralIssues,
    int FinalPosition,
    bool HasStructuralUncertainty,
    string DecompiledText);

internal sealed record ScriptBytecodeEmissionSafetyAnalysis(
    bool IsSafeForEmission,
    bool PayloadWalkSafe,
    bool LocalBindingsComplete,
    int FinalPosition,
    IReadOnlyList<ScriptBytecodeStructuralIssue> StructuralIssues,
    IReadOnlyList<string> DecompilerUncertainties,
    IReadOnlyList<uint> MissingLocalIndices,
    IReadOnlyList<string> StorageMismatches,
    IReadOnlyList<uint> DanglingScrvIndices,
    IReadOnlyList<string> Diagnostics);
