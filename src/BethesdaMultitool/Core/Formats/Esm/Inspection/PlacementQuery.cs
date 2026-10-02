using System.Globalization;
using System.IO.MemoryMappedFiles;
using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Utilities;

namespace BethesdaMultitool.Core.Formats.Esm.Inspection;

/// <summary>Borrowed analysis inputs. The caller owns the accessor's lifetime.</summary>
internal sealed record PlacementQuerySource(string FilePath, AnalysisFileType FileType, RecordCollection Records,
    FormIdResolver Resolver, AnalysisResult RawResult, MemoryMappedViewAccessor? Accessor);

/// <summary>A physical placement occurrence, not a winning FormID or a runtime enable-state prediction.</summary>
internal sealed record PlacementOccurrence
{
    public required string SourcePath { get; init; }
    public required string SourceKind { get; init; }
    public required string RecordType { get; init; }
    public uint FormId { get; init; }
    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
    public uint Flags { get; init; }
    public bool IsPersistent => (Flags & 0x400) != 0;
    public bool IsInitiallyDisabled => (Flags & 0x800) != 0;
    public bool IsDeleted => (Flags & 0x20) != 0;
    public string? EditorId { get; init; }
    public uint? BaseFormId { get; init; }
    public string? BaseEditorId { get; init; }
    public string? BaseName { get; init; }
    public string Category { get; init; } = "Unknown";
    public uint? ParentCellFormId { get; init; }
    public long? ParentCellOffset { get; init; }
    public uint? WorldspaceFormId { get; init; }
    public required string AssignmentSource { get; init; }
    public float? X { get; init; }
    public float? Y { get; init; }
    public float? Z { get; init; }
    public float? RotX { get; init; }
    public float? RotY { get; init; }
    public float? RotZ { get; init; }
    public float? Scale { get; init; }
    public uint? EnableParentFormId { get; init; }
    public byte? EnableParentFlags { get; init; }
    public bool? OppositeEnableParent => EnableParentFlags is byte flags ? (flags & 1) != 0 : null;
    public bool IsPersistentOverlay { get; init; }
    public required string PayloadStatus { get; init; }
    public uint? FileLocalFormId { get; init; }
    public uint? FileLocalBaseFormId { get; init; }
    public uint? FileLocalParentCellFormId { get; init; }
    public uint? FileLocalWorldspaceFormId { get; init; }
    public uint? FileLocalEnableParentFormId { get; init; }
    public string? SelectionStatus { get; init; }
}

/// <summary>Read-only placement inspection shared by CLI consumers; never collapses distinct offsets.</summary>
internal static class PlacementQuery
{
    internal static bool IsPlacement(string signature) => signature is "REFR" or "ACHR" or "ACRE";

    internal static bool TryParseFormId(string value, out uint formId) => uint.TryParse(
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? value[2..] : value,
        NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out formId);

    internal static bool TryParseOffset(string value, out long offset)
    {
        var hex = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
        return long.TryParse(hex ? value[2..] : value, hex ? NumberStyles.AllowHexSpecifier : NumberStyles.None,
            CultureInfo.InvariantCulture, out offset) && offset >= 0;
    }

    internal static List<CellRecord> CollectCells(RecordCollection records) => records.Cells
        .Concat(records.Worldspaces.SelectMany(w => w.Cells))
        .DistinctBy(c => (c.FormId, c.Offset)).OrderBy(c => c.Offset).ThenBy(c => c.FormId).ToList();

    internal static List<CellRecord> ReadCells(UnifiedAnalysisResult source) =>
        ReadCells(source.Records, source.RawResult.EsmRecords);

    internal static List<CellRecord> ReadCells(RecordCollection records, EsmRecordScanResult? scan)
    {
        var cells = CollectCells(records);
        if (scan?.PlacementGroups is not { Count: > 0 } groups) { return cells; }
        var worlds = new SortedIntervalMap(groups.Where(g => g.GroupType == 1).ToList());
        return cells.Select(cell =>
        {
            var index = worlds.FindContainingInterval(cell.Offset);
            return cell with { WorldspaceFormId = index >= 0 ? worlds.GetLabelAsFormId(index) : null };
        }).ToList();
    }

    internal static IReadOnlyList<CellRecord> ResolveCells(IEnumerable<CellRecord> cells, string query,
        long? offset = null)
    {
        var candidates = cells.Where(c => !offset.HasValue || c.Offset == offset.Value).ToList();
        if (TryParseFormId(query, out var id))
        {
            var byId = candidates.Where(c => c.FormId == id).ToList();
            if (byId.Count > 0 || query.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) { return byId; }
        }
        var exact = candidates.Where(c => string.Equals(c.EditorId, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) { return exact; }
        exact = candidates.Where(c => string.Equals(c.FullName, query, StringComparison.OrdinalIgnoreCase)).ToList();
        if (exact.Count > 0) { return exact; }
        return candidates.Where(c => (c.EditorId?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
            (c.FullName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();
    }

    internal static IReadOnlyList<PlacementOccurrence> Read(UnifiedAnalysisResult source, uint? formId = null,
        string? editorId = null, long? offset = null, CancellationToken cancellationToken = default)
        => Read(new PlacementQuerySource(source.FilePath, source.FileType, source.Records, source.Resolver, source.RawResult, source.Accessor),
            formId, editorId, offset, cancellationToken);

    internal static IReadOnlyList<PlacementOccurrence> Read(PlacementQuerySource source, uint? formId = null,
        string? editorId = null, long? offset = null, CancellationToken cancellationToken = default)
    {
        var scan = source.RawResult.EsmRecords;
        if (scan == null) { return []; }
        var editorOffsets = editorId == null ? [] : scan.EditorIds
            .Where(e => string.Equals(e.Name, editorId, StringComparison.OrdinalIgnoreCase)).Select(e => e.Offset).ToArray();
        var headers = scan.MainRecords.Where(h => IsPlacement(h.RecordType) &&
            (!formId.HasValue || h.FormId == formId) && (!offset.HasValue || h.Offset == offset) &&
            (editorId == null || editorOffsets.Any(o => o >= h.Offset && o < h.Offset + h.HeaderSize + h.DataSize))).ToList();
        var recovered = source.FileType == AnalysisFileType.Minidump
            ? scan.RefrRecords.Where(r => IsPlacement(r.Header.RecordType) &&
                (!formId.HasValue || r.Header.FormId == formId) && (!offset.HasValue || r.Header.Offset == offset) &&
                (editorId == null || string.Equals(r.EditorId, editorId, StringComparison.OrdinalIgnoreCase))).ToArray()
            : [];
        if (headers.Count == 0 && recovered.Length == 0) { return []; }
        // Use the central reader (compression and VA-contiguous dump bounds); do not resurrect
        // the enormous global REFR cache that SemanticFileLoader deliberately releases.
        var selectedScan = new EsmRecordScanResult { Game = scan.Game, MainRecords = headers };
        var context = new RecordParserContext(selectedScan, source.RawResult.FormIdMap, source.Accessor,
            source.RawResult.FileSize, source.RawResult.MinidumpInfo);
        var cells = ReadCells(source.Records, scan);
        var childGroups = new SortedIntervalMap(scan.PlacementGroups.Where(g => g.GroupType is 8 or 9 or 10).ToList());
        var worldGroups = new SortedIntervalMap(scan.PlacementGroups.Where(g => g.GroupType == 1).ToList());
        var cellsById = cells.GroupBy(c => c.FormId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Offset).ToArray());
        var (_, categories) = ObjectBoundsIndex.BuildCombined(source.Records);
        var result = new List<PlacementOccurrence>();
        var seen = new HashSet<(string, uint, long)>();
        var buffer = new byte[4096];
        foreach (var header in headers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var key = (header.RecordType, header.FormId, header.Offset);
            if (!seen.Add(key)) { continue; }
            context.PartiallyRecoveredFormIds.Clear();
            context.NonContiguousRecordFormIds.Clear();
            var data = context.ReadRecordData(header, buffer);
            var raw = data is { } payload
                ? EsmWorldExtractor.ExtractRefrFromBuffer(payload.Data, payload.Size, header, source.RawResult.FormIdMap)
                : null;
            // Header-only/deleted or truncated occurrences must remain visible too.
            Add(header, raw, source.FileType == AnalysisFileType.Minidump ? "captured-record" : "plugin-record",
                data is null ? "unreadable" : context.PartiallyRecoveredFormIds.Contains(header.FormId) ||
                    context.NonContiguousRecordFormIds.Contains(header.FormId) ? "partial" : "readable");
        }
        // Runtime-only records have no serialized header in MainRecords. Preserve recovered
        // occurrences separately, including their recovery provenance and nullable parentage.
        if (source.FileType == AnalysisFileType.Minidump)
        {
            foreach (var raw in recovered)
            {
                var h = raw.Header;
                if ((!formId.HasValue || h.FormId == formId) && (!offset.HasValue || h.Offset == offset) &&
                    seen.Add((h.RecordType, h.FormId, h.Offset)))
                {
                    Add(h, raw, "runtime-recovery", "recovered");
                }
            }
        }
        return result.OrderBy(r => r.Offset).ThenBy(r => r.RecordType, StringComparer.Ordinal).ToList();

        void Add(DetectedMainRecord header, ExtractedRefrRecord? raw, string kind, string status)
        {
            if (editorId != null && !string.Equals(raw?.EditorId, editorId, StringComparison.OrdinalIgnoreCase)) { return; }
            uint? cellId = raw?.ParentCellFormId;
            long? cellOffset = null;
            uint? worldId = null;
            var assignment = cellId.HasValue ? "recovered-parent-cell" : "unresolved";
            var groupIndex = childGroups.FindContainingInterval(header.Offset);
            if (groupIndex >= 0)
            {
                cellId = childGroups.GetLabelAsFormId(groupIndex);
                assignment = "physical-cell-child-GRUP";
            }
            if (cellId.HasValue && cellsById.TryGetValue(cellId.Value, out var parents))
            {
                // In a plugin a CELL precedes its child GRUP. For runtime data multiple CELL
                // captures with the same ID are alternatives, not a nearest-offset authority.
                var cell = groupIndex >= 0 ? parents.LastOrDefault(c => c.Offset < header.Offset) :
                    parents.Length == 1 ? parents[0] : null;
                cellOffset = cell?.Offset;
                worldId = cell?.WorldspaceFormId;
                if (cell == null) { assignment += ":cell-occurrence-unresolved"; }
            }
            var worldIndex = worldGroups.FindContainingInterval(header.Offset);
            if (worldIndex >= 0) { worldId = worldGroups.GetLabelAsFormId(worldIndex); }
            result.Add(new PlacementOccurrence
            {
                SourcePath = Path.GetFullPath(source.FilePath), SourceKind = kind, RecordType = header.RecordType,
                FormId = header.FormId, Offset = header.Offset, IsBigEndian = header.IsBigEndian, Flags = header.Flags,
                EditorId = raw?.EditorId, BaseFormId = raw is { BaseFormId: > 0 } ? raw.BaseFormId : null,
                BaseEditorId = raw is null ? null : source.Resolver.GetEditorId(raw.BaseFormId),
                BaseName = raw is null ? null : source.Resolver.GetBestNameWithRefChain(raw.BaseFormId),
                Category = raw?.IsMapMarker == true ? "MapMarker" : header.RecordType == "ACHR" ? "Npc" :
                    header.RecordType == "ACRE" ? "Creature" :
                    categories.GetValueOrDefault(raw?.BaseFormId ?? 0, PlacedObjectCategory.Unknown).ToString(),
                ParentCellFormId = cellId, ParentCellOffset = cellOffset, WorldspaceFormId = worldId,
                AssignmentSource = assignment, X = raw?.Position?.X, Y = raw?.Position?.Y, Z = raw?.Position?.Z,
                RotX = raw?.Position?.RotX, RotY = raw?.Position?.RotY, RotZ = raw?.Position?.RotZ,
                Scale = raw?.Scale, EnableParentFormId = raw?.EnableParentFormId,
                EnableParentFlags = raw?.EnableParentFlags, PayloadStatus = status
            });
        }
    }

    internal static IReadOnlyList<PlacementOccurrence> InCell(IEnumerable<PlacementOccurrence> placements,
        CellRecord cell, bool includePersistent, string? typeFilter, bool matchPhysicalCellOffset = true)
    {
        var width = cell.CellWorldSize > 0 ? cell.CellWorldSize : 4096f;
        var rows = new List<PlacementOccurrence>();
        foreach (var row in placements)
        {
            if (typeFilter != null && !row.RecordType.Equals(typeFilter, StringComparison.OrdinalIgnoreCase)) { continue; }
            if (row.ParentCellFormId == cell.FormId && (!matchPhysicalCellOffset || row.ParentCellOffset == cell.Offset)) { rows.Add(row); continue; }
            if (includePersistent && !cell.IsInterior && row.IsPersistent && cell.GridX.HasValue && cell.GridY.HasValue &&
                cell.WorldspaceFormId is > 0 && row.WorldspaceFormId == cell.WorldspaceFormId &&
                row.X >= (double)cell.GridX.Value * width && row.X < ((double)cell.GridX.Value + 1) * width &&
                row.Y >= (double)cell.GridY.Value * width && row.Y < ((double)cell.GridY.Value + 1) * width)
            {
                rows.Add(row with { IsPersistentOverlay = true });
            }
        }
        return rows;
    }
}
