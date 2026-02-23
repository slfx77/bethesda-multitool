using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Cell browser logic: builds cell list items, applies filters, and groups cells
///     for display in the grouped ListView.
/// </summary>
internal static class WorldMapCellBrowser
{
    internal static List<WorldMapControl.CellListItem> BuildCellListItems(
        List<CellRecord> cells, bool groupInteriors, WorldViewData data)
    {
        var items = new List<WorldMapControl.CellListItem>();
        foreach (var cell in cells)
        {
            string group;
            if (cell.IsInterior)
            {
                if (groupInteriors)
                {
                    group = "Interior";
                }
                else
                {
                    var name = cell.EditorId ?? cell.FullName ?? "";
                    group = name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "#";
                }
            }
            else if (cell.WorldspaceFormId is > 0)
            {
                var wsEditorId = data.Resolver.GetEditorId(cell.WorldspaceFormId.Value);
                var wsDisplayName = data.Resolver.GetDisplayName(cell.WorldspaceFormId.Value);

                if (!string.IsNullOrEmpty(wsDisplayName) && !string.IsNullOrEmpty(wsEditorId) &&
                    !string.Equals(wsDisplayName, wsEditorId, StringComparison.OrdinalIgnoreCase))
                {
                    group = $"{wsDisplayName} ({wsEditorId})";
                }
                else
                {
                    group = wsEditorId ?? wsDisplayName ?? $"Worldspace 0x{cell.WorldspaceFormId.Value:X8}";
                }
            }
            else
            {
                group = "Unknown";
            }

            var gridLabel = cell.GridX.HasValue && cell.GridY.HasValue
                ? $"[{cell.GridX.Value},{cell.GridY.Value}]"
                : "";
            var displayName = cell.EditorId ?? cell.FullName ?? $"0x{cell.FormId:X8}";
            var objectCount = $"{cell.PlacedObjects.Count} obj";

            items.Add(new WorldMapControl.CellListItem
            {
                Group = group,
                GridLabel = gridLabel,
                DisplayName = displayName,
                ObjectCount = objectCount,
                Cell = cell
            });
        }

        return items;
    }

    internal static List<WorldMapControl.CellListItem> ApplyFilters(
        List<WorldMapControl.CellListItem> allItems,
        string query, bool hasObjects, bool namedOnly)
    {
        IEnumerable<WorldMapControl.CellListItem> filtered = allItems;

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(i =>
                i.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.GridLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                i.Group.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        if (hasObjects)
        {
            filtered = filtered.Where(i => i.Cell.PlacedObjects.Count > 0);
        }

        if (namedOnly)
        {
            filtered = filtered.Where(i =>
                !string.IsNullOrEmpty(i.Cell.FullName) || !string.IsNullOrEmpty(i.Cell.EditorId));
        }

        return filtered.ToList();
    }

    internal static List<WorldMapControl.CellListGroup> BuildGroupedSource(
        List<WorldMapControl.CellListItem> items)
    {
        var sorted = items
            .OrderBy(i => WorldMapColors.GetGroupSortOrder(i.Group))
            .ThenBy(i => i.Group)
            .ThenBy(i => i.Cell.GridX ?? int.MaxValue)
            .ThenBy(i => i.Cell.GridY ?? int.MaxValue);

        var grouped = sorted.GroupBy(i => i.Group);
        var source = new List<WorldMapControl.CellListGroup>();
        foreach (var group in grouped)
        {
            source.Add(new WorldMapControl.CellListGroup(group.Key, group.ToList()));
        }

        return source;
    }
}
