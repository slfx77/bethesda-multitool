using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Integration tests that exercise the ScriptDecompiler against synthetic SCDA bytecode.
///     Validates that decompiled output matches expected SCTX source text structurally and
///     semantically.
/// </summary>
public class ScriptDecompilerIntegrationTests(ITestOutputHelper output)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Decompile_AllSyntheticScripts_NoExceptions()
    {
        var scripts = BuildSyntheticScripts();
        _output.WriteLine($"Testing {scripts.Count} synthetic SCPT records");

        var successCount = 0;
        var errorCount = 0;

        foreach (var script in scripts)
        {
            var decompiler = new ScriptDecompiler(
                script.Variables, script.ReferencedObjects, _ => null);
            var decompiled = decompiler.Decompile(script.CompiledData);

            if (decompiled.Contains("; Decompilation error") || decompiled.Contains("; Error decoding"))
            {
                errorCount++;
                _output.WriteLine($"  ERROR in {script.EditorId}: {ScriptTestHelpers.GetFirstErrorLine(decompiled)}");
            }
            else
            {
                successCount++;
            }
        }

        _output.WriteLine($"\nResults: {successCount} success, {errorCount} errors out of {scripts.Count} total");
        Assert.Equal(0, errorCount);
    }

    [Fact]
    public void Decompile_SyntheticScripts_StructurallyCorrect()
    {
        var scripts = BuildSyntheticScripts();
        var structuralMatches = 0;
        var structuralMismatches = new List<string>();

        foreach (var script in scripts)
        {
            var decompiler = new ScriptDecompiler(
                script.Variables, script.ReferencedObjects, _ => null);
            var decompiled = decompiler.Decompile(script.CompiledData);

            var sourceStructure = ScriptTestHelpers.ExtractStructuralKeywords(script.SourceText);
            var decompiledStructure = ScriptTestHelpers.ExtractStructuralKeywords(decompiled);

            if (ScriptTestHelpers.StructurallyEquivalent(sourceStructure, decompiledStructure))
            {
                structuralMatches++;
            }
            else
            {
                structuralMismatches.Add(script.EditorId);
                _output.WriteLine($"\n--- Structural mismatch: {script.EditorId} ---");
                _output.WriteLine($"Source keywords:     [{string.Join(", ", sourceStructure)}]");
                _output.WriteLine($"Decompiled keywords: [{string.Join(", ", decompiledStructure)}]");
                _output.WriteLine($"Source:\n{script.SourceText}");
                _output.WriteLine($"Decompiled:\n{decompiled}");
            }
        }

        _output.WriteLine($"\nStructural matches: {structuralMatches}/{scripts.Count}");
        Assert.Empty(structuralMismatches);
    }

    [Fact]
    public void Decompile_SyntheticScripts_SemanticComparison()
    {
        var scripts = BuildSyntheticScripts();
        var nameMap = ScriptComparer.BuildFunctionNameNormalizationMap();

        var totalMatches = 0;
        var totalMismatches = 0;
        var scriptsCompared = 0;

        foreach (var script in scripts)
        {
            var decompiler = new ScriptDecompiler(
                script.Variables, script.ReferencedObjects, _ => null);
            var decompiled = decompiler.Decompile(script.CompiledData);

            scriptsCompared++;
            var result = ScriptComparer.CompareScripts(script.SourceText, decompiled, nameMap);
            totalMatches += result.MatchCount;
            totalMismatches += result.TotalMismatches;

            _output.WriteLine($"{script.EditorId}: {result.MatchRate:F1}% match " +
                              $"({result.MatchCount} matches, {result.TotalMismatches} mismatches)");

            if (result.Examples.Count > 0)
            {
                foreach (var ex in result.Examples.Take(3))
                {
                    _output.WriteLine($"  [{ex.Category}] SCTX: {ex.Source} | SCDA: {ex.Decompiled}");
                }
            }
        }

        var totalLines = totalMatches + totalMismatches;
        var overallMatchRate = totalLines > 0 ? 100.0 * totalMatches / totalLines : 0;

        _output.WriteLine($"\nOverall: {scriptsCompared} scripts, {totalLines} lines, " +
                          $"{overallMatchRate:F1}% match rate");

        Assert.True(scriptsCompared > 0, "Should have scripts to compare");
        Assert.True(overallMatchRate > 50,
            $"Overall semantic match rate {overallMatchRate:F1}% below 50% threshold");
    }

    internal sealed record SyntheticScript(
        string EditorId,
        string SourceText,
        byte[] CompiledData,
        List<ScriptVariableInfo> Variables,
        List<uint> ReferencedObjects);

    #region Synthetic Script Data

    /// <summary>
    ///     Build a set of synthetic scripts with hand-crafted LE bytecode and matching source text.
    /// </summary>
    private static List<SyntheticScript> BuildSyntheticScripts()
    {
        return
        [
            BuildSimpleGameModeScript(),
            BuildIfEndIfScript(),
            BuildSetVariableScript(),
            BuildWhileLoopScript(),
            BuildReturnScript()
        ];
    }

    /// <summary>
    ///     Simple: ScriptName, Begin GameMode, End
    /// </summary>
    private static SyntheticScript BuildSimpleGameModeScript()
    {
        // ScriptName(0x001D) paramLen=0
        // Begin(0x0010) paramLen=8: blockType=0(GameMode) + endOffset(4) + paramCount=0(2)
        // End(0x0011) paramLen=0
        var bytecode = new List<byte>();
        AppendOpcodeLE(bytecode, 0x001D, 0); // ScriptName
        AppendBeginGameModeLE(bytecode);
        AppendOpcodeLE(bytecode, 0x0011, 0); // End

        return new SyntheticScript(
            "TestSimpleGameMode",
            "ScriptName TestSimpleGameMode\r\n\r\nBegin GameMode\r\nEnd",
            bytecode.ToArray(),
            [],
            []);
    }

    /// <summary>
    ///     If/EndIf: Begin GameMode, If (1 == 1), EndIf, End
    /// </summary>
    private static SyntheticScript BuildIfEndIfScript()
    {
        var bytecode = new List<byte>();
        AppendOpcodeLE(bytecode, 0x001D, 0); // ScriptName
        AppendBeginGameModeLE(bytecode);

        // If(0x0016) paramLen layout: [jumpOff:2][exprLen:2][expr]
        // Expression used here is a literal 1: [0x20][0x6E][01 00 00 00].
        var expr = BuildIntLiteralExpr(1);
        var ifParams = new List<byte>();
        AppendUInt16LE(ifParams, 0); // jumpOffset (unused for decompilation)
        AppendUInt16LE(ifParams, (ushort)expr.Length); // exprLen
        ifParams.AddRange(expr);
        AppendOpcodeLE(bytecode, 0x0016, (ushort)ifParams.Count, ifParams.ToArray()); // If

        AppendOpcodeLE(bytecode, 0x0019, 0); // EndIf
        AppendOpcodeLE(bytecode, 0x0011, 0); // End

        return new SyntheticScript(
            "TestIfEndIf",
            "ScriptName TestIfEndIf\r\n\r\nBegin GameMode\r\n  If 1\r\n  EndIf\r\nEnd",
            bytecode.ToArray(),
            [],
            []);
    }

    /// <summary>
    ///     Set: Begin GameMode, Set myVar To 42, End
    /// </summary>
    private static SyntheticScript BuildSetVariableScript()
    {
        var bytecode = new List<byte>();
        AppendOpcodeLE(bytecode, 0x001D, 0); // ScriptName
        AppendBeginGameModeLE(bytecode);

        // Set(0x0015): [marker:1][varIdx:2LE][exprLen:2LE][expr]
        // marker = 0x66 (float local), varIdx = 0
        // Expression: int literal 42
        var expr = BuildIntLiteralExpr(42);
        var setParams = new List<byte>();
        setParams.Add(0x66); // MarkerFloatLocal
        AppendUInt16LE(setParams, 0); // variable index 0
        AppendUInt16LE(setParams, (ushort)expr.Length); // expression length
        setParams.AddRange(expr);
        AppendOpcodeLE(bytecode, 0x0015, (ushort)setParams.Count, setParams.ToArray());

        AppendOpcodeLE(bytecode, 0x0011, 0); // End

        return new SyntheticScript(
            "TestSetVariable",
            "ScriptName TestSetVariable\r\n\r\nBegin GameMode\r\n  Set myVar to 42\r\nEnd",
            bytecode.ToArray(),
            [new ScriptVariableInfo(0, "myVar", 0)], // float variable at index 0
            []);
    }

    /// <summary>
    ///     While/EndWhile: Begin GameMode, While (1), EndWhile, End
    /// </summary>
    private static SyntheticScript BuildWhileLoopScript()
    {
        var bytecode = new List<byte>();
        AppendOpcodeLE(bytecode, 0x001D, 0); // ScriptName
        AppendBeginGameModeLE(bytecode);

        // While(0x001A): same format as If: [jumpOff:2][exprLen:2][expr]
        var expr = BuildIntLiteralExpr(1);
        var whileParams = new List<byte>();
        AppendUInt16LE(whileParams, 0); // jumpOffset
        AppendUInt16LE(whileParams, (ushort)expr.Length);
        whileParams.AddRange(expr);
        AppendOpcodeLE(bytecode, 0x001A, (ushort)whileParams.Count, whileParams.ToArray());

        AppendOpcodeLE(bytecode, 0x001B, 0); // EndWhile
        AppendOpcodeLE(bytecode, 0x0011, 0); // End

        return new SyntheticScript(
            "TestWhileLoop",
            "ScriptName TestWhileLoop\r\n\r\nBegin GameMode\r\n  While 1\r\n  EndWhile\r\nEnd",
            bytecode.ToArray(),
            [],
            []);
    }

    /// <summary>
    ///     Return: Begin GameMode, Return, End
    /// </summary>
    private static SyntheticScript BuildReturnScript()
    {
        var bytecode = new List<byte>();
        AppendOpcodeLE(bytecode, 0x001D, 0); // ScriptName
        AppendBeginGameModeLE(bytecode);
        AppendOpcodeLE(bytecode, 0x001E, 0); // Return
        AppendOpcodeLE(bytecode, 0x0011, 0); // End

        return new SyntheticScript(
            "TestReturn",
            "ScriptName TestReturn\r\n\r\nBegin GameMode\r\n  Return\r\nEnd",
            bytecode.ToArray(),
            [],
            []);
    }

    #endregion

    #region Bytecode Helpers

    private static void AppendOpcodeLE(List<byte> buf, ushort opcode, ushort paramLen,
        byte[]? paramData = null)
    {
        AppendUInt16LE(buf, opcode);
        AppendUInt16LE(buf, paramLen);
        if (paramData != null)
        {
            buf.AddRange(paramData);
        }
    }

    private static void AppendBeginGameModeLE(List<byte> buf)
    {
        // Begin: blockType=0(GameMode) + endOffset=0(4B) + paramCount=0(2B) = 8 bytes
        var beginParams = new byte[8];
        // blockType 0 = GameMode (already 0)
        // endOffset 0 (already 0)
        // paramCount 0 (already 0)
        AppendOpcodeLE(buf, 0x0010, 8, beginParams);
    }

    private static byte[] BuildIntLiteralExpr(int value)
    {
        // Expression token: [0x20 push][0x6E int literal][value:4LE]
        var expr = new byte[6];
        expr[0] = 0x20; // ExprPush
        expr[1] = 0x6E; // ExprIntLiteral
        BinaryPrimitives.WriteInt32LittleEndian(expr.AsSpan(2), value);
        return expr;
    }

    private static void AppendUInt16LE(List<byte> buf, ushort value)
    {
        buf.Add((byte)(value & 0xFF));
        buf.Add((byte)(value >> 8));
    }

    #endregion
}