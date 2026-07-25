using System.Text;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Script;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Script;

public sealed class ScriptSpecialFunctionPayloadTests
{
    [Fact]
    public void ShowMessage_NoFormatArguments_WalksAndSwapsTrailingUInt32()
    {
        var bytecode = BuildFunctionBigEndian(
            0x1059,
            [
                0x00, 0x01, // ordinary parameter count
                0x72, 0x00, 0x03, // Message: r/ref-slot 3
                0x00, 0x00, // format-argument count
                0x00, 0x00, 0x00, 0x01 // captured engine field
            ]);
        uint[] references = [0x10, 0x20, 0x30];

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            bytecode, true, [], references);
        var decompiled = DecompileBigEndian(bytecode, [], references);
        var littleEndian = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
            bytecode, [], references);

        Assert.True(safety.IsSafeForEmission);
        Assert.Empty(safety.Diagnostics);
        Assert.Contains("ShowMessage 0x00000030 1", decompiled, StringComparison.Ordinal);
        Assert.Equal(
            new byte[]
            {
                0x59, 0x10, 0x0B, 0x00,
                0x01, 0x00,
                0x72, 0x03, 0x00,
                0x00, 0x00,
                0x01, 0x00, 0x00, 0x00,
                0xFF, 0xFF, 0x00, 0x00
            },
            littleEndian);
    }

    [Fact]
    public void ShowMessage_DirectFormatArguments_AreTrackedInSourceOrder()
    {
        var bytecode = BuildFunctionBigEndian(
            0x1059,
            [
                0x00, 0x01,
                0x72, 0x00, 0x01,
                0x00, 0x02,
                0x73, 0x00, 0x0A,
                0x73, 0x00, 0x08,
                0x00, 0x00, 0x00, 0x01
            ]);
        var variables = new List<ScriptVariableInfo>
        {
            new(10, "FirstFormat", 1),
            new(8, "SecondFormat", 1)
        };
        uint[] references = [0x1234];

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            bytecode, true, variables, references);
        var decompiled = DecompileBigEndian(bytecode, variables, references);
        var littleEndian = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
            bytecode, variables, references);

        Assert.True(safety.IsSafeForEmission);
        Assert.Equal("ShowMessage 0x00001234 FirstFormat SecondFormat 1", decompiled);
        Assert.Equal(new byte[] { 0x0A, 0x00 }, littleEndian[12..14]);
        Assert.Equal(new byte[] { 0x08, 0x00 }, littleEndian[15..17]);
        Assert.Equal(new byte[] { 0x01, 0x00, 0x00, 0x00 }, littleEndian[17..21]);
    }

    [Fact]
    public void ShowMessage_GlobalFormatArgument_AndSignedDuration_ArePreservedAndSwapped()
    {
        var bytecode = BuildFunctionBigEndian(
            0x1059,
            [
                0x00, 0x01,
                0x72, 0x00, 0x01,
                0x00, 0x01,
                0x47, 0x00, 0x02,
                0xFF, 0xFF, 0xFF, 0xFE
            ]);
        uint[] references = [0x1234, 0x5678];

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            bytecode, true, [], references);
        var decompiled = DecompileBigEndian(bytecode, [], references);
        var littleEndian = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
            bytecode, [], references);

        Assert.True(safety.IsSafeForEmission);
        Assert.Equal("ShowMessage 0x00001234 0x00005678 -2", decompiled);
        Assert.Equal(new byte[] { 0x02, 0x00 }, littleEndian[12..14]);
        Assert.Equal(new byte[] { 0xFE, 0xFF, 0xFF, 0xFF }, littleEndian[14..18]);
    }

    [Fact]
    public void ShowMessage_ExternalFormatArgument_SwapsBothReferenceAndLocalIndices()
    {
        var bytecode = BuildFunctionBigEndian(
            0x1059,
            [
                0x00, 0x01,
                0x72, 0x00, 0x06,
                0x00, 0x01,
                0x72, 0x00, 0x03, 0x73, 0x00, 0x1B,
                0x00, 0x00, 0x00, 0x00
            ]);
        uint[] references = [0x11, 0x22, 0x33, 0x44, 0x55, 0x66];

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            bytecode, true, [], references);
        var decompiled = DecompileBigEndian(bytecode, [], references);
        var littleEndian = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
            bytecode, [], references);

        Assert.True(safety.IsSafeForEmission);
        Assert.Equal("ShowMessage 0x00000066 0x00000033.var27", decompiled);
        Assert.Equal(new byte[] { 0x03, 0x00 }, littleEndian[12..14]);
        Assert.Equal(new byte[] { 0x1B, 0x00 }, littleEndian[15..17]);
    }

    [Fact]
    public void ShowWarning_StringAndRequiredTerminator_WalkAndSwapExactly()
    {
        var text = Encoding.ASCII.GetBytes("Trigger Activated");
        var payload = new List<byte> { 0x00, 0x01, 0x00, (byte)text.Length };
        payload.AddRange(text);
        payload.AddRange([0x00, 0x00]);
        var bytecode = BuildFunctionBigEndian(0x11B9, [.. payload]);

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(bytecode, true, [], []);
        var decompiled = DecompileBigEndian(bytecode, [], []);
        var littleEndian = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(bytecode);

        Assert.True(safety.IsSafeForEmission);
        Assert.Contains("ShowWarning \"Trigger Activated\"", decompiled, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0xB9, 0x11 }, littleEndian[..2]);
        Assert.Equal(new byte[] { 0x17, 0x00 }, littleEndian[2..4]);
        Assert.Equal(new byte[] { 0x01, 0x00 }, littleEndian[4..6]);
        Assert.Equal(new byte[] { 0x11, 0x00 }, littleEndian[6..8]);
        Assert.Equal(new byte[] { 0x00, 0x00 }, littleEndian[25..27]);
    }

    [Fact]
    public void Lock_ExternalVariableArgument_WalksAndSwapsBothIndices()
    {
        var bytecode = BuildFunctionBigEndian(
            0x1072,
            [0x00, 0x01, 0x72, 0x00, 0x02, 0x73, 0x00, 0x3A]);
        uint[] references = [0x111, 0x222];

        var safety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            bytecode, true, [], references);
        var decompiled = DecompileBigEndian(bytecode, [], references);
        var littleEndian = ScriptBytecodeEndianConverter.SwapBigEndianToLittleEndian(
            bytecode, [], references);

        Assert.True(safety.IsSafeForEmission);
        Assert.Contains("Lock 0x00000222.var58", decompiled, StringComparison.Ordinal);
        Assert.Equal(new byte[] { 0x02, 0x00 }, littleEndian[7..9]);
        Assert.Equal(new byte[] { 0x3A, 0x00 }, littleEndian[10..12]);
    }

    [Fact]
    public void MalformedSpecialTails_RemainFailClosed()
    {
        var badShowMessage = BuildFunctionBigEndian(
            0x1059,
            [
                0x00, 0x01,
                0x72, 0x00, 0x01,
                0x00, 0x01,
                0x01, 0x00, 0x01,
                0x00, 0x00, 0x00, 0x00
            ]);
        var tooManyShowMessageArguments = BuildFunctionBigEndian(
            0x1059,
            [
                0x00, 0x01,
                0x72, 0x00, 0x01,
                0x00, 0x0A,
                0x00, 0x00, 0x00, 0x00
            ]);
        var badShowWarning = BuildFunctionBigEndian(
            0x11B9,
            [0x00, 0x01, 0x00, 0x01, (byte)'X', 0x00, 0x01]);

        var showMessageSafety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            badShowMessage, true, [], [0x1234]);
        var showWarningSafety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            badShowWarning, true, [], []);
        var excessiveArgumentsSafety = ScriptBytecodeAnalyzer.AnalyzeEmissionSafety(
            tooManyShowMessageArguments, true, [], [0x1234]);

        Assert.False(showMessageSafety.IsSafeForEmission);
        Assert.Contains(
            showMessageSafety.Diagnostics,
            static diagnostic => diagnostic.Contains("opaque-function-payload", StringComparison.Ordinal));
        Assert.False(showWarningSafety.IsSafeForEmission);
        Assert.Contains(
            showWarningSafety.Diagnostics,
            static diagnostic => diagnostic.Contains("decompiler-uncertainty", StringComparison.Ordinal));
        Assert.False(excessiveArgumentsSafety.IsSafeForEmission);
        Assert.Contains(
            excessiveArgumentsSafety.Diagnostics,
            static diagnostic => diagnostic.Contains("decompiler-uncertainty", StringComparison.Ordinal));
    }

    [Fact]
    public void RuntimeMerger_AcceptsKnownSpecialLayouts_AndRejectsMalformedTail()
    {
        var validShowMessage = BuildFunctionBigEndian(
            0x1059,
            [0x00, 0x01, 0x72, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]);
        var validShowWarning = BuildFunctionBigEndian(
            0x11B9,
            [0x00, 0x01, 0x00, 0x01, (byte)'X', 0x00, 0x00]);
        var validLock = BuildFunctionBigEndian(
            0x1072,
            [0x00, 0x01, 0x72, 0x00, 0x01, 0x73, 0x00, 0x02]);
        var malformed = BuildFunctionBigEndian(
            0x1059,
            [0x00, 0x01, 0x72, 0x00, 0x01, 0x00, 0x01, 0x01, 0x00, 0x01, 0, 0, 0, 0]);

        Assert.NotNull(ScriptRuntimeMerger.CreateScriptFromRuntimeData(
            CompleteRuntime(1, validShowMessage, [(0x10, null)])));
        Assert.NotNull(ScriptRuntimeMerger.CreateScriptFromRuntimeData(
            CompleteRuntime(2, validShowWarning, [])));
        Assert.NotNull(ScriptRuntimeMerger.CreateScriptFromRuntimeData(
            CompleteRuntime(3, validLock, [(0x10, null)])));
        Assert.Null(ScriptRuntimeMerger.CreateScriptFromRuntimeData(
            CompleteRuntime(4, malformed, [(0x10, null)])));
    }

    private static RuntimeScriptData CompleteRuntime(
        uint formId,
        byte[] bytecode,
        List<(uint FormId, string? EditorId)> references)
    {
        return new RuntimeScriptData
        {
            FormId = formId,
            EditorId = $"Special{formId}",
            CompiledData = bytecode,
            DataSize = (uint)bytecode.Length,
            RefObjectCount = (uint)references.Count,
            ReferencedObjects = references,
            IsCompiled = true,
            VariableMetadataComplete = true,
            VariablesComplete = true,
            ReferencedObjectsComplete = true
        };
    }

    private static string DecompileBigEndian(
        byte[] bytecode,
        IReadOnlyList<ScriptVariableInfo> variables,
        IReadOnlyList<uint> references)
    {
        return new ScriptDecompiler([.. variables], [.. references], _ => null, true)
            .Decompile(bytecode);
    }

    private static byte[] BuildFunctionBigEndian(ushort opcode, byte[] payload)
    {
        var bytes = new List<byte>();
        AppendUInt16BigEndian(bytes, opcode);
        AppendUInt16BigEndian(bytes, checked((ushort)payload.Length));
        bytes.AddRange(payload);
        AppendUInt16BigEndian(bytes, 0xFFFF);
        AppendUInt16BigEndian(bytes, 0);
        return [.. bytes];
    }

    private static void AppendUInt16BigEndian(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)(value >> 8));
        bytes.Add((byte)value);
    }
}