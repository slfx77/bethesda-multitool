using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

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
                // NAM0/NAM9: world unit coordinates, convert to cell grid
                (minCellX, minCellY) = CellUtils.WorldToCellCoordinates(ws.BoundsMinX.Value, ws.BoundsMinY!.Value);
                (maxCellX, maxCellY) = CellUtils.WorldToCellCoordinates(ws.BoundsMaxX.Value, ws.BoundsMaxY!.Value);
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

        // Sort by area ascending so the smallest (most specific) containing worldspace
        // wins for nested worldspaces (e.g. DLC zones inside the main Wasteland WRLD).
        // Matches the disambiguation order used by BuildWorldspaceBoundsFromCellMaps.
        worldspaceBounds.Sort((a, b) =>
        {
            var areaA = (long)(a.MaxCellX - a.MinCellX) * (a.MaxCellY - a.MinCellY);
            var areaB = (long)(b.MaxCellX - b.MinCellX) * (b.MaxCellY - b.MinCellY);
            return areaA.CompareTo(areaB);
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

            // Find matching worldspace (prefer smallest by area = first in sorted list,
            // so a child worldspace wins over its parent when their bounds overlap).
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
    ///     Links parsed cells to their parent worldspace's Cells list
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
    ///     Interior cell refs are separated and grouped into interior cell stubs instead of
    ///     being placed on the exterior world map.
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

        // Separate interior refs — these should not appear on the exterior world map.
        // Interior cell refs have local coordinates (often near origin), so they would
        // cluster at map center (0,0) if placed on the exterior map.
        var exteriorOrphans = new List<ExtractedRefrRecord>();
        var interiorOrphans = new List<ExtractedRefrRecord>();
        foreach (var orphan in orphans)
        {
            if (orphan.ParentCellIsInterior == true)
            {
                interiorOrphans.Add(orphan);
            }
            else
            {
                exteriorOrphans.Add(orphan);
            }
        }

        if (interiorOrphans.Count > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: {orphans.Count} orphans ({exteriorOrphans.Count} exterior, {interiorOrphans.Count} interior)");
        }

        var cellByFormId = new Dictionary<uint, CellRecord>();
        foreach (var cell in existingCells)
        {
            cellByFormId.TryAdd(cell.FormId, cell);
        }

        // Phase 0.5: Create stub cells from runtime cell maps for cells not yet parsed.
        // This gives Phase 1 (ParentCellFormId) and Phase 1.5 (grid lookup) real CellRecords
        // to assign orphans to, instead of falling through to virtual cells.
        var stubsCreated = PersistentRefRedistributor.CreateCellMapStubs(existingCells, cellByFormId, context);
        var parentSignalStubsCreated = PersistentRefRedistributor.CreateReferencedCellStubs(orphans, existingCells, cellByFormId, context);

        // Persistent-ref redistribution is hoisted to a separate top-level pass invoked from
        // RecordParser AFTER CreateVirtualCells returns. By then Phase 1 has already moved
        // persistent refs into their owning persistent cell containers via ParentCellFormId,
        // and the redistribution has refs to actually move. Calling it here would run before
        // those refs exist.

        // Phase Interior: Group interior orphans by their parent cell FormID.
        var interiorCellsCreated = PersistentRefRedistributor.AssignInteriorOrphans(interiorOrphans, existingCells, cellByFormId, context);
        if (interiorCellsCreated > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: created {interiorCellsCreated} interior cell stubs for {interiorOrphans.Count} interior refs");
        }

        if (exteriorOrphans.Count == 0)
        {
            if (stubsCreated > 0 || parentSignalStubsCreated > 0 || interiorCellsCreated > 0)
            {
                Logger.Instance.Debug(
                    "  [Semantic] CreateVirtualCells: all orphans resolved (stubs + interior), no virtual cells needed");
            }

            return [];
        }

        // Phase 1: Assign exterior orphans to existing cells using ParentCellFormId,
        // falling back to runtime ExtraPersistentCell when the direct parent-cell signal is absent.
        var reassigned = 0;
        var trueOrphans = new List<ExtractedRefrRecord>();
        foreach (var orphan in exteriorOrphans)
        {
            var assignmentCellFormId = orphan.ParentCellFormId ?? orphan.PersistentCellFormId;
            if (assignmentCellFormId.HasValue &&
                cellByFormId.TryGetValue(assignmentCellFormId.Value, out var parentCell))
            {
                parentCell.PlacedObjects.Add(ToPlacedReference(orphan, context, "ParentCell"));
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
                $"  [Semantic] CreateVirtualCells: {reassigned}/{exteriorOrphans.Count} exterior orphans reassigned via parent/persistent cell");
        }

        if (trueOrphans.Count == 0)
        {
            Logger.Instance.Debug("  [Semantic] CreateVirtualCells: all orphans reassigned, no virtual cells needed");
            return [];
        }

        // Phase 1.5: Use worldspace cell maps to resolve orphans by position -> grid -> real cell.
        // Uses per-worldspace grid maps to avoid cross-worldspace contamination when multiple
        // worldspaces have cells at the same grid coordinates.
        if (context.RuntimeWorldspaceCellMaps is { Count: > 0 })
        {
            // Build per-worldspace grid-to-cell maps
            var gridToCellByWorldspace = new Dictionary<uint, Dictionary<(int, int), uint>>();
            foreach (var (wsFormId, wsData) in context.RuntimeWorldspaceCellMaps)
            {
                var gridMap = new Dictionary<(int, int), uint>();
                foreach (var cellEntry in wsData.Cells)
                {
                    gridMap.TryAdd((cellEntry.GridX, cellEntry.GridY), cellEntry.CellFormId);
                }

                if (gridMap.Count > 0)
                {
                    gridToCellByWorldspace[wsFormId] = gridMap;
                }
            }

            if (gridToCellByWorldspace.Count > 0)
            {
                // Build bounding boxes from actual cell positions for worldspace disambiguation.
                var wsBounds = PersistentRefRedistributor.BuildWorldspaceBoundsFromCellMaps(gridToCellByWorldspace);

                var cellMapResolved = 0;
                var stillOrphans = new List<ExtractedRefrRecord>();
                foreach (var orphan in trueOrphans)
                {
                    var (gx, gy) = CellUtils.WorldToCellCoordinates(orphan.Position!.X, orphan.Position.Y);

                    var resolvedCell = PersistentRefRedistributor.TryResolveOrphanByGrid(
                        gx, gy, gridToCellByWorldspace, wsBounds, cellByFormId, context);
                    if (resolvedCell != null)
                    {
                        resolvedCell.PlacedObjects.Add(ToPlacedReference(orphan, context, "GridMap"));
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

        // Diagnostic: count exterior refs near origin that weren't classified as interior.
        // These may be unloaded persistent refs or interior refs with unknown cell type.
        var nearOriginExterior = trueOrphans.Count(r =>
            MathF.Abs(r.Position!.X) < 100f && MathF.Abs(r.Position.Y) < 100f);
        if (nearOriginExterior > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: {nearOriginExterior} exterior orphans near (0,0) — " +
                "may be unloaded persistent refs or interior refs with unknown cell type");
        }

        // Phase 2: Bin remaining orphans into worldspace-scoped virtual cells, but only when the
        // computed grid is plausible for that worldspace. Refs whose position doesn't fall within
        // any known worldspace's bounds go into per-worldspace (or null) Unresolved buckets so we
        // don't fabricate spurious tile assignments at [0,0] or wherever floor(pos/4096) lands.
        var wsBoundsForPhase2 = context.RuntimeWorldspaceCellMaps is { Count: > 0 }
            ? PersistentRefRedistributor.BuildWorldspaceBoundsFromCellMaps(
                context.RuntimeWorldspaceCellMaps.ToDictionary(
                    kv => kv.Key,
                    kv => kv.Value.Cells.GroupBy(c => (c.GridX, c.GridY))
                        .ToDictionary(g => g.Key, g => g.First().CellFormId)))
            : [];

        var virtualCells = new List<CellRecord>();
        var virtualByKey = new Dictionary<(uint WsFormId, int Gx, int Gy), CellRecord>();
        var unresolvedByWs = new Dictionary<uint, CellRecord>();
        var syntheticFormId = 0xFF000001u;
        var unresolvedFormId = 0xFE100001u;

        foreach (var orphan in trueOrphans)
        {
            var pos = orphan.Position!;
            var (gx, gy) = CellUtils.WorldToCellCoordinates(pos.X, pos.Y);

            // Find a worldspace whose grid bounds contain (gx, gy).
            // wsBounds is sorted by cell count ascending, so smallest (most specific) wins.
            uint? matchedWs = null;
            foreach (var (wsFormId, minGX, minGY, maxGX, maxGY, _) in wsBoundsForPhase2)
            {
                if (gx >= minGX && gx <= maxGX && gy >= minGY && gy <= maxGY)
                {
                    matchedWs = wsFormId;
                    break;
                }
            }

            if (matchedWs is uint wsId)
            {
                var key = (wsId, gx, gy);
                if (!virtualByKey.TryGetValue(key, out var vcell))
                {
                    var wsName = context.GetEditorId(wsId) ?? $"0x{wsId:X8}";
                    vcell = new CellRecord
                    {
                        FormId = syntheticFormId++,
                        EditorId = $"[Virtual {gx},{gy} {wsName}]",
                        GridX = gx,
                        GridY = gy,
                        WorldspaceFormId = wsId,
                        PlacedObjects = [],
                        IsVirtual = true,
                        IsBigEndian = orphan.Header.IsBigEndian
                    };
                    virtualByKey[key] = vcell;
                    virtualCells.Add(vcell);
                }

                vcell.PlacedObjects.Add(ToPlacedReference(orphan, context, "Virtual"));
            }
            else
            {
                // Position doesn't fall within any known worldspace — true unresolved ref.
                var bucketWs = orphan.ParentCellFormId is uint pcid &&
                               cellByFormId.TryGetValue(pcid, out var pcell) &&
                               pcell.WorldspaceFormId is uint pcws
                    ? pcws
                    : 0u;

                if (!unresolvedByWs.TryGetValue(bucketWs, out var ucell))
                {
                    var wsName = bucketWs == 0
                        ? "Unknown"
                        : context.GetEditorId(bucketWs) ?? $"0x{bucketWs:X8}";
                    ucell = new CellRecord
                    {
                        FormId = unresolvedFormId++,
                        EditorId = $"[Unresolved {wsName}]",
                        GridX = null,
                        GridY = null,
                        WorldspaceFormId = bucketWs == 0 ? null : bucketWs,
                        PlacedObjects = [],
                        IsVirtual = true,
                        IsUnresolvedBucket = true,
                        IsBigEndian = orphan.Header.IsBigEndian
                    };
                    unresolvedByWs[bucketWs] = ucell;
                    virtualCells.Add(ucell);
                }

                ucell.PlacedObjects.Add(ToPlacedReference(orphan, context, "Unresolved"));
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] CreateVirtualCells: {trueOrphans.Count} true orphans -> " +
            $"{virtualByKey.Count} bounded virtual cells, {unresolvedByWs.Count} unresolved buckets");

        return virtualCells;
    }

    /// <summary>
    ///     Top-level pass invoked from RecordParser after CreateVirtualCells. Delegates to
    ///     <see cref="PersistentRefRedistributor" />.
    /// </summary>
    internal static int RedistributePersistentRefs(
        List<CellRecord> existingCells,
        RecordParserContext context)
    {
        return PersistentRefRedistributor.RedistributePersistentRefs(existingCells, context);
    }

    internal static PlacedReference ToPlacedReference(
        ExtractedRefrRecord r, RecordParserContext context, string? assignmentSource = null)
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
            Radius = r.Radius,
            Count = r.Count,
            OwnerFormId = r.OwnerFormId,
            EncounterZoneFormId = r.EncounterZoneFormId,
            LockLevel = r.LockLevel,
            LockKeyFormId = r.LockKeyFormId,
            LockFlags = r.LockFlags,
            LockNumTries = r.LockNumTries,
            LockTimesUnlocked = r.LockTimesUnlocked,
            EnableParentFormId = r.EnableParentFormId,
            EnableParentFlags = r.EnableParentFlags,
            PersistentCellFormId = r.PersistentCellFormId,
            StartingPosition = r.StartingPosition,
            StartingWorldOrCellFormId = r.StartingWorldOrCellFormId,
            PackageStartLocation = r.PackageStartLocation,
            MerchantContainerFormId = r.MerchantContainerFormId,
            LeveledCreatureOriginalBaseFormId = r.LeveledCreatureOriginalBaseFormId,
            LeveledCreatureTemplateFormId = r.LeveledCreatureTemplateFormId,
            IsPersistent = r.Header.IsPersistent,
            IsInitiallyDisabled = r.Header.IsInitiallyDisabled,
            DestinationDoorFormId = r.DestinationDoorFormId,
            IsMapMarker = r.IsMapMarker,
            MarkerType = r.MarkerType.HasValue ? (MapMarkerType)r.MarkerType.Value : null,
            MarkerName = r.MarkerName,
            LinkedRefKeywordFormId = r.LinkedRefKeywordFormId,
            LinkedRefFormId = r.LinkedRefFormId,
            LinkedRefChildrenFormIds = r.LinkedRefChildrenFormIds,
            EditorId = r.EditorId,
            Offset = r.Header.Offset,
            IsBigEndian = r.Header.IsBigEndian,
            AssignmentSource = assignmentSource
        };
    }

}
