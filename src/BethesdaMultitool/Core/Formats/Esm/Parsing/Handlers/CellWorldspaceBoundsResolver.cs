using BethesdaMultitool.Core.Formats.Esm.Analysis.Cells;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Worldspace-bounds inference helpers for <see cref="CellLinkageHandler" />: builds cell-grid
///     bounding boxes from worldspace map/bounds data, finds candidate worldspaces for a grid
///     coordinate, and walks adjacent FormID/grid runs used to anchor ambiguous DMP cells.
/// </summary>
internal static class CellWorldspaceBoundsResolver
{
    internal static List<int> BuildAdjacentFormIdComponent(
        uint startFormId,
        List<CellRecord> cells,
        Dictionary<uint, int> indexByFormId,
        HashSet<uint> visited)
    {
        var component = new List<int>();
        var queue = new Queue<uint>();
        queue.Enqueue(startFormId);

        while (queue.Count > 0)
        {
            var formId = queue.Dequeue();
            if (!indexByFormId.TryGetValue(formId, out var index))
            {
                continue;
            }

            component.Add(index);
            foreach (var neighborFormId in new[] { formId - 1, formId + 1 })
            {
                if (!indexByFormId.TryGetValue(neighborFormId, out var neighborIndex))
                {
                    continue;
                }

                var cell = cells[index];
                var neighbor = cells[neighborIndex];
                var manhattan = Math.Abs(cell.GridX!.Value - neighbor.GridX!.Value) +
                                Math.Abs(cell.GridY!.Value - neighbor.GridY!.Value);
                if (manhattan == 1 && visited.Add(neighborFormId))
                {
                    queue.Enqueue(neighborFormId);
                }
            }
        }

        return component;
    }

    internal static bool CanAssignToAnchoredWorldspace(
        CellRecord cell,
        uint anchorWorldspace,
        IReadOnlyList<WorldspaceBounds> bounds)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            return false;
        }

        if (cell.CandidateWorldspaceFormIds.Count > 0)
        {
            return cell.CandidateWorldspaceFormIds.Contains(anchorWorldspace);
        }

        var candidates = FindCandidateWorldspaces(bounds, cell.GridX.Value, cell.GridY.Value);
        return candidates.Count == 0 || candidates.Contains(anchorWorldspace);
    }

    internal static List<WorldspaceBounds> BuildWorldspaceBounds(IEnumerable<WorldspaceRecord> worldspaces)
    {
        var bounds = new List<WorldspaceBounds>();
        foreach (var ws in worldspaces)
        {
            int minCellX, minCellY, maxCellX, maxCellY;

            if (ws.MapNWCellX.HasValue && ws.MapSECellX.HasValue)
            {
                minCellX = Math.Min(ws.MapNWCellX.Value, ws.MapSECellX.Value);
                maxCellX = Math.Max(ws.MapNWCellX.Value, ws.MapSECellX.Value);
                minCellY = Math.Min(ws.MapNWCellY!.Value, ws.MapSECellY!.Value);
                maxCellY = Math.Max(ws.MapNWCellY.Value, ws.MapSECellY.Value);
            }
            else if (ws.BoundsMinX.HasValue && ws.BoundsMaxX.HasValue)
            {
                (minCellX, minCellY) = CellUtils.WorldToCellCoordinates(ws.BoundsMinX.Value, ws.BoundsMinY!.Value);
                (maxCellX, maxCellY) = CellUtils.WorldToCellCoordinates(ws.BoundsMaxX.Value, ws.BoundsMaxY!.Value);
            }
            else
            {
                continue;
            }

            bounds.Add(new WorldspaceBounds(ws.FormId, minCellX, minCellY, maxCellX, maxCellY));
        }

        return bounds;
    }

    internal static List<uint> FindCandidateWorldspaces(
        IReadOnlyList<WorldspaceBounds> bounds,
        int gridX,
        int gridY)
    {
        return bounds
            .Where(b => gridX >= b.MinCellX && gridX <= b.MaxCellX &&
                        gridY >= b.MinCellY && gridY <= b.MaxCellY)
            .Select(b => b.FormId)
            .Distinct()
            .Order()
            .ToList();
    }

    internal readonly record struct WorldspaceBounds(
        uint FormId,
        int MinCellX,
        int MinCellY,
        int MaxCellX,
        int MaxCellY);
}
