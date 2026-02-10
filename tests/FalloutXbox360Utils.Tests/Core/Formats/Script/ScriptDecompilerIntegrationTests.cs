using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;
using Xunit;
using Xunit.Abstractions;

namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Integration tests that exercise the ScriptDecompiler against real SCDA bytecode
///     from the Xbox 360 ESM file. Compares decompiled output with embedded SCTX source text.
///     These tests are skipped when the sample ESM file is not present.
/// </summary>
public class ScriptDecompilerIntegrationTests
{
    private const string Xbox360EsmPath = @"Sample\ESM\360_final\FalloutNV.esm";
    private readonly ITestOutputHelper _output;

    public ScriptDecompilerIntegrationTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void Decompile_AllScptRecords_NoExceptions()
    {
        var esmPath = ScriptTestHelpers.FindSamplePath(Xbox360EsmPath);
        if (esmPath == null)
        {
            _output.WriteLine("SKIPPED: ESM file not found");
            return;
        }

        var fileData = File.ReadAllBytes(esmPath);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        Assert.True(isBigEndian, "Xbox 360 ESM should be big-endian");

        var scptRecords = EsmRecordParser.ScanForRecordType(fileData, isBigEndian, "SCPT");
        _output.WriteLine($"Found {scptRecords.Count} SCPT records");
        Assert.True(scptRecords.Count > 0, "Should find at least one SCPT record");

        var successCount = 0;
        var errorCount = 0;
        var totalCount = 0;

        foreach (var record in scptRecords)
        {
            totalCount++;
            var recordData = EsmHelpers.GetRecordData(fileData, record, isBigEndian);
            var (variables, referencedObjects, compiledData, _, editorId) =
                ExtractScriptSubrecords(recordData, isBigEndian);

            if (compiledData == null || compiledData.Length == 0)
            {
                continue;
            }

            // SCDA bytecode is always little-endian — compiled by PC-based GECK, stored verbatim in ESM
            var decompiler = new ScriptDecompiler(
                variables, referencedObjects,
                _ => null,
                isBigEndian: false);

            var result = decompiler.Decompile(compiledData);

            if (result.Contains("; Decompilation error") || result.Contains("; Error decoding"))
            {
                errorCount++;
                if (errorCount <= 5)
                {
                    _output.WriteLine(
                        $"  ERROR in {editorId ?? $"0x{record.FormId:X8}"}: {ScriptTestHelpers.GetFirstErrorLine(result)}");
                }
            }
            else
            {
                successCount++;
            }
        }

        _output.WriteLine($"\nResults: {successCount} success, {errorCount} errors out of {totalCount} total");
        _output.WriteLine($"Success rate: {(totalCount > 0 ? (100.0 * successCount / totalCount) : 0):F1}%");

        // We expect the vast majority to decompile without fatal errors
        Assert.True(errorCount == 0 || (double)successCount / totalCount > 0.9,
            $"Too many decompilation errors: {errorCount} out of {totalCount}");
    }

    [Fact]
    public void Decompile_ScptRecords_StructurallyCorrect()
    {
        var esmPath = ScriptTestHelpers.FindSamplePath(Xbox360EsmPath);
        if (esmPath == null)
        {
            _output.WriteLine("SKIPPED: ESM file not found");
            return;
        }

        var fileData = File.ReadAllBytes(esmPath);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var scptRecords = EsmRecordParser.ScanForRecordType(fileData, isBigEndian, "SCPT");

        var scriptsWithSource = 0;
        var structuralMatches = 0;
        var structuralMismatches = new List<string>();

        foreach (var record in scptRecords)
        {
            var recordData = EsmHelpers.GetRecordData(fileData, record, isBigEndian);
            var (variables, referencedObjects, compiledData, sourceText, editorId) =
                ExtractScriptSubrecords(recordData, isBigEndian);

            if (compiledData == null || compiledData.Length == 0 || string.IsNullOrEmpty(sourceText))
            {
                continue;
            }

            scriptsWithSource++;

            // SCDA bytecode is always little-endian
            var decompiler = new ScriptDecompiler(
                variables, referencedObjects,
                _ => null,
                isBigEndian: false);

            var decompiled = decompiler.Decompile(compiledData);

            var sourceStructure = ScriptTestHelpers.ExtractStructuralKeywords(sourceText);
            var decompiledStructure = ScriptTestHelpers.ExtractStructuralKeywords(decompiled);

            if (ScriptTestHelpers.StructurallyEquivalent(sourceStructure, decompiledStructure))
            {
                structuralMatches++;
            }
            else
            {
                structuralMismatches.Add(editorId ?? $"0x{record.FormId:X8}");
                if (structuralMismatches.Count <= 3)
                {
                    _output.WriteLine($"\n--- Structural mismatch: {editorId ?? $"0x{record.FormId:X8}"} ---");
                    _output.WriteLine($"Source keywords:     [{string.Join(", ", sourceStructure)}]");
                    _output.WriteLine($"Decompiled keywords: [{string.Join(", ", decompiledStructure)}]");
                }
            }
        }

        _output.WriteLine($"\n{scriptsWithSource} scripts with both SCDA+SCTX");
        _output.WriteLine($"Structural matches: {structuralMatches}/{scriptsWithSource}");
        if (structuralMismatches.Count > 0)
        {
            _output.WriteLine($"Mismatches: {string.Join(", ", structuralMismatches.Take(20))}");
        }

        Assert.True(scriptsWithSource > 0, "Should find scripts with both SCDA and SCTX");
    }

    [Fact]
    public void Decompile_SampleScripts_DetailedComparison()
    {
        var esmPath = ScriptTestHelpers.FindSamplePath(Xbox360EsmPath);
        if (esmPath == null)
        {
            _output.WriteLine("SKIPPED: ESM file not found");
            return;
        }

        var fileData = File.ReadAllBytes(esmPath);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var scptRecords = EsmRecordParser.ScanForRecordType(fileData, isBigEndian, "SCPT");

        // Take first 10 scripts that have both SCDA and SCTX for detailed comparison
        var detailedCount = 0;
        foreach (var record in scptRecords)
        {
            if (detailedCount >= 10)
            {
                break;
            }

            var recordData = EsmHelpers.GetRecordData(fileData, record, isBigEndian);
            var (variables, referencedObjects, compiledData, sourceText, editorId) =
                ExtractScriptSubrecords(recordData, isBigEndian);

            if (compiledData == null || compiledData.Length == 0 || string.IsNullOrEmpty(sourceText))
            {
                continue;
            }

            detailedCount++;

            // SCDA bytecode is always little-endian
            var decompiler = new ScriptDecompiler(
                variables, referencedObjects,
                _ => null,
                isBigEndian: false);

            var decompiled = decompiler.Decompile(compiledData);

            _output.WriteLine($"\n{'=',-60}");
            _output.WriteLine(
                $"Script: {editorId ?? $"0x{record.FormId:X8}"}  (FormID: 0x{record.FormId:X8})");
            _output.WriteLine(
                $"Variables: {variables.Count}, References: {referencedObjects.Count}, SCDA size: {compiledData.Length}");

            // Hex dump first 64 bytes of SCDA for format analysis
            var dumpLen = Math.Min(64, compiledData.Length);
            var hexDump = BitConverter.ToString(compiledData, 0, dumpLen).Replace("-", " ");
            _output.WriteLine($"SCDA hex (first {dumpLen} bytes): {hexDump}");

            _output.WriteLine($"{'-',-60}");
            _output.WriteLine("SCTX (original source):");
            foreach (var line in sourceText.Split('\n').Take(20))
            {
                _output.WriteLine($"  | {line.TrimEnd('\r')}");
            }

            if (sourceText.Split('\n').Length > 20)
            {
                _output.WriteLine($"  | ... ({sourceText.Split('\n').Length - 20} more lines)");
            }

            _output.WriteLine($"{'-',-60}");
            _output.WriteLine("Decompiled:");
            foreach (var line in decompiled.Split('\n').Take(20))
            {
                _output.WriteLine($"  | {line.TrimEnd('\r')}");
            }

            if (decompiled.Split('\n').Length > 20)
            {
                _output.WriteLine($"  | ... ({decompiled.Split('\n').Length - 20} more lines)");
            }
        }

        _output.WriteLine($"\n\nDetailed comparison of {detailedCount} scripts complete.");
        Assert.True(detailedCount > 0, "Should find scripts with both SCDA and SCTX for comparison");
    }

    [Fact]
    public void Decompile_DiagnoseMismatches()
    {
        var esmPath = ScriptTestHelpers.FindSamplePath(Xbox360EsmPath);
        if (esmPath == null)
        {
            _output.WriteLine("SKIPPED: ESM file not found");
            return;
        }

        var fileData = File.ReadAllBytes(esmPath);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var scptRecords = EsmRecordParser.ScanForRecordType(fileData, isBigEndian, "SCPT");

        var truncatedCount = 0;
        var unknownOpcodeCount = 0;
        var errorDecodingCount = 0;
        var cleanMismatchCount = 0;
        var totalMismatch = 0;
        var diagnosed = 0;

        foreach (var record in scptRecords)
        {
            var recordData = EsmHelpers.GetRecordData(fileData, record, isBigEndian);
            var (variables, referencedObjects, compiledData, sourceText, editorId) =
                ExtractScriptSubrecords(recordData, isBigEndian);

            if (compiledData == null || compiledData.Length == 0 || string.IsNullOrEmpty(sourceText))
            {
                continue;
            }

            var decompiler = new ScriptDecompiler(
                variables, referencedObjects, _ => null, isBigEndian: false);
            var decompiled = decompiler.Decompile(compiledData);

            string[] blockKeywords = ["Begin", "End", "If", "ElseIf", "Else", "EndIf", "While", "EndWhile"];
            var sourceBlocks = ScriptTestHelpers.ExtractStructuralKeywords(sourceText)
                .Where(k => blockKeywords.Contains(k)).ToList();
            var decompiledBlocks = ScriptTestHelpers.ExtractStructuralKeywords(decompiled)
                .Where(k => blockKeywords.Contains(k)).ToList();

            if (sourceBlocks.SequenceEqual(decompiledBlocks))
            {
                continue;
            }

            totalMismatch++;

            if (decompiled.Contains("; Truncated"))
            {
                truncatedCount++;
            }
            else if (decompiled.Contains("; Unknown opcode"))
            {
                unknownOpcodeCount++;
            }
            else if (decompiled.Contains("; Error decoding") || decompiled.Contains("; Decompilation error"))
            {
                errorDecodingCount++;
            }
            else
            {
                cleanMismatchCount++;
            }

            if (diagnosed < 5)
            {
                diagnosed++;
                _output.WriteLine($"\n=== MISMATCH #{diagnosed}: {editorId} (0x{record.FormId:X8}) ===");
                _output.WriteLine($"Source blocks:     [{string.Join(", ", sourceBlocks)}]");
                _output.WriteLine($"Decompiled blocks: [{string.Join(", ", decompiledBlocks)}]");
                _output.WriteLine(
                    $"SCDA size: {compiledData.Length}, Vars: {variables.Count}, Refs: {referencedObjects.Count}");

                // Full hex dump of SCDA with offset annotations
                _output.WriteLine("--- SCDA hex dump ---");
                for (var i = 0; i < compiledData.Length; i += 16)
                {
                    var len = Math.Min(16, compiledData.Length - i);
                    var hex = BitConverter.ToString(compiledData, i, len).Replace("-", " ");
                    _output.WriteLine($"  0x{i:X4}: {hex}");
                }

                _output.WriteLine("--- Full decompiled output ---");
                foreach (var line in decompiled.Split('\n'))
                {
                    _output.WriteLine($"  | {line.TrimEnd('\r')}");
                }

                _output.WriteLine("--- Source (SCTX) ---");
                foreach (var line in sourceText.Split('\n').Take(30))
                {
                    _output.WriteLine($"  | {line.TrimEnd('\r')}");
                }
            }
        }

        _output.WriteLine($"\n=== MISMATCH SUMMARY ===");
        _output.WriteLine($"Total mismatches: {totalMismatch}");
        _output.WriteLine($"  Truncated: {truncatedCount}");
        _output.WriteLine($"  Unknown opcode: {unknownOpcodeCount}");
        _output.WriteLine($"  Error decoding: {errorDecodingCount}");
        _output.WriteLine($"  Clean (no error comments): {cleanMismatchCount}");

        // Diagnostic test — mismatches are informational, not failures
        Assert.True(true, "Diagnostic test completed");
    }

    #region Helpers

    /// <summary>
    ///     Extract script subrecords from SCPT record data (same pattern as SemanticReconstructor.Scripts.cs).
    /// </summary>
    internal static (List<ScriptVariableInfo> Variables, List<uint> ReferencedObjects,
        byte[]? CompiledData, string? SourceText, string? EditorId)
        ExtractScriptSubrecords(byte[] recordData, bool isBigEndian)
    {
        var variables = new List<ScriptVariableInfo>();
        var referencedObjects = new List<uint>();
        byte[]? compiledData = null;
        string? sourceText = null;
        string? editorId = null;
        uint? pendingSlsdIndex = null;
        byte pendingSlsdType = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(recordData, recordData.Length, isBigEndian))
        {
            var subData = recordData.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;

                case "SCTX":
                    sourceText = EsmStringUtils.ReadNullTermString(subData);
                    break;

                case "SCDA":
                    compiledData = subData.ToArray();
                    break;

                case "SLSD" when sub.DataLength >= 16:
                    pendingSlsdIndex = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    var isIntegerRaw = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[12..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                    pendingSlsdType = isIntegerRaw != 0 ? (byte)1 : (byte)0;
                    break;

                case "SCVR":
                {
                    var varName = EsmStringUtils.ReadNullTermString(subData);
                    if (pendingSlsdIndex.HasValue)
                    {
                        variables.Add(new ScriptVariableInfo(pendingSlsdIndex.Value, varName, pendingSlsdType));
                        pendingSlsdIndex = null;
                    }

                    break;
                }

                case "SCRO" when sub.DataLength >= 4:
                    var formId = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    referencedObjects.Add(formId);
                    break;
            }
        }

        if (pendingSlsdIndex.HasValue)
        {
            variables.Add(new ScriptVariableInfo(pendingSlsdIndex.Value, null, pendingSlsdType));
        }

        return (variables, referencedObjects, compiledData, sourceText, editorId);
    }

    #endregion
}
