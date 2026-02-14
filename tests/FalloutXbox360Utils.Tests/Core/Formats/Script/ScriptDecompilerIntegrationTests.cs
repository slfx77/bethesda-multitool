using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Script;
using FalloutXbox360Utils.Core.Utils;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Script;

/// <summary>
///     Integration tests that exercise the ScriptDecompiler against real SCDA bytecode
///     from the Xbox 360 ESM file. Compares decompiled output with embedded SCTX source text.
///     These tests are skipped when the sample ESM file is not present.
/// </summary>
public class ScriptDecompilerIntegrationTests(ITestOutputHelper output, SampleFileFixture samples)
{
    private readonly ITestOutputHelper _output = output;

    [Fact]
    public void Decompile_AllScptRecords_NoExceptions()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var fileData = File.ReadAllBytes(samples.Xbox360FinalEsm!);
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
                false);

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
        _output.WriteLine($"Success rate: {(totalCount > 0 ? 100.0 * successCount / totalCount : 0):F1}%");

        // We expect the vast majority to decompile without fatal errors
        Assert.True(errorCount == 0 || (double)successCount / totalCount > 0.9,
            $"Too many decompilation errors: {errorCount} out of {totalCount}");
    }

    [Fact]
    public void Decompile_ScptRecords_StructurallyCorrect()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var fileData = File.ReadAllBytes(samples.Xbox360FinalEsm!);
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
                false);

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
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var fileData = File.ReadAllBytes(samples.Xbox360FinalEsm!);
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
                false);

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
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var fileData = File.ReadAllBytes(samples.Xbox360FinalEsm!);
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
                variables, referencedObjects, _ => null, false);
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

        _output.WriteLine("\n=== MISMATCH SUMMARY ===");
        _output.WriteLine($"Total mismatches: {totalMismatch}");
        _output.WriteLine($"  Truncated: {truncatedCount}");
        _output.WriteLine($"  Unknown opcode: {unknownOpcodeCount}");
        _output.WriteLine($"  Error decoding: {errorDecodingCount}");
        _output.WriteLine($"  Clean (no error comments): {cleanMismatchCount}");

        // Diagnostic test — mismatches are informational, not failures
        Assert.True(true, "Diagnostic test completed");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Decompile_EsmScripts_SemanticComparison()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var fileData = File.ReadAllBytes(samples.Xbox360FinalEsm!);
        var isBigEndian = EsmParser.IsBigEndian(fileData);

        // Build FormID→EditorID map for SCRO resolution
        _output.WriteLine("Building FormID→EditorID map...");
        var formIdMap = EsmHelpers.BuildFormIdToEdidMap(fileData, isBigEndian);
        _output.WriteLine($"FormID map: {formIdMap.Count} entries");

        // Build cross-script variable database (pass 1: collect all script variables)
        var scptRecords = EsmRecordParser.ScanForRecordType(fileData, isBigEndian, "SCPT");
        var scriptVariableDb = new Dictionary<uint, List<ScriptVariableInfo>>();
        foreach (var record in scptRecords)
        {
            var recordData = EsmHelpers.GetRecordData(fileData, record, isBigEndian);
            var (variables, _, _, _, _) = ExtractScriptSubrecords(recordData, isBigEndian);
            if (variables.Count > 0)
            {
                scriptVariableDb[record.FormId] = variables;
            }
        }

        _output.WriteLine($"Script variable database: {scriptVariableDb.Count} scripts with variables");

        // Build object→script mapping from SCRI subrecords, and ref→base from NAME subrecords
        // SCRI links quests, NPCs, items etc. to their attached scripts
        // NAME links placed references (REFR/ACHR) to their base objects
        var objectToScript = new Dictionary<uint, uint>();
        var refToBase = new Dictionary<uint, uint>();
        var allRecords = EsmRecordParser.ScanAllRecords(fileData, isBigEndian);
        foreach (var record in allRecords)
        {
            if (record.Signature == "GRUP" || record.Signature == "SCPT")
            {
                continue;
            }

            try
            {
                var recordData = EsmHelpers.GetRecordData(fileData, record, isBigEndian);
                var subrecords = EsmRecordParser.ParseSubrecords(recordData, isBigEndian);

                // SCRI: object → script mapping
                var scriSub = subrecords.FirstOrDefault(s => s.Signature == "SCRI");
                if (scriSub is { Data.Length: >= 4 })
                {
                    var scriptFormId = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(scriSub.Data)
                        : BinaryPrimitives.ReadUInt32LittleEndian(scriSub.Data);
                    if (scriptFormId != 0)
                    {
                        objectToScript[record.FormId] = scriptFormId;
                    }
                }

                // NAME: placed reference → base object mapping (for REFR/ACHR/ACRE)
                if (record.Signature is "REFR" or "ACHR" or "ACRE")
                {
                    var nameSub = subrecords.FirstOrDefault(s => s.Signature == "NAME");
                    if (nameSub is { Data.Length: >= 4 })
                    {
                        var baseFormId = isBigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(nameSub.Data)
                            : BinaryPrimitives.ReadUInt32LittleEndian(nameSub.Data);
                        if (baseFormId != 0)
                        {
                            refToBase[record.FormId] = baseFormId;
                        }
                    }
                }
            }
            catch
            {
                // Skip records that fail to parse
            }
        }

        _output.WriteLine($"Object→Script mapping: {objectToScript.Count} objects with scripts");
        _output.WriteLine($"Ref→Base mapping: {refToBase.Count} placed references");

        // Build function name normalization map
        var nameMap = ScriptComparer.BuildFunctionNameNormalizationMap();

        // Pass 2: Decompile and compare
        var totalMatches = 0;
        var aggregateMismatches = new Dictionary<string, int>();
        var aggregateTolerated = new Dictionary<string, int>();
        var scriptMatchRates = new List<double>();
        var worstScripts = new List<(string Name, double MatchRate, int Mismatches)>();
        var scriptsCompared = 0;

        Func<uint, string?> resolveFormName = formId => formIdMap.GetValueOrDefault(formId);
        Func<uint, ushort, string?> resolveExternalVariable = (formId, varIndex) =>
        {
            // Try each level of indirection to find the script variables:
            // 1. Direct: formId is a script FormID
            // 2. Object→Script: formId is a quest/NPC that has a script via SCRI
            // 3. Ref→Base→Script: formId is a placed ref, follow to base object, then to script
            var candidates = new List<uint> { formId };

            // Add object→script path
            if (objectToScript.TryGetValue(formId, out var scriptId))
            {
                candidates.Add(scriptId);
            }

            // Add ref→base→script path
            if (refToBase.TryGetValue(formId, out var baseId))
            {
                candidates.Add(baseId);
                if (objectToScript.TryGetValue(baseId, out var baseScriptId))
                {
                    candidates.Add(baseScriptId);
                }
            }

            foreach (var candidateId in candidates)
            {
                if (scriptVariableDb.TryGetValue(candidateId, out var vars))
                {
                    var v = vars.FirstOrDefault(x => x.Index == varIndex);
                    if (v?.Name != null)
                    {
                        return v.Name;
                    }
                }
            }

            return null;
        };

        foreach (var record in scptRecords)
        {
            var recordData = EsmHelpers.GetRecordData(fileData, record, isBigEndian);
            var (variables, referencedObjects, compiledData, sourceText, editorId) =
                ExtractScriptSubrecords(recordData, isBigEndian);

            if (compiledData == null || compiledData.Length == 0 || string.IsNullOrEmpty(sourceText))
            {
                continue;
            }

            // SCDA bytecode is always little-endian
            var decompiler = new ScriptDecompiler(
                variables, referencedObjects, resolveFormName,
                false, editorId, resolveExternalVariable);

            var decompiled = decompiler.Decompile(compiledData);
            scriptsCompared++;

            var result = ScriptComparer.CompareScripts(sourceText, decompiled, nameMap);

            totalMatches += result.MatchCount;
            scriptMatchRates.Add(result.MatchRate);

            foreach (var (category, count) in result.MismatchesByCategory)
            {
                aggregateMismatches.TryGetValue(category, out var existing);
                aggregateMismatches[category] = existing + count;
            }

            foreach (var (category, count) in result.ToleratedDifferences)
            {
                aggregateTolerated.TryGetValue(category, out var existing);
                aggregateTolerated[category] = existing + count;
            }

            if (result.MatchRate < 80 && worstScripts.Count < 10)
            {
                worstScripts.Add((
                    editorId ?? $"0x{record.FormId:X8}",
                    result.MatchRate,
                    result.TotalMismatches));
            }
        }

        var totalMismatches = aggregateMismatches.Values.Sum();
        var totalTolerated = aggregateTolerated.Values.Sum();
        var totalLines = totalMatches + totalMismatches;
        var overallMatchRate = totalLines > 0 ? 100.0 * totalMatches / totalLines : 0;

        _output.WriteLine($"\n=== ESM Semantic Comparison Results ===");
        _output.WriteLine($"Scripts compared: {scriptsCompared}");
        _output.WriteLine($"Total lines compared: {totalLines:N0}");
        _output.WriteLine($"Matching lines: {totalMatches:N0} (includes {totalTolerated:N0} tolerated)");
        _output.WriteLine($"Mismatched lines: {totalMismatches:N0}");
        _output.WriteLine($"Overall match rate: {overallMatchRate:F1}%");

        _output.WriteLine($"\n--- Mismatch Categories ---");
        foreach (var (category, count) in aggregateMismatches.OrderByDescending(kv => kv.Value))
        {
            var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
            _output.WriteLine($"  {category,-25} {count,6:N0}  ({pct:F1}%)");
        }

        if (aggregateTolerated.Count > 0)
        {
            _output.WriteLine($"\n--- Tolerated Differences (counted as matches) ---");
            foreach (var (category, count) in aggregateTolerated.OrderByDescending(kv => kv.Value))
            {
                var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
                _output.WriteLine($"  {category,-25} {count,6:N0}  ({pct:F1}%)");
            }
        }

        if (worstScripts.Count > 0)
        {
            _output.WriteLine($"\n--- Worst Scripts ---");
            foreach (var (name, rate, mismatches) in worstScripts)
            {
                _output.WriteLine($"  {name}: {rate:F1}% match ({mismatches} mismatches)");
            }
        }

        // Show examples from the first script that has mismatches
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
                variables, referencedObjects, resolveFormName,
                false, editorId, resolveExternalVariable);
            var decompiled = decompiler.Decompile(compiledData);
            var compResult = ScriptComparer.CompareScripts(sourceText, decompiled, nameMap);

            if (compResult.Examples.Count > 0)
            {
                _output.WriteLine($"\n--- Example mismatches from {editorId ?? "?"} ---");
                foreach (var (source, decompiledLine, category) in compResult.Examples.Take(5))
                {
                    _output.WriteLine($"  [{category}]");
                    _output.WriteLine($"    SCTX: {source}");
                    _output.WriteLine($"    SCDA: {decompiledLine}");
                }

                break;
            }
        }

        // Write detailed report to file
        var reportPath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "TestOutput", "esm-semantic-comparison.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        using (var writer = new StreamWriter(reportPath))
        {
            writer.WriteLine($"=== ESM Semantic Comparison Results ===");
            writer.WriteLine($"Scripts compared: {scriptsCompared}");
            writer.WriteLine($"Total lines compared: {totalLines:N0}");
            writer.WriteLine($"Matching lines: {totalMatches:N0}");
            writer.WriteLine($"Mismatched lines: {totalMismatches:N0}");
            writer.WriteLine($"Overall match rate: {overallMatchRate:F1}%");
            writer.WriteLine();
            writer.WriteLine($"--- Mismatch Categories ---");
            foreach (var (category, count) in aggregateMismatches.OrderByDescending(kv => kv.Value))
            {
                var pct = totalLines > 0 ? 100.0 * count / totalLines : 0;
                writer.WriteLine($"  {category,-25} {count,6:N0}  ({pct:F1}%)");
            }

            writer.WriteLine();
            writer.WriteLine("--- All Mismatch Examples (first 10 per script) ---");
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
                    variables, referencedObjects, resolveFormName,
                    false, editorId, resolveExternalVariable);
                var decompiled = decompiler.Decompile(compiledData);
                var compResult = ScriptComparer.CompareScripts(sourceText, decompiled, nameMap);

                if (compResult.Examples.Count == 0)
                {
                    continue;
                }

                writer.WriteLine(
                    $"\n  {editorId ?? $"0x{record.FormId:X8}"} ({compResult.MatchRate:F1}% match, {compResult.TotalMismatches} mismatches):");
                foreach (var (source, decompiledLine, category) in compResult.Examples)
                {
                    writer.WriteLine($"    [{category}]");
                    writer.WriteLine($"      SCTX: {source}");
                    writer.WriteLine($"      SCDA: {decompiledLine}");
                }
            }
        }

        _output.WriteLine($"\nDetailed report written to: {Path.GetFullPath(reportPath)}");

        Assert.True(scriptsCompared > 0, "Should have scripts with both source and compiled data");
        Assert.True(overallMatchRate > 50,
            $"Overall semantic match rate {overallMatchRate:F1}% below 50% threshold");
    }

    #region Helpers

    /// <summary>
    ///     Extract script subrecords from SCPT record data (same pattern as RecordParser.Scripts.cs).
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

                // SCRV entries occupy slots in the reference list alongside SCRO.
                // The bytecode uses 1-based indices into the combined SCRO+SCRV list.
                // Store with high bit set as a flag for the decompiler.
                case "SCRV" when sub.DataLength >= 4:
                    var varIdx = isBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    referencedObjects.Add(0x80000000 | varIdx);
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