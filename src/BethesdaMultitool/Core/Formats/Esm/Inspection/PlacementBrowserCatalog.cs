using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Semantic.LoadOrder;

namespace BethesdaMultitool.Core.Formats.Esm.Inspection;

/// <summary>A retained scan and a deferred, exact-occurrence reader. Listing never reads payloads.</summary>
internal sealed record PlacementBrowserSource(string FilePath, AnalysisFileType FileType, EsmRecordScanResult Scan,
    Func<uint, long, IReadOnlyList<PlacementOccurrence>> Read);

internal sealed class PlacementBrowserEntry
{
    private readonly Lazy<PlacementOccurrence?> _detail;

    internal PlacementBrowserEntry(PlacementBrowserSource source, DetectedMainRecord header, string? editorId,
        uint formId, string selectionStatus, PluginLoadOrder? order, FormIdResolver? resolver)
    {
        SourcePath = Path.GetFullPath(source.FilePath);
        RecordType = header.RecordType;
        FileLocalFormId = header.FormId;
        FormId = formId;
        Offset = header.Offset;
        EditorId = editorId;
        Flags = header.Flags;
        SelectionStatus = selectionStatus;
        _detail = new(() =>
        {
            var row = source.Read(FileLocalFormId, Offset).SingleOrDefault(r => r.RecordType == RecordType);
            if (row == null || order == null) { return row; }
            uint? Map(uint? id) => id.HasValue ? order.Map(SourcePath, id.Value).LoadOrderFormId : null;
            var baseId = Map(row.BaseFormId);
            return row with
            {
                FormId = FormId, BaseFormId = baseId, ParentCellFormId = Map(row.ParentCellFormId),
                WorldspaceFormId = Map(row.WorldspaceFormId), EnableParentFormId = Map(row.EnableParentFormId),
                BaseEditorId = baseId.HasValue ? resolver?.GetEditorId(baseId.Value) : null,
                BaseName = baseId.HasValue ? resolver?.GetBestNameWithRefChain(baseId.Value) : null
            };
        });
    }

    public string SourcePath { get; }
    public string RecordType { get; }
    public uint FileLocalFormId { get; }
    public uint FormId { get; }
    public long Offset { get; }
    public string? EditorId { get; }
    public uint Flags { get; }
    public string SelectionStatus { get; }
    internal PlacementOccurrence? Read() => _detail.Value;
}

internal static class PlacementBrowserCatalog
{
    internal static PlacementBrowserSource FromSnapshot(string path, AnalysisFileType fileType,
        AnalysisResult analysis, RecordCollection records, FormIdResolver resolver)
    {
        var fullPath = Path.GetFullPath(path);
        var lastWrite = File.GetLastWriteTimeUtc(fullPath);
        return new(fullPath, fileType, analysis.EsmRecords!, (id, offset) =>
        {
            if (new FileInfo(fullPath).Length != analysis.FileSize || File.GetLastWriteTimeUtc(fullPath) != lastWrite)
            { throw new IOException("Source changed. Reopen it to inspect this occurrence."); }
            // A fresh read-only mapping avoids retaining GUI/session accessors in lazy tree nodes.
            using var mapping = MemoryMappedFile.CreateFromFile(fullPath, FileMode.Open, null, 0, MemoryMappedFileAccess.Read);
            using var accessor = mapping.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            return PlacementQuery.Read(new PlacementQuerySource(fullPath, fileType, records, resolver, analysis, accessor),
                id, offset: offset);
        });
    }

    internal static IReadOnlyList<PlacementBrowserEntry> Create(IEnumerable<PlacementBrowserSource> sources,
        LoadOrderSelectionView? selected = null)
    {
        var result = new List<PlacementBrowserEntry>();
        foreach (var source in sources)
        {
            var editorIds = source.Scan.EditorIds.OrderBy(e => e.Offset).ToArray();
            var recovered = source.FileType == AnalysisFileType.Minidump ? source.Scan.RefrRecords : [];
            var runtimeNames = recovered.GroupBy(r => (r.Header.RecordType, r.Header.FormId, r.Header.Offset))
                .ToDictionary(g => g.Key, g => g.First().EditorId);
            var headers = source.Scan.MainRecords.Concat(recovered.Select(r => r.Header))
                .Where(h => PlacementQuery.IsPlacement(h.RecordType))
                .DistinctBy(h => (h.RecordType, h.FormId, h.Offset)).OrderBy(h => h.Offset);
            foreach (var header in headers)
            {
                var globalId = selected?.Order.Map(source.FilePath, header.FormId).LoadOrderFormId ?? header.FormId;
                var status = "Physical occurrence";
                if (selected != null)
                {
                    if (!selected.Index.Records.TryGetValue(globalId, out var identity) ||
                        !identity.Winner.FilePath.Equals(Path.GetFullPath(source.FilePath), StringComparison.OrdinalIgnoreCase))
                    { continue; }
                    // All physical copies in the winning plugin survive, including ambiguous, deleted,
                    // or unparsed records. Earlier plugins never fill a selected record's missing fields.
                    status = identity.HasAmbiguousWinningRecords ? "Ambiguous winning occurrences" :
                        identity.TypeConflict ? "Record type conflict" : identity.DeletedByWinner ? "Deleted winner" : "Selected winner";
                }
                var editorId = FindEditorId(editorIds, header) ??
                    runtimeNames.GetValueOrDefault((header.RecordType, header.FormId, header.Offset));
                result.Add(new(source, header, editorId, globalId, status, selected?.Order, selected?.Resolver));
            }
        }
        return result;
    }

    private static string? FindEditorId(EdidRecord[] entries, DetectedMainRecord header)
    {
        var lo = 0;
        var hi = entries.Length;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (entries[mid].Offset < header.Offset) { lo = mid + 1; }
            else { hi = mid; }
        }
        return lo < entries.Length && entries[lo].Offset < header.Offset + header.HeaderSize + header.DataSize
            ? entries[lo].Name : null;
    }
}
