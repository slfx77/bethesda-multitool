using BethesdaMultitool.Core.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Inspection;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Records;
using BethesdaMultitool.Core.Formats.Esm.Utilities;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

internal sealed record SelectedLandComponent(uint FormId, uint? ParentCellFormId, string Plugin, string SourcePath,
    uint FileLocalFormId, long Offset, LandHeightmap? Heightmap, LandVisualData? VisualData);

/// <summary>Physical child ownership is independent of CELL/WRLD override selection.</summary>
internal static class LoadOrderChildEvidence
{
    internal static IEnumerable<(uint ParentCellFormId, PlacedReference Reference)> ReadPlacementParents(
        RecordCollection records, EsmRecordScanResult? scan)
    {
        if (scan == null) { yield break; }
        var groups = new SortedIntervalMap(scan.PlacementGroups.Where(g => g.GroupType is 8 or 9 or 10).ToList());
        foreach (var reference in PlacementQuery.CollectCells(records).SelectMany(c => c.PlacedObjects)
                     .DistinctBy(p => (p.RecordType, p.FormId, p.Offset)))
        {
            var group = groups.FindContainingInterval(reference.Offset);
            if (group >= 0) { yield return (groups.GetLabelAsFormId(group), reference); }
        }
    }

    internal static RecordCollection WithPhysicalPlacements(UnifiedAnalysisResult loaded, CancellationToken cancellationToken)
        => WithPhysicalPlacements(loaded.Records, loaded.RawResult.EsmRecords, cancellationToken);

    internal static RecordCollection WithPhysicalPlacements(RecordCollection records, EsmRecordScanResult? scan,
        CancellationToken cancellationToken = default)
    {
        if (scan == null) { return records; }
        var cells = PlacementQuery.ReadCells(records, scan);
        var cellsById = cells.GroupBy(c => c.FormId).ToDictionary(g => g.Key, g => g.OrderBy(c => c.Offset).ToArray());
        var groups = new SortedIntervalMap(scan.PlacementGroups.Where(g => g.GroupType is 8 or 9 or 10).ToList());
        var typed = PlacementQuery.CollectCells(records).SelectMany(c => c.PlacedObjects)
            .GroupBy(p => (p.RecordType, p.FormId, p.Offset)).ToDictionary(g => g.Key, g => g.First());
        var byParent = new Dictionary<(uint, long), List<PlacedReference>>();
        foreach (var reference in typed.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var group = groups.FindContainingInterval(reference.Offset);
            if (group < 0 || !cellsById.TryGetValue(groups.GetLabelAsFormId(group), out var parents)) { continue; }
            var parent = parents.LastOrDefault(c => c.Offset < reference.Offset);
            if (parent == null) { continue; }
            var key = (parent.FormId, parent.Offset);
            if (!byParent.TryGetValue(key, out var list)) { byParent[key] = list = []; }
            list.Add(reference);
        }
        return records with
        {
            Cells = cells.Select(cell => cell with
            {
                PlacedObjects = byParent.GetValueOrDefault((cell.FormId, cell.Offset), [])
            }).ToList(),
            Worldspaces = records.Worldspaces.Select(w => w with { Cells = [] }).ToList()
        };
    }

    internal static IReadOnlyList<SelectedLandComponent> ReadTerrain(UnifiedAnalysisResult loaded,
        PluginLoadOrder order, PluginLoadOrderEntry entry, LoadOrderRecordIndex index)
        => ReadTerrain(loaded.Records, loaded.RawResult.EsmRecords, order, entry, index);

    internal static IReadOnlyList<SelectedLandComponent> ReadTerrain(RecordCollection records, EsmRecordScanResult? scan,
        PluginLoadOrder order, PluginLoadOrderEntry entry, LoadOrderRecordIndex index)
    {
        if (scan is null) { return []; }
        var groups = new SortedIntervalMap(scan.PlacementGroups.Where(g => g.GroupType is 8 or 9 or 10).ToList());
        var selected = new List<SelectedLandComponent>();
        foreach (var land in scan.LandRecords)
        {
            var id = order.Map(entry.Path, land.Header.FormId).LoadOrderFormId;
            if (!index.Records.TryGetValue(id, out var identity) || identity.DeletedByWinner || identity.TypeConflict ||
                identity.HasAmbiguousWinningRecords || !identity.Winner.Plugin.Equals(entry.Name, StringComparison.OrdinalIgnoreCase) ||
                identity.Winner.Offset != land.Header.Offset) { continue; }
            var group = groups.FindContainingInterval(land.Header.Offset);
            uint? parent = group >= 0 ? order.Map(entry.Path, groups.GetLabelAsFormId(group)).LoadOrderFormId : null;
            var visual = land.VisualData is null ? null : RecordCollectionFormIdRebaser.RebaseModel(land.VisualData,
                value => order.Map(entry.Path, value).LoadOrderFormId, records.Game);
            if (visual != null) { visual.SourceParentCellFormId = parent; }
            var height = (land.ParsedHeightmap ?? land.Heightmap) is { } storedHeight ? storedHeight with { } : null;
            if (height != null) { height.SourceParentCellFormId = parent; }
            selected.Add(new SelectedLandComponent(id, parent, entry.Name, entry.Path, land.Header.FormId,
                land.Header.Offset, height, visual));
        }
        return selected;
    }
}
