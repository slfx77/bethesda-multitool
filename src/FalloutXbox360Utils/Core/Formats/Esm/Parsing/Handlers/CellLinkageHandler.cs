using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Static utility methods for linking cells to worldspaces, inferring worldspace membership,
///     and creating virtual cells for orphan placed references in DMP mode.
/// </summary>
internal static class CellLinkageHandler
{
    /// <summary>
    ///     DMP fallback: infer worldspace membership for exterior cells by testing grid coordinates
    ///     against worldspace bounds (NAM0/NAM9 or MNAM cell ranges). Called when no GRUP hierarchy
    ///     is available to provide exact cell-to-worldspace mapping.
    /// </summary>
    internal static void InferCellWorldspaces(List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        if (worldspaces.Count == 0)
        {
            return;
        }

        // Build bounding boxes in cell grid coordinates from each worldspace's bounds data.
        // Prefer MNAM (explicit cell ranges) over NAM0/NAM9 (world unit coordinates).
        var worldspaceBounds =
            new List<(WorldspaceRecord Ws, int MinCellX, int MinCellY, int MaxCellX, int MaxCellY)>();
        foreach (var ws in worldspaces)
        {
            int minCellX, minCellY, maxCellX, maxCellY;

            if (ws.MapNWCellX.HasValue && ws.MapSECellX.HasValue)
            {
                // MNAM: explicit cell ranges (NW = top-left, SE = bottom-right)
                minCellX = Math.Min(ws.MapNWCellX.Value, ws.MapSECellX.Value);
                maxCellX = Math.Max(ws.MapNWCellX.Value, ws.MapSECellX.Value);
                minCellY = Math.Min(ws.MapNWCellY!.Value, ws.MapSECellY!.Value);
                maxCellY = Math.Max(ws.MapNWCellY.Value, ws.MapSECellY.Value);
            }
            else if (ws.BoundsMinX.HasValue && ws.BoundsMaxX.HasValue)
            {
                // NAM0/NAM9: world unit coordinates, convert to cell grid (4096 units per cell)
                minCellX = (int)MathF.Floor(ws.BoundsMinX.Value / 4096f);
                maxCellX = (int)MathF.Floor(ws.BoundsMaxX.Value / 4096f);
                minCellY = (int)MathF.Floor(ws.BoundsMinY!.Value / 4096f);
                maxCellY = (int)MathF.Floor(ws.BoundsMaxY!.Value / 4096f);
            }
            else
            {
                continue; // No bounds data available
            }

            worldspaceBounds.Add((ws, minCellX, minCellY, maxCellX, maxCellY));
        }

        if (worldspaceBounds.Count == 0)
        {
            // No worldspaces have bounds data; assign all exterior cells to the first worldspace
            // as a best-effort fallback.
            var fallback = worldspaces[0];
            for (var i = 0; i < cells.Count; i++)
            {
                var cell = cells[i];
                if (!cell.IsInterior && cell.GridX.HasValue && cell.WorldspaceFormId is null or 0)
                {
                    cells[i] = cell with { WorldspaceFormId = fallback.FormId };
                }
            }

            Logger.Instance.Debug(
                $"  [Semantic] InferCellWorldspaces: no bounds data, assigned all exterior cells to {fallback.EditorId ?? $"0x{fallback.FormId:X8}"}");
            return;
        }

        // Sort by area descending so the largest worldspace is used as tiebreaker
        worldspaceBounds.Sort((a, b) =>
        {
            var areaA = (long)(a.MaxCellX - a.MinCellX) * (a.MaxCellY - a.MinCellY);
            var areaB = (long)(b.MaxCellX - b.MinCellX) * (b.MaxCellY - b.MinCellY);
            return areaB.CompareTo(areaA);
        });

        var inferredCount = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsInterior || !cell.GridX.HasValue || cell.WorldspaceFormId is > 0)
            {
                continue;
            }

            var gx = cell.GridX.Value;
            var gy = cell.GridY!.Value;

            // Find matching worldspace (prefer largest by area = first in sorted list)
            foreach (var (ws, minX, minY, maxX, maxY) in worldspaceBounds)
            {
                if (gx >= minX && gx <= maxX && gy >= minY && gy <= maxY)
                {
                    cells[i] = cell with { WorldspaceFormId = ws.FormId };
                    inferredCount++;
                    break;
                }
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] InferCellWorldspaces: inferred {inferredCount} cells across {worldspaceBounds.Count} worldspaces");
    }

    /// <summary>
    ///     Links reconstructed cells to their parent worldspace's Cells list
    ///     based on CellRecord.WorldspaceFormId.
    /// </summary>
    internal static void LinkCellsToWorldspaces(List<CellRecord> cells, List<WorldspaceRecord> worldspaces)
    {
        var worldspaceByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < worldspaces.Count; i++)
        {
            worldspaceByFormId.TryAdd(worldspaces[i].FormId, i);
        }

        var cellsByWorldspace = new Dictionary<uint, List<CellRecord>>();
        foreach (var cell in cells)
        {
            if (cell.WorldspaceFormId is > 0 &&
                worldspaceByFormId.ContainsKey(cell.WorldspaceFormId.Value))
            {
                if (!cellsByWorldspace.TryGetValue(cell.WorldspaceFormId.Value, out var list))
                {
                    list = [];
                    cellsByWorldspace[cell.WorldspaceFormId.Value] = list;
                }

                list.Add(cell);
            }
        }

        foreach (var (worldFormId, worldCells) in cellsByWorldspace)
        {
            if (worldspaceByFormId.TryGetValue(worldFormId, out var idx))
            {
                worldspaces[idx] = worldspaces[idx] with { Cells = worldCells };
            }
        }
    }

    /// <summary>
    ///     DMP fallback: create virtual cells for orphan REFR/ACHR/ACRE records that were not
    ///     assigned to any real cell. Groups orphans by their XY-derived grid position and
    ///     creates synthetic CellRecord entries so they appear on the world map.
    /// </summary>
    internal static List<CellRecord> CreateVirtualCells(
        List<CellRecord> existingCells,
        IReadOnlyList<ExtractedRefrRecord> allRefrs,
        RecordParserContext context)
    {
        // Collect FormIDs of all refs already placed in cells
        var placedFormIds = new HashSet<uint>();
        foreach (var cell in existingCells)
        {
            foreach (var obj in cell.PlacedObjects)
            {
                placedFormIds.Add(obj.FormId);
            }
        }

        // Find orphan refs (have a valid position but not in any cell)
        var orphans = allRefrs
            .Where(r => !placedFormIds.Contains(r.Header.FormId) && r.Position != null
                                                                 && (MathF.Abs(r.Position.X) > 1f ||
                                                                     MathF.Abs(r.Position.Y) > 1f))
            .ToList();

        if (orphans.Count == 0)
        {
            return [];
        }

        // Phase 1: Assign orphans to existing cells using ParentCellFormId from runtime struct reader.
        // The proximity heuristic (ResolveCellRefs) misses many refs in DMP mode because ESM fragments
        // are scattered across memory. ParentCellFormId from pParentCell pointer is more reliable.
        var cellByFormId = new Dictionary<uint, CellRecord>();
        foreach (var cell in existingCells)
        {
            cellByFormId.TryAdd(cell.FormId, cell);
        }

        var reassigned = 0;
        var trueOrphans = new List<ExtractedRefrRecord>();
        foreach (var orphan in orphans)
        {
            if (orphan.ParentCellFormId.HasValue &&
                cellByFormId.TryGetValue(orphan.ParentCellFormId.Value, out var parentCell))
            {
                parentCell.PlacedObjects.Add(ToPlacedReference(orphan, context));
                reassigned++;
            }
            else
            {
                trueOrphans.Add(orphan);
            }
        }

        if (reassigned > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: {reassigned}/{orphans.Count} orphans reassigned to existing cells via ParentCellFormId");
        }

        if (trueOrphans.Count == 0)
        {
            Logger.Instance.Debug("  [Semantic] CreateVirtualCells: all orphans reassigned, no virtual cells needed");
            return [];
        }

        // Phase 1.5: Use worldspace cell maps to resolve orphans by position -> grid -> real cell.
        // The cell map from TESWorldSpace.pCellMap gives us the authoritative grid-to-cell mapping.
        if (context.RuntimeWorldspaceCellMaps is { Count: > 0 })
        {
            // Build grid->CellFormId lookup across all worldspaces
            var gridToCellFormId = new Dictionary<(int, int), uint>();
            foreach (var (_, wsData) in context.RuntimeWorldspaceCellMaps)
            {
                foreach (var cellEntry in wsData.Cells)
                {
                    gridToCellFormId.TryAdd((cellEntry.GridX, cellEntry.GridY), cellEntry.CellFormId);
                }
            }

            if (gridToCellFormId.Count > 0)
            {
                var cellMapResolved = 0;
                var stillOrphans = new List<ExtractedRefrRecord>();
                foreach (var orphan in trueOrphans)
                {
                    var gx = (int)MathF.Floor(orphan.Position!.X / 4096f);
                    var gy = (int)MathF.Floor(orphan.Position.Y / 4096f);

                    if (gridToCellFormId.TryGetValue((gx, gy), out var realCellFormId) &&
                        cellByFormId.TryGetValue(realCellFormId, out var realCell))
                    {
                        realCell.PlacedObjects.Add(ToPlacedReference(orphan, context));
                        cellMapResolved++;
                    }
                    else
                    {
                        stillOrphans.Add(orphan);
                    }
                }

                if (cellMapResolved > 0)
                {
                    Logger.Instance.Debug(
                        $"  [Semantic] CreateVirtualCells: {cellMapResolved}/{trueOrphans.Count} orphans resolved via worldspace cell maps");
                }

                trueOrphans = stillOrphans;

                if (trueOrphans.Count == 0)
                {
                    Logger.Instance.Debug(
                        "  [Semantic] CreateVirtualCells: all orphans resolved, no virtual cells needed");
                    return [];
                }
            }
        }

        // Phase 2: Group remaining orphans by grid cell derived from position (4096 world units per cell)
        var groups = trueOrphans
            .GroupBy(r => ((int)MathF.Floor(r.Position!.X / 4096f), (int)MathF.Floor(r.Position.Y / 4096f)))
            .Where(g => g.Any())
            .ToList();

        var virtualCells = new List<CellRecord>();
        var syntheticFormId = 0xFF000001u; // Synthetic FormIDs in a high range unlikely to collide

        foreach (var group in groups)
        {
            var (gridX, gridY) = group.Key;
            var refs = group.Select(r => ToPlacedReference(r, context)).ToList();

            virtualCells.Add(new CellRecord
            {
                FormId = syntheticFormId++,
                EditorId = $"[Virtual {gridX},{gridY}]",
                GridX = gridX,
                GridY = gridY,
                PlacedObjects = refs,
                IsVirtual = true,
                IsBigEndian = refs[0].IsBigEndian
            });
        }

        Logger.Instance.Debug(
            $"  [Semantic] CreateVirtualCells: {trueOrphans.Count} true orphans -> {virtualCells.Count} virtual cells");

        return virtualCells;
    }

    internal static PlacedReference ToPlacedReference(ExtractedRefrRecord r, RecordParserContext context)
    {
        return new PlacedReference
        {
            FormId = r.Header.FormId,
            BaseFormId = r.BaseFormId,
            BaseEditorId = r.BaseEditorId ?? context.GetEditorId(r.BaseFormId),
            RecordType = r.Header.RecordType,
            X = r.Position?.X ?? 0,
            Y = r.Position?.Y ?? 0,
            Z = r.Position?.Z ?? 0,
            RotX = r.Position?.RotX ?? 0,
            RotY = r.Position?.RotY ?? 0,
            RotZ = r.Position?.RotZ ?? 0,
            Scale = r.Scale,
            OwnerFormId = r.OwnerFormId,
            EnableParentFormId = r.EnableParentFormId,
            EnableParentFlags = r.EnableParentFlags,
            IsPersistent = r.Header.IsPersistent,
            IsInitiallyDisabled = r.Header.IsInitiallyDisabled,
            DestinationDoorFormId = r.DestinationDoorFormId,
            IsMapMarker = r.IsMapMarker,
            MarkerType = r.MarkerType.HasValue ? (MapMarkerType)r.MarkerType.Value : null,
            MarkerName = r.MarkerName,
            LinkedRefFormId = r.LinkedRefFormId,
            Offset = r.Header.Offset,
            IsBigEndian = r.Header.IsBigEndian
        };
    }
}
