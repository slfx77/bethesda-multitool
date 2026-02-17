using System.IO.MemoryMappedFiles;
using FalloutXbox360Utils.Core.Formats.Esm;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

/// <summary>
///     Static cache that runs the full PC final ESM pipeline once and shares the results
///     between all test classes that need them (e.g. EsmWorldspaceAchrIntegrationTests).
///     Eliminates duplicate ~17s reconstruction + ~5s parsing per test class.
/// </summary>
internal static class PcFinalEsmPipelineCache
{
    private static PipelineResult? _cached;
    private static readonly Lock CacheLock = new();

    internal record PipelineResult(
        List<ParsedMainRecord> ParsedRecords,
        List<GrupHeaderInfo> GrupHeaders,
        EsmRecordScanResult ScanResult,
        RecordCollection Collection,
        bool IsBigEndian);

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

        var (cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo) =
            EsmFileAnalyzer.BuildAllMaps(parsedRecords, grupHeaders);

        var scanResult = EsmFileAnalyzer.ConvertToScanResult(
            parsedRecords, isBigEndian, cellToWorldspace, landToWorldspace, cellToRefr, topicToInfo);

        EsmFileAnalyzer.ExtractRefrRecordsFromParsed(scanResult, parsedRecords, isBigEndian);

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

        var reconstructor = new RecordParser(scanResult, formIdCorrelations: formIdMap,
            accessor: accessor, fileSize: fileData.Length);
        var collection = reconstructor.ReconstructAll();

        return new PipelineResult(parsedRecords, grupHeaders, scanResult, collection, isBigEndian);
    }
}
