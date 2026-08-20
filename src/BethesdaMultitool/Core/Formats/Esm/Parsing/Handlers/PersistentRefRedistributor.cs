using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Analysis.Cells;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Persistent-ref redistribution: moves persistent refs with valid world positions into
///     the real exterior tile they belong to. This handles both worldspace persistent-cell
///     containers and persistent refs stored under normal exterior cell child groups. Also
///     provides helpers for creating cell stubs from runtime cell maps and resolving orphan
///     refs by grid position, used by both this class and <see cref="CellLinkageHandler" />.
/// </summary>
internal static class PersistentRefRedistributor
{
    /// <summary>
    ///     Top-level pass invoked from RecordParser after CreateVirtualCells. Moves persistent
    ///     refs with valid world positions into the real exterior tile they belong to. The full
    ///     three-source algorithm lives in <see cref="PersistentRefRedistributionPass.Run" />.
    /// </summary>
    internal static int RedistributePersistentRefs(
        List<CellRecord> existingCells,
        RecordParserContext context)
    {
        var cellByFormId = new Dictionary<uint, CellRecord>(existingCells.Count);
        foreach (var cell in existingCells)
        {
            cellByFormId.TryAdd(cell.FormId, cell);
        }

        return PersistentRefRedistributionPass.Run(existingCells, cellByFormId, context);
    }

    /// <summary>
    ///     Cross-cell deduplication for runtime-sourced refs. When the same REFR FormID appears
    ///     in multiple non-persistent cells of the same worldspace at the same world position,
    ///     either the engine has copied a persistent ref into every loaded grid cell's
    ///     listReferences array (RuntimeCellList) or the DMP proximity fallback's offset
    ///     window has picked up overlapping REFRs for adjacent cell records (Proximity).
    ///     Consolidate each duplicate set into a single cell using this priority:
    ///     1. Existing cell whose grid contains the ref's world position.
    ///     2. Synthetic virtual exterior tile at the position's grid, if any duplicate's
    ///     target grid is otherwise unrepresented in the worldspace.
    ///     3. The worldspace's persistent cell.
    ///     4. The first cell the ref was seen in (fallback).
    /// </summary>
    internal static int DeduplicateRuntimeCellRefs(
        List<CellRecord> existingCells,
        RecordParserContext context)
    {
        if (existingCells.Count == 0)
        {
            return 0;
        }

        var cellByFormId = new Dictionary<uint, CellRecord>(existingCells.Count);
        var gridToCellByWs = new Dictionary<uint, Dictionary<(int, int), uint>>();
        var persistentCellByWs = new Dictionary<uint, uint>();
        var maxSyntheticFormId = 0xFE800000u;
        foreach (var cell in existingCells)
        {
            cellByFormId.TryAdd(cell.FormId, cell);

            if (cell.FormId is >= 0xFE800000u and < 0xFE900000u && cell.FormId > maxSyntheticFormId)
            {
                maxSyntheticFormId = cell.FormId;
            }

            if (cell.WorldspaceFormId is not uint wsId)
            {
                continue;
            }

            if (cell.IsPersistentCell)
            {
                persistentCellByWs.TryAdd(wsId, cell.FormId);
            }

            if (!cell.IsInterior && cell.GridX.HasValue && cell.GridY.HasValue)
            {
                if (!gridToCellByWs.TryGetValue(wsId, out var gridMap))
                {
                    gridMap = [];
                    gridToCellByWs[wsId] = gridMap;
                }

                gridMap.TryAdd((cell.GridX.Value, cell.GridY.Value), cell.FormId);
            }
        }

        var nextSyntheticFormId = maxSyntheticFormId + 1;

        // Per-worldspace occurrence index: refFormId -> [(cellFormId, ref)]
        var occurrencesByWs =
            new Dictionary<uint, Dictionary<uint, List<(uint CellFormId, PlacedReference Ref)>>>();
        foreach (var cell in existingCells)
        {
            if (cell.WorldspaceFormId is not uint wsId || cell.PlacedObjects.Count == 0)
            {
                continue;
            }

            foreach (var pref in cell.PlacedObjects)
            {
                // Limit dedup to sources that can produce engine/parser-time duplicates:
                //   RuntimeCellList — runtime cell shares persistent refs across loaded grids.
                //   Proximity       — DMP fallback uses offset binary search; adjacent cell
                //                     records pick up overlapping REFR windows.
                if (pref.AssignmentSource is not ("RuntimeCellList" or "Proximity"))
                {
                    continue;
                }

                if (!occurrencesByWs.TryGetValue(wsId, out var refOcc))
                {
                    refOcc = [];
                    occurrencesByWs[wsId] = refOcc;
                }

                if (!refOcc.TryGetValue(pref.FormId, out var list))
                {
                    list = [];
                    refOcc[pref.FormId] = list;
                }

                list.Add((cell.FormId, pref));
            }
        }

        var refsToRemoveFromCell = new Dictionary<uint, HashSet<uint>>();
        var refsToAddToCell = new Dictionary<uint, List<PlacedReference>>();
        var syntheticCellsAdded = new List<CellRecord>();
        var deduplicated = 0;

        foreach (var (wsId, refOcc) in occurrencesByWs)
        {
            if (!gridToCellByWs.TryGetValue(wsId, out var gridMap))
            {
                gridMap = [];
                gridToCellByWs[wsId] = gridMap;
            }

            persistentCellByWs.TryGetValue(wsId, out var persistentCellFormId);

            foreach (var (refFormId, occurrences) in refOcc)
            {
                if (occurrences.Count <= 1)
                {
                    continue;
                }

                // Only treat as engine-time duplicates if all copies share the same position.
                var first = occurrences[0].Ref;
                var samePosition = true;
                for (var i = 1; i < occurrences.Count; i++)
                {
                    var r = occurrences[i].Ref;
                    if (MathF.Abs(r.X - first.X) > 0.5f ||
                        MathF.Abs(r.Y - first.Y) > 0.5f ||
                        MathF.Abs(r.Z - first.Z) > 0.5f)
                    {
                        samePosition = false;
                        break;
                    }
                }

                if (!samePosition)
                {
                    continue;
                }

                uint destCellFormId;
                var (gx, gy) = CellUtils.WorldToCellCoordinates(first.X, first.Y);
                if (gridMap.TryGetValue((gx, gy), out var gridCell))
                {
                    destCellFormId = gridCell;
                }
                else if (MathF.Abs(first.X) > 1f || MathF.Abs(first.Y) > 1f)
                {
                    // Synthesize a virtual exterior tile so the ref lands on the actual map
                    // grid that contains its world position, instead of being dumped into
                    // the persistent cell at (0,0).
                    var wsName = context.GetEditorId(wsId) ?? $"0x{wsId:X8}";
                    var synthetic = new CellRecord
                    {
                        FormId = nextSyntheticFormId++,
                        EditorId = $"[Virtual {gx},{gy} {wsName}]",
                        GridX = gx,
                        GridY = gy,
                        WorldspaceFormId = wsId,
                        PlacedObjects = [],
                        IsVirtual = true,
                        IsBigEndian = first.IsBigEndian
                    };
                    cellByFormId[synthetic.FormId] = synthetic;
                    syntheticCellsAdded.Add(synthetic);
                    gridMap[(gx, gy)] = synthetic.FormId;
                    destCellFormId = synthetic.FormId;
                }
                else if (persistentCellFormId != 0)
                {
                    destCellFormId = persistentCellFormId;
                }
                else
                {
                    destCellFormId = occurrences[0].CellFormId;
                }

                var destHasRef = false;
                foreach (var (cellFormId, _) in occurrences)
                {
                    if (cellFormId == destCellFormId)
                    {
                        destHasRef = true;
                        continue;
                    }

                    if (!refsToRemoveFromCell.TryGetValue(cellFormId, out var removeSet))
                    {
                        removeSet = [];
                        refsToRemoveFromCell[cellFormId] = removeSet;
                    }

                    removeSet.Add(refFormId);
                    deduplicated++;
                }

                if (!destHasRef)
                {
                    if (!refsToAddToCell.TryGetValue(destCellFormId, out var addList))
                    {
                        addList = [];
                        refsToAddToCell[destCellFormId] = addList;
                    }

                    addList.Add(first);
                }
            }
        }

        if (deduplicated == 0)
        {
            return 0;
        }

        if (syntheticCellsAdded.Count > 0)
        {
            existingCells.AddRange(syntheticCellsAdded);
        }

        for (var i = 0; i < existingCells.Count; i++)
        {
            var cell = existingCells[i];
            var hasRemovals = refsToRemoveFromCell.TryGetValue(cell.FormId, out var removeSet);
            var hasAdditions = refsToAddToCell.TryGetValue(cell.FormId, out var addList);

            if (!hasRemovals && !hasAdditions)
            {
                continue;
            }

            var newObjects = new List<PlacedReference>(cell.PlacedObjects.Count);
            foreach (var pref in cell.PlacedObjects)
            {
                if (hasRemovals
                    && pref.AssignmentSource is "RuntimeCellList" or "Proximity"
                    && removeSet!.Contains(pref.FormId))
                {
                    continue;
                }

                newObjects.Add(pref);
            }

            if (hasAdditions)
            {
                newObjects.AddRange(addList!);
            }

            existingCells[i] = cell with { PlacedObjects = newObjects };
        }

        return deduplicated;
    }

    /// <summary>
    ///     Final parser-level guard against cross-worldspace contamination. A placed REFR/ACHR/ACRE
    ///     FormID is a single record and should not survive in multiple cells after persistent
    ///     redistribution and runtime-cell attachment. Keep the best placement, preferring the
    ///     cell whose grid contains the ref position and then real parsed cells over synthetic
    ///     buckets.
    /// </summary>
    internal static int DeduplicatePlacedRefsToBestCell(List<CellRecord> existingCells)
    {
        if (existingCells.Count == 0)
        {
            return 0;
        }

        // First-seen map + a duplicates-only overflow table. The old shape allocated one
        // List<RefOccurrence> (plus backing array) per DISTINCT FormID even though >99.99% of them
        // occur exactly once — O(total refs) garbage to find O(duplicates) work.
        var firstSeen = new Dictionary<uint, RefOccurrence>();
        Dictionary<uint, List<RefOccurrence>>? duplicatesByRef = null;
        for (var cellIndex = 0; cellIndex < existingCells.Count; cellIndex++)
        {
            var cell = existingCells[cellIndex];
            for (var objectIndex = 0; objectIndex < cell.PlacedObjects.Count; objectIndex++)
            {
                var pref = cell.PlacedObjects[objectIndex];
                if (pref.FormId == 0)
                {
                    continue;
                }

                var occurrence = new RefOccurrence(cellIndex, objectIndex, cell, pref);
                if (firstSeen.TryAdd(pref.FormId, occurrence))
                {
                    continue;
                }

                duplicatesByRef ??= [];
                if (!duplicatesByRef.TryGetValue(pref.FormId, out var occurrences))
                {
                    // Seed with the first sighting so encounter order matches the old shape.
                    occurrences = [firstSeen[pref.FormId]];
                    duplicatesByRef[pref.FormId] = occurrences;
                }

                occurrences.Add(occurrence);
            }
        }

        if (duplicatesByRef is null)
        {
            return 0;
        }

        var removalsByCell = new Dictionary<int, HashSet<int>>();
        var removed = 0;
        foreach (var occurrences in duplicatesByRef.Values)
        {
            var best = occurrences
                .OrderByDescending(o => PlacedRefScoring.ScoreRefOccurrence(o.Cell, o.Ref))
                .ThenBy(o => o.CellIndex)
                .ThenBy(o => o.ObjectIndex)
                .First();

            foreach (var occurrence in occurrences)
            {
                if (occurrence.CellIndex == best.CellIndex &&
                    occurrence.ObjectIndex == best.ObjectIndex)
                {
                    continue;
                }

                if (!removalsByCell.TryGetValue(occurrence.CellIndex, out var removals))
                {
                    removals = [];
                    removalsByCell[occurrence.CellIndex] = removals;
                }

                if (removals.Add(occurrence.ObjectIndex))
                {
                    removed++;
                }
            }
        }

        if (removed == 0)
        {
            return 0;
        }

        foreach (var (cellIndex, removals) in removalsByCell)
        {
            // In-place list surgery, NOT `cell with { PlacedObjects = kept }`: worldspace cell
            // lists alias the same CellRecord instances, and replacing the element would fork the
            // graph (the worldspace view would keep the un-deduplicated cell).
            var placed = existingCells[cellIndex].PlacedObjects;
            var kept = new List<PlacedReference>(placed.Count - removals.Count);
            for (var objectIndex = 0; objectIndex < placed.Count; objectIndex++)
            {
                if (!removals.Contains(objectIndex))
                {
                    kept.Add(placed[objectIndex]);
                }
            }

            placed.Clear();
            placed.AddRange(kept);
        }

        return removed;
    }

    /// <summary>
    ///     Phase 0.5: Create stub CellRecords from RuntimeWorldspaceCellMaps for cells known
    ///     to the engine but not parsed from ESM fragments. This includes the persistent
    ///     cell and grid cells from pCellMap hash tables.
    /// </summary>
    internal static int CreateCellMapStubs(
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
    internal static int CreateReferencedCellStubs(
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
    internal static int AssignInteriorOrphans(
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
                cell.PlacedObjects.Add(CellLinkageHandler.ToPlacedReference(orphan, context, "Interior"));
            }
        }

        return cellsCreated;
    }

    /// <summary>
    ///     Build a cell stub from context and orphan ref data for a referenced cell FormID.
    /// </summary>
    internal static CellRecord? BuildReferencedCellStub(
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

    /// <summary>
    ///     Try to resolve an orphan ref at the given grid position to a real cell,
    ///     disambiguating between worldspaces when multiple claim the same grid coords.
    /// </summary>
    internal static CellRecord? TryResolveOrphanByGrid(
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
    internal static List<(uint WsFormId, int MinGX, int MinGY, int MaxGX, int MaxGY, int CellCount)>
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
