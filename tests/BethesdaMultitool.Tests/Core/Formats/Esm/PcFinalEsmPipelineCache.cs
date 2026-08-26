using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Coverage;
using BethesdaMultitool.Core.Formats.Esm.Analysis.FileAnalysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;

namespace BethesdaMultitool.Tests.Core.Formats.Esm;

/// <summary>
///     Static cache that runs the full PC final ESM pipeline once and shares the results
///     between all test classes that need them (e.g. EsmWorldspaceAchrIntegrationTests).
///     Eliminates duplicate ~17s parsing + ~5s parsing per test class.
///     <para>
///         <b>Consumers must treat the result as immutable.</b> The cache hands every caller the
///         same object graph — <see cref="PipelineResult.Collection" />,
///         <see cref="PipelineResult.ScanResult" /> and the record lists are shared by reference,
///         with no defensive copy. Mutating any of them corrupts every later consumer in the same
///         process, and because the cache outlives individual test classes the damage surfaces as
///         an unrelated test failing in an order-dependent way. Clone before modifying.
///     </para>
///     <para>
///         Same contract as <c>RealAssetEsmCache</c>; the two differ only in which pipeline they
///         memoize.
///     </para>
/// </summary>
internal static class PcFinalEsmPipelineCache
{
    /// <summary>
    ///     Process-lifetime memo. Guarded by <see cref="CacheLock" /> for publication only — the
    ///     graph it points at is never mutated after <see cref="Build" /> returns.
    /// </summary>
    private static PipelineResult? _cached;

    private static readonly Lock CacheLock = new();

    /// <summary>
    ///     Returns the shared pipeline result, building it on first call. The returned graph is
    ///     shared with every other caller and must not be mutated.
    /// </summary>
    public static PipelineResult GetOrBuild(string filePath)
    {
        lock (CacheLock)
        {
            return _cached ??= Build(filePath);
        }
    }

    private static PipelineResult Build(string filePath)
    {
        var fileData = File.ReadAllBytes(filePath);
        var isBigEndian = EsmParser.IsBigEndian(fileData);
        var (parsedRecords, grupHeaders) = EsmParser.EnumerateRecordsWithGrups(fileData);

        var (cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo, landToCell) =
            EsmFileAnalyzer.BuildAllMaps(parsedRecords, grupHeaders);

        var scanResult = EsmDataExtractor.ConvertToScanResult(
            parsedRecords, isBigEndian, cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo, landToCell);

        EsmDataExtractor.ExtractRefrRecordsFromParsed(scanResult, parsedRecords, isBigEndian);

        using var mmf = MemoryMappedFile.CreateFromFile(filePath, FileMode.Open, null, 0,
            MemoryMappedFileAccess.Read);
        using var accessor = mmf.CreateViewAccessor(0, fileData.Length, MemoryMappedFileAccess.Read);

        EsmWorldExtractor.ExtractLandRecords(accessor, fileData.Length, scanResult);

        // Build FormID correlations from pre-parsed subrecords (same pattern as EsmFileAnalyzer.AnalyzeCore).
        // Without this, RecordParserContext falls back to BuildFormIdToEditorIdMap which is slower.
        var formIdMap = new Dictionary<uint, string>();
        foreach (var record in parsedRecords)
        {
            if (record.Header.FormId == 0 || formIdMap.ContainsKey(record.Header.FormId))
            {
                continue;
            }

            var editorId = record.Subrecords.FirstOrDefault(s => s.Signature == "EDID")?.DataAsString;
            if (!string.IsNullOrEmpty(editorId))
            {
                formIdMap[record.Header.FormId] = editorId;
            }
        }

        var parser = new RecordParser(scanResult, formIdMap,
            accessor, fileData.Length);
        var collection = parser.ParseAll();

        return new PipelineResult(parsedRecords, grupHeaders, scanResult, collection, isBigEndian);
    }

    internal sealed record PipelineResult(
        List<ParsedMainRecord> ParsedRecords,
        List<GrupHeaderInfo> GrupHeaders,
        EsmRecordScanResult ScanResult,
        RecordCollection Collection,
        bool IsBigEndian);
}