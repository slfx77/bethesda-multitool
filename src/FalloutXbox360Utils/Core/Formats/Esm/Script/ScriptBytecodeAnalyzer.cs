using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Script;

/// <summary>
///     Structural analysis for SCDA bytecode using the same decompiler walk that drives
///     endian conversion. This intentionally does not reinterpret or rewrite bytecode; it
///     reports how much of the stream the script model can walk and which multi-byte fields
///     are known.
/// </summary>
public static class ScriptBytecodeAnalyzer
{
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
            return new ScriptBytecodeWalk([], 0, string.Empty);
        }

        var reader = new BytecodeReader(bytecode, isBigEndian);
        reader.StartTrackingMultiByteReads();

        var vars = new List<ScriptVariableInfo>(variables ?? []);
        var refs = new List<uint>(referencedObjects ?? []);
        var decompiler = new ScriptDecompiler(vars, refs, _ => null, isBigEndian, scriptName);

        var decompiledText = decompiler.Decompile(bytecode, externalReader: reader);
        var regions = reader.StopTrackingMultiByteReads();
        return new ScriptBytecodeWalk(regions, reader.Position, decompiledText);
    }

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
    int FinalPosition,
    string DecompiledText);
