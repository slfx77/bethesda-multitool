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
        var stubsCreated = CreateCellMapStubs(existingCells, cellByFormId, context);
        var parentSignalStubsCreated = CreateReferencedCellStubs(orphans, existingCells, cellByFormId, context);

        // Phase 0.75: Redistribute refs sitting on persistent cells to their real exterior tiles
        // by world position. The persistent cell is a logical container — refs it owns carry real
        // worldspace coordinates that belong to specific exterior cells. Without this pass, every
        // persistent ref appears clustered at the persistent stub's (formerly hardcoded) grid.
        var redistributed = RedistributePersistentRefs(existingCells, cellByFormId, context);
        if (redistributed > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: redistributed {redistributed} persistent refs from persistent cells to exterior tiles");
        }

        // Phase Interior: Group interior orphans by their parent cell FormID.
        var interiorCellsCreated = AssignInteriorOrphans(interiorOrphans, existingCells, cellByFormId, context);
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
                var wsBounds = BuildWorldspaceBoundsFromCellMaps(gridToCellByWorldspace);

                var cellMapResolved = 0;
                var stillOrphans = new List<ExtractedRefrRecord>();
                foreach (var orphan in trueOrphans)
                {
                    var (gx, gy) = CellUtils.WorldToCellCoordinates(orphan.Position!.X, orphan.Position.Y);

                    var resolvedCell = TryResolveOrphanByGrid(
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
            ? BuildWorldspaceBoundsFromCellMaps(
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
    ///     Phase 0.75: Walk every persistent CellRecord and move refs with valid world positions
    ///     into the real exterior tile they belong to (per-worldspace grid lookup). Refs keep
    ///     IsPersistent=true and gain OriginCellFormId pointing back at the persistent container,
    ///     so reports can still reconstruct a "persistent only" view. Refs whose grid isn't in
    ///     the worldspace cell map remain on the persistent cell (they have nowhere better to go;
    ///     a later pass could promote them to runtime stubs if desired).
    /// </summary>
    private static int RedistributePersistentRefs(
        List<CellRecord> existingCells,
        Dictionary<uint, CellRecord> cellByFormId,
        RecordParserContext context)
    {
        if (context.RuntimeWorldspaceCellMaps is not { Count: > 0 })
        {
            return 0;
        }

        // Build per-worldspace grid -> cell FormID maps (same shape as Phase 1.5).
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

        if (gridToCellByWorldspace.Count == 0)
        {
            return 0;
        }

        var moved = 0;
        for (var i = 0; i < existingCells.Count; i++)
        {
            var pcell = existingCells[i];
            if (!pcell.IsPersistentCell || pcell.PlacedObjects.Count == 0)
            {
                continue;
            }

            var wsId = pcell.WorldspaceFormId;
            if (wsId is null || !gridToCellByWorldspace.TryGetValue(wsId.Value, out var gridMap))
            {
                continue;
            }

            var keep = new List<PlacedReference>(pcell.PlacedObjects.Count);
            foreach (var pref in pcell.PlacedObjects)
            {
                // Only move refs with non-trivial coordinates. Refs near the origin are
                // either genuine origin-tile refs or coordinate-less container refs;
                // leave them on the persistent cell unless their grid clearly resolves.
                var (gx, gy) = CellUtils.WorldToCellCoordinates(pref.X, pref.Y);

                // Skip refs whose position is essentially zero AND no grid entry exists
                // for (0,0) — those are likely uninitialized positions, not real placements.
                var hasPosition = MathF.Abs(pref.X) > 1f || MathF.Abs(pref.Y) > 1f;
                if (!hasPosition)
                {
                    keep.Add(pref);
                    continue;
                }

                if (gridMap.TryGetValue((gx, gy), out var destCellFormId) &&
                    cellByFormId.TryGetValue(destCellFormId, out var destCell) &&
                    destCell.FormId != pcell.FormId)
                {
                    destCell.PlacedObjects.Add(pref with
                    {
                        OriginCellFormId = pcell.FormId,
                        AssignmentSource = "PersistentRedistributed"
                    });
                    moved++;
                }
                else
                {
                    keep.Add(pref);
                }
            }

            if (keep.Count != pcell.PlacedObjects.Count)
            {
                existingCells[i] = pcell with { PlacedObjects = keep };
                cellByFormId[pcell.FormId] = existingCells[i];
            }
        }

        return moved;
    }

    /// <summary>
    ///     Phase 0.5: Create stub CellRecords from RuntimeWorldspaceCellMaps for cells known
    ///     to the engine but not parsed from ESM fragments. This includes the persistent
    ///     cell and grid cells from pCellMap hash tables.
    /// </summary>
    private static int CreateCellMapStubs(
        List<CellRecord> existingCells,
        Dictionary<uint, CellRecord> cellByFormId,
        RecordParserContext context)
    {
        if (context.RuntimeWorldspaceCellMaps is not { Count: > 0 })
        {
            return 0;
        }

        var stubsCreated = 0;
        foreach (var (wsFormId, wsData) in context.RuntimeWorldspaceCellMaps)
        {
            // Create persistent cell stub if not already present
            if (wsData.PersistentCellFormId is > 0 &&
                !cellByFormId.ContainsKey(wsData.PersistentCellFormId.Value))
            {
                var persistentStub = new CellRecord
                {
                    FormId = wsData.PersistentCellFormId.Value,
                    EditorId = context.GetEditorId(wsData.PersistentCellFormId.Value),
                    FullName = context.FormIdToFullName.GetValueOrDefault(wsData.PersistentCellFormId.Value),
                    GridX = null,
                    GridY = null,
                    WorldspaceFormId = wsFormId,
                    HasPersistentObjects = true,
                    IsPersistentCell = true,
                    PlacedObjects = [],
                    IsBigEndian = true
                };
                cellByFormId[persistentStub.FormId] = persistentStub;
                existingCells.Add(persistentStub);
                stubsCreated++;
            }

            // Create stubs for grid cells from pCellMap
            foreach (var entry in wsData.Cells)
            {
                if (!cellByFormId.ContainsKey(entry.CellFormId))
                {
                    var stub = new CellRecord
                    {
                        FormId = entry.CellFormId,
                        EditorId = context.GetEditorId(entry.CellFormId),
                        FullName = context.FormIdToFullName.GetValueOrDefault(entry.CellFormId),
                        GridX = entry.GridX,
                        GridY = entry.GridY,
                        WorldspaceFormId = entry.WorldspaceFormId ?? wsFormId,
                        PlacedObjects = [],
                        IsBigEndian = true
                    };
                    cellByFormId[stub.FormId] = stub;
                    existingCells.Add(stub);
                    stubsCreated++;
                }
            }
        }

        if (stubsCreated > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: created {stubsCreated} stub cells from runtime cell maps");
        }

        return stubsCreated;
    }

    /// <summary>
    ///     Create real-ID cell stubs for parent/persistent cell FormIDs carried directly on orphan refs.
    ///     This handles DMP cases where the REFR points at a valid runtime CELL but the cell itself
    ///     did not appear in carved ESM fragments or runtime worldspace cell maps.
    /// </summary>
    private static int CreateReferencedCellStubs(
        List<ExtractedRefrRecord> orphans,
        List<CellRecord> existingCells,
        Dictionary<uint, CellRecord> cellByFormId,
        RecordParserContext context)
    {
        if (orphans.Count == 0)
        {
            return 0;
        }

        Dictionary<uint, RuntimeEditorIdEntry>? runtimeCellEntries = null;
        if (context.RuntimeReader != null)
        {
            runtimeCellEntries = new Dictionary<uint, RuntimeEditorIdEntry>();
            foreach (var entry in context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType == 0x39 && entry.FormId != 0)
                {
                    runtimeCellEntries.TryAdd(entry.FormId, entry);
                }
            }
        }

        var stubsCreated = 0;
        foreach (var group in orphans
                     .Select(orphan => (CellFormId: orphan.ParentCellFormId ?? orphan.PersistentCellFormId ?? 0u,
                         Orphan: orphan))
                     .Where(item => item.CellFormId != 0 && !cellByFormId.ContainsKey(item.CellFormId))
                     .GroupBy(item => item.CellFormId))
        {
            var relatedOrphans = group.Select(item => item.Orphan).ToList();
            var derivedStub = BuildReferencedCellStub(group.Key, relatedOrphans, context);
            if (derivedStub == null)
            {
                continue;
            }

            var cell = derivedStub;
            if (context.RuntimeReader != null &&
                runtimeCellEntries != null &&
                runtimeCellEntries.TryGetValue(group.Key, out var runtimeCellEntry))
            {
                var runtimeCell = context.RuntimeReader.ReadRuntimeCell(runtimeCellEntry);
                if (runtimeCell != null)
                {
                    cell = runtimeCell with
                    {
                        EditorId = runtimeCell.EditorId ?? derivedStub.EditorId,
                        FullName = runtimeCell.FullName ?? derivedStub.FullName,
                        GridX = runtimeCell.GridX ?? derivedStub.GridX,
                        GridY = runtimeCell.GridY ?? derivedStub.GridY,
                        WorldspaceFormId = runtimeCell.WorldspaceFormId ?? derivedStub.WorldspaceFormId,
                        Flags = runtimeCell.Flags != 0 ? runtimeCell.Flags : derivedStub.Flags,
                        HasPersistentObjects = runtimeCell.HasPersistentObjects || derivedStub.HasPersistentObjects
                    };
                }
            }

            cellByFormId[cell.FormId] = cell;
            existingCells.Add(cell);
            stubsCreated++;
        }

        if (stubsCreated > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: created {stubsCreated} stub cells from parent/persistent cell signals");
        }

        return stubsCreated;
    }

    /// <summary>
    ///     Group interior orphan refs by ParentCellFormId (or runtime persistent-cell fallback)
    ///     and assign them to interior cell stubs.
    ///     Creates new CellRecord stubs with IsInterior=true for cells not already parsed.
    /// </summary>
    private static int AssignInteriorOrphans(
        List<ExtractedRefrRecord> interiorOrphans,
        List<CellRecord> existingCells,
        Dictionary<uint, CellRecord> cellByFormId,
        RecordParserContext context)
    {
        if (interiorOrphans.Count == 0)
        {
            return 0;
        }

        var cellsCreated = 0;
        var existingCellFormIds = new HashSet<uint>(existingCells.Select(c => c.FormId));

        // Group by parent cell FormID
        foreach (var group in interiorOrphans.GroupBy(r => r.ParentCellFormId ?? r.PersistentCellFormId ?? 0))
        {
            CellRecord cell;
            if (group.Key != 0 && cellByFormId.TryGetValue(group.Key, out var existingCell))
            {
                cell = existingCell;
            }
            else if (group.Key != 0)
            {
                cell = new CellRecord
                {
                    FormId = group.Key,
                    EditorId = context.GetEditorId(group.Key),
                    FullName = context.FormIdToFullName.GetValueOrDefault(group.Key),
                    Flags = 1, // IsInterior
                    PlacedObjects = [],
                    IsBigEndian = true
                };
                cellByFormId[group.Key] = cell;
                existingCells.Add(cell);
                existingCellFormIds.Add(cell.FormId);
                cellsCreated++;
            }
            else
            {
                // Interior orphans with no parent cell — create a single catch-all
                cell = new CellRecord
                {
                    FormId = 0xFE000001,
                    EditorId = "[Unassigned Interior]",
                    Flags = 1, // IsInterior
                    PlacedObjects = [],
                    IsVirtual = true,
                    IsBigEndian = true
                };
                cellByFormId.TryAdd(cell.FormId, cell);
                if (!existingCellFormIds.Contains(cell.FormId))
                {
                    existingCells.Add(cell);
                    existingCellFormIds.Add(cell.FormId);
                    cellsCreated++;
                }

                cell = cellByFormId[cell.FormId];
            }

            foreach (var orphan in group)
            {
                cell.PlacedObjects.Add(ToPlacedReference(orphan, context, "Interior"));
            }
        }

        return cellsCreated;
    }

    private static CellRecord? BuildReferencedCellStub(
        uint cellFormId,
        List<ExtractedRefrRecord> relatedOrphans,
        RecordParserContext context)
    {
        if (cellFormId == 0 || relatedOrphans.Count == 0)
        {
            return null;
        }

        var isInterior = relatedOrphans.Any(orphan => orphan.ParentCellIsInterior == true);
        var hasPersistentObjects = relatedOrphans.Any(orphan =>
            orphan.Header.IsPersistent || orphan.PersistentCellFormId == cellFormId);

        int? gridX = null;
        int? gridY = null;
        if (!isInterior)
        {
            var distinctGrids = relatedOrphans
                .Where(orphan => orphan.Position != null)
                .Select(orphan => CellUtils.WorldToCellCoordinates(orphan.Position!.X, orphan.Position.Y))
                .Distinct()
                .Take(2)
                .ToList();

            if (distinctGrids.Count == 1)
            {
                gridX = distinctGrids[0].cellX;
                gridY = distinctGrids[0].cellY;
            }
        }

        return new CellRecord
        {
            FormId = cellFormId,
            EditorId = context.GetEditorId(cellFormId),
            FullName = context.FormIdToFullName.GetValueOrDefault(cellFormId),
            GridX = gridX,
            GridY = gridY,
            Flags = isInterior ? (byte)0x01 : (byte)0x00,
            HasPersistentObjects = hasPersistentObjects,
            IsBigEndian = true
        };
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

    /// <summary>
    ///     Try to resolve an orphan ref at the given grid position to a real cell,
    ///     disambiguating between worldspaces when multiple claim the same grid coords.
    /// </summary>
    private static CellRecord? TryResolveOrphanByGrid(
        int gx, int gy,
        Dictionary<uint, Dictionary<(int, int), uint>> gridToCellByWorldspace,
        List<(uint WsFormId, int MinGX, int MinGY, int MaxGX, int MaxGY, int CellCount)> wsBounds,
        Dictionary<uint, CellRecord> cellByFormId,
        RecordParserContext context)
    {
        // Collect all worldspaces that have a cell at this grid position
        uint? singleCellFormId = null;
        var multipleMatches = false;

        foreach (var (_, gridMap) in gridToCellByWorldspace)
        {
            if (gridMap.TryGetValue((gx, gy), out var cellFormId))
            {
                if (singleCellFormId == null)
                {
                    singleCellFormId = cellFormId;
                }
                else
                {
                    multipleMatches = true;
                    break;
                }
            }
        }

        if (singleCellFormId == null)
        {
            return null; // No worldspace has a cell at this grid
        }

        if (!multipleMatches)
        {
            // Unambiguous: only one worldspace claims this grid cell
            return cellByFormId.GetValueOrDefault(singleCellFormId.Value);
        }

        // Ambiguous: multiple worldspaces have cells at (gx, gy).
        // Pick the most specific worldspace (smallest area) whose bounds contain the grid position.
        // wsBounds is sorted by CellCount ascending, so the first matching is the most specific.
        Logger.Instance.Debug(
            $"  [Semantic] Phase1.5: grid ({gx},{gy}) claimed by multiple worldspaces, disambiguating by bounds");

        foreach (var (wsFormId, minGX, minGY, maxGX, maxGY, _) in wsBounds)
        {
            if (gx >= minGX && gx <= maxGX && gy >= minGY && gy <= maxGY &&
                gridToCellByWorldspace.TryGetValue(wsFormId, out var gridMap) &&
                gridMap.TryGetValue((gx, gy), out var cellFormId) &&
                cellByFormId.TryGetValue(cellFormId, out var cell))
            {
                var wsName = context.GetEditorId(wsFormId) ?? $"0x{wsFormId:X8}";
                Logger.Instance.Debug(
                    $"  [Semantic] Phase1.5: resolved grid ({gx},{gy}) -> {wsName}");
                return cell;
            }
        }

        // Fallback: just use the first match found
        return cellByFormId.GetValueOrDefault(singleCellFormId.Value);
    }

    /// <summary>
    ///     Build bounding boxes in grid coordinates from actual cell positions in each worldspace.
    ///     Sorted by cell count ascending so the smallest (most specific) worldspace wins ties.
    /// </summary>
    private static List<(uint WsFormId, int MinGX, int MinGY, int MaxGX, int MaxGY, int CellCount)>
        BuildWorldspaceBoundsFromCellMaps(
            Dictionary<uint, Dictionary<(int, int), uint>> gridToCellByWorldspace)
    {
        var result = new List<(uint WsFormId, int MinGX, int MinGY, int MaxGX, int MaxGY, int CellCount)>();
        foreach (var (wsFormId, gridMap) in gridToCellByWorldspace)
        {
            var minGX = int.MaxValue;
            var minGY = int.MaxValue;
            var maxGX = int.MinValue;
            var maxGY = int.MinValue;
            foreach (var (gx, gy) in gridMap.Keys)
            {
                if (gx < minGX) minGX = gx;
                if (gy < minGY) minGY = gy;
                if (gx > maxGX) maxGX = gx;
                if (gy > maxGY) maxGY = gy;
            }

            result.Add((wsFormId, minGX, minGY, maxGX, maxGY, gridMap.Count));
        }

        // Sort by cell count ascending — smaller worldspaces are more specific
        result.Sort((a, b) => a.CellCount.CompareTo(b.CellCount));
        return result;
    }
}
