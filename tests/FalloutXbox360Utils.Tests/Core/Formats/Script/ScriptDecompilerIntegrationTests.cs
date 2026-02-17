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
    private static byte[]? _cachedFileData;
    private static List<AnalyzerRecordInfo>? _cachedScptRecords;
    private static List<DecompResult>? _cachedDecompResults;
    private static readonly Lock CacheLock = new();
    private readonly ITestOutputHelper _output = output;

    private record DecompResult(
        uint FormId,
        string? EditorId,
        string? SourceText,
        string Decompiled);

    private byte[] GetFileData()
    {
        lock (CacheLock)
        {
            return _cachedFileData ??= File.ReadAllBytes(samples.Xbox360FinalEsm!);
        }
    }

    private List<AnalyzerRecordInfo> GetScptRecords(byte[] fileData, bool isBigEndian)
    {
        lock (CacheLock)
        {
            return _cachedScptRecords ??= EsmRecordParser.ScanForRecordType(fileData, isBigEndian, "SCPT");
        }
    }

    /// <summary>
    ///     Single decompilation pass over all SCPT records, cached and shared by all tests.
    ///     Uses null resolver — the decompiler is O(B) per script; we don't need O(N*M) ESM
    ///     scans just for FormID name resolution in a test.
    /// </summary>
    private List<DecompResult> GetDecompResults(byte[] fileData, bool isBigEndian)
    {
        lock (CacheLock)
        {
            if (_cachedDecompResults != null)
            {
                return _cachedDecompResults;
            }

            var scptRecords = GetScptRecords(fileData, isBigEndian);
            _cachedDecompResults = new List<DecompResult>();
            foreach (var record in scptRecords)
            {
                var recordData = EsmHelpers.GetRecordData(fileData, record, isBigEndian);
                var (variables, referencedObjects, compiledData, sourceText, editorId) =
                    ExtractScriptSubrecords(recordData, isBigEndian);

                if (compiledData == null || compiledData.Length == 0)
                {
                    continue;
                }

                var decompiler = new ScriptDecompiler(
                    variables, referencedObjects, _ => null, false);
                var decompiled = decompiler.Decompile(compiledData);
                _cachedDecompResults.Add(new DecompResult(
                    record.FormId, editorId, sourceText, decompiled));
            }

            return _cachedDecompResults;
        }
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Decompile_AllScptRecords_NoExceptions()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var fileData = GetFileData();
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        Assert.True(isBigEndian, "Xbox 360 ESM should be big-endian");

        var results = GetDecompResults(fileData, isBigEndian);
        _output.WriteLine($"Found {results.Count} SCPT records with compiled data");
        Assert.True(results.Count > 0, "Should find at least one SCPT record");

        var successCount = 0;
        var errorCount = 0;

        foreach (var r in results)
        {
            if (r.Decompiled.Contains("; Decompilation error") || r.Decompiled.Contains("; Error decoding"))
            {
                errorCount++;
                if (errorCount <= 5)
                {
                    _output.WriteLine(
                        $"  ERROR in {r.EditorId ?? $"0x{r.FormId:X8}"}: {ScriptTestHelpers.GetFirstErrorLine(r.Decompiled)}");
                }
            }
            else
            {
                successCount++;
            }
        }

        _output.WriteLine($"\nResults: {successCount} success, {errorCount} errors out of {results.Count} total");
        _output.WriteLine($"Success rate: {(results.Count > 0 ? 100.0 * successCount / results.Count : 0):F1}%");

        Assert.True(errorCount == 0 || (double)successCount / results.Count > 0.9,
            $"Too many decompilation errors: {errorCount} out of {results.Count}");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Decompile_ScptRecords_StructurallyCorrect()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var fileData = GetFileData();
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var results = GetDecompResults(fileData, isBigEndian);

        var scriptsWithSource = 0;
        var structuralMatches = 0;
        var structuralMismatches = new List<string>();

        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.SourceText))
            {
                continue;
            }

            scriptsWithSource++;

            var sourceStructure = ScriptTestHelpers.ExtractStructuralKeywords(r.SourceText);
            var decompiledStructure = ScriptTestHelpers.ExtractStructuralKeywords(r.Decompiled);

            if (ScriptTestHelpers.StructurallyEquivalent(sourceStructure, decompiledStructure))
            {
                structuralMatches++;
            }
            else
            {
                structuralMismatches.Add(r.EditorId ?? $"0x{r.FormId:X8}");
                if (structuralMismatches.Count <= 3)
                {
                    _output.WriteLine($"\n--- Structural mismatch: {r.EditorId ?? $"0x{r.FormId:X8}"} ---");
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

        var matchRate = scriptsWithSource > 0 ? 100.0 * structuralMatches / scriptsWithSource : 0;
        _output.WriteLine($"Structural match rate: {matchRate:F1}%");
        Assert.True(matchRate > 90,
            $"Structural match rate {matchRate:F1}% below 90% threshold ({structuralMismatches.Count} mismatches)");
    }

    [Fact]
    [Trait("Category", "Slow")]
    public void Decompile_EsmScripts_SemanticComparison()
    {
        Assert.SkipWhen(samples.Xbox360FinalEsm is null, "Xbox 360 final ESM not available");

        var fileData = GetFileData();
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var results = GetDecompResults(fileData, isBigEndian);

        var nameMap = ScriptComparer.BuildFunctionNameNormalizationMap();

        var totalMatches = 0;
        var aggregateMismatches = new Dictionary<string, int>();
        var aggregateTolerated = new Dictionary<string, int>();
        var worstScripts = new List<(string Name, double MatchRate, int Mismatches)>();
        var scriptsCompared = 0;
        var perScriptResults = new List<(string? EditorId, uint FormId, ScriptComparisonResult Result)>();

        foreach (var r in results)
        {
            if (string.IsNullOrEmpty(r.SourceText))
            {
                continue;
            }

            scriptsCompared++;
            var result = ScriptComparer.CompareScripts(r.SourceText, r.Decompiled, nameMap);
            perScriptResults.Add((r.EditorId, r.FormId, result));

            totalMatches += result.MatchCount;

            foreach (var kvp in result.MismatchesByCategory)
            {
                aggregateMismatches.TryGetValue(kvp.Key, out var existing);
                aggregateMismatches[kvp.Key] = existing + kvp.Value;
            }

            foreach (var kvp in result.ToleratedDifferences)
            {
                aggregateTolerated.TryGetValue(kvp.Key, out var existing);
                aggregateTolerated[kvp.Key] = existing + kvp.Value;
            }

            if (result.MatchRate < 80 && worstScripts.Count < 10)
            {
                worstScripts.Add((
                    r.EditorId ?? $"0x{r.FormId:X8}",
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
        foreach (var kvp in aggregateMismatches.OrderByDescending(kv => kv.Value))
        {
            var pct = totalLines > 0 ? 100.0 * kvp.Value / totalLines : 0;
            _output.WriteLine($"  {kvp.Key,-25} {kvp.Value,6:N0}  ({pct:F1}%)");
        }

        if (aggregateTolerated.Count > 0)
        {
            _output.WriteLine($"\n--- Tolerated Differences (counted as matches) ---");
            foreach (var kvp in aggregateTolerated.OrderByDescending(kv => kv.Value))
            {
                var pct = totalLines > 0 ? 100.0 * kvp.Value / totalLines : 0;
                _output.WriteLine($"  {kvp.Key,-25} {kvp.Value,6:N0}  ({pct:F1}%)");
            }
        }

        if (worstScripts.Count > 0)
        {
            _output.WriteLine($"\n--- Worst Scripts ---");
            foreach (var w in worstScripts)
            {
                _output.WriteLine($"  {w.Name}: {w.MatchRate:F1}% match ({w.Mismatches} mismatches)");
            }
        }

        // Show examples from the first script that has mismatches
        foreach (var entry in perScriptResults)
        {
            if (entry.Result.Examples.Count > 0)
            {
                _output.WriteLine($"\n--- Example mismatches from {entry.EditorId ?? "?"} ---");
                foreach (var example in entry.Result.Examples.Take(5))
                {
                    _output.WriteLine($"  [{example.Category}]");
                    _output.WriteLine($"    SCTX: {example.Source}");
                    _output.WriteLine($"    SCDA: {example.Decompiled}");
                }

                break;
            }
        }

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