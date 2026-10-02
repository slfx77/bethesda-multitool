using BethesdaMultitool.Core.Formats.Esm.Inspection;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Semantic.LoadOrder;

/// <summary>Physical query payloads projected through the shared selection boundary.</summary>
internal static class LoadOrderPlacementQuery
{
    internal static IEnumerable<PlacementOccurrence> WinningOccurrences(LoadOrderSelectionView view,
        PluginLoadOrderEntry entry, IEnumerable<PlacementOccurrence> occurrences)
    {
        foreach (var row in occurrences)
        {
            var id = Map(row.FormId);
            if (!view.Index.Records.TryGetValue(id, out var identity) ||
                !identity.Winner.Plugin.Equals(entry.Name, StringComparison.OrdinalIgnoreCase)) continue;
            // Retain every competing physical winner; selection decides whether it can be displayed.
            yield return row with
            {
                FormId = id, BaseFormId = Optional(row.BaseFormId), ParentCellFormId = Optional(row.ParentCellFormId),
                WorldspaceFormId = Optional(row.WorldspaceFormId), EnableParentFormId = Optional(row.EnableParentFormId),
                FileLocalFormId = row.FormId, FileLocalBaseFormId = row.BaseFormId,
                FileLocalParentCellFormId = row.ParentCellFormId, FileLocalWorldspaceFormId = row.WorldspaceFormId,
                FileLocalEnableParentFormId = row.EnableParentFormId
            };
        }
        uint Map(uint id) => view.Order.Map(entry.Path, id).LoadOrderFormId;
        uint? Optional(uint? id) => id.HasValue ? Map(id.Value) : null;
    }

    internal static (IReadOnlyList<PlacementOccurrence> Objects, IReadOnlyList<PlacementOccurrence> Issues) InCell(
        LoadOrderSelectionView view, CellRecord cell, bool includePersistent, string? typeFilter)
    {
        var (_, categories) = ObjectBoundsIndex.BuildCombined(view.Records);
        var rows = PlacementQuery.InCell(view.PhysicalPlacements, cell, includePersistent, typeFilter,
            matchPhysicalCellOffset: false).Select(row =>
        {
            var status = view.Status(view.Index.Records[row.FormId]);
            return row with
            {
                SelectionStatus = status,
                BaseEditorId = row.BaseFormId.HasValue ? view.Resolver.GetEditorId(row.BaseFormId.Value) : null,
                BaseName = row.BaseFormId.HasValue ? view.Resolver.GetBestNameWithRefChain(row.BaseFormId.Value) : null,
                Category = row.Category == "MapMarker" ? row.Category : row.RecordType == "ACHR" ? "Npc" :
                    row.RecordType == "ACRE" ? "Creature" : categories.GetValueOrDefault(row.BaseFormId ?? 0,
                        PlacedObjectCategory.Unknown).ToString()
            };
        }).OrderBy(row => row.FormId).ThenBy(row => row.SourcePath, StringComparer.OrdinalIgnoreCase)
          .ThenBy(row => row.Offset).ToArray();
        return (rows.Where(row => row.SelectionStatus == "included-in-typed-view").ToArray(),
            rows.Where(row => row.SelectionStatus != "included-in-typed-view").ToArray());
    }
}
