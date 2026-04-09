using FalloutXbox360Utils.Core.Formats.Esm.Analysis;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Persistent-ref redistribution: walks every persistent CellRecord container and moves
///     refs with valid world positions into the real exterior tile they belong to. Also provides
///     helpers for creating cell stubs from runtime cell maps and resolving orphan refs by grid
///     position, used by both this class and <see cref="CellLinkageHandler" />.
/// </summary>
internal static class PersistentRefRedistributor
{
    /// <summary>
    ///     Top-level pass invoked from RecordParser after CreateVirtualCells. Walks every
    ///     persistent CellRecord container and moves refs with valid world positions into the
    ///     real exterior tile they belong to. See
    ///     <see cref="RedistributePersistentRefs(List{CellRecord}, Dictionary{uint, CellRecord}, RecordParserContext)" />
    ///     for the full algorithm.
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

        return RedistributePersistentRefs(existingCells, cellByFormId, context);
    }

    /// <summary>
    ///     Phase 0.75: Walk every persistent CellRecord and move refs with valid world positions
    ///     into the real exterior tile they belong to. Refs keep IsPersistent=true and gain
    ///     OriginCellFormId pointing back at the persistent container, so reports can still
    ///     reconstruct a "persistent only" view.
    ///     Destination grid lookup uses three sources, in order:
    ///     1. Runtime worldspace cell maps (TESWorldSpace pCellMap hash tables) when populated.
    ///     2. Parsed CellRecords carved from ESM fragments — these carry WorldspaceFormId
    ///     and grid coords from XCLC subrecords.
    ///     3. Synthetic per-worldspace virtual exterior tiles created on the fly when neither
    ///     source covers the destination grid. This handles dumps where the streaming cache
    ///     is empty (main menu, interior cell, loading screen) so persistent refs still
    ///     appear in their owning exterior tiles instead of pooling in the persistent
    ///     container.
    /// </summary>
    private static int RedistributePersistentRefs(
        List<CellRecord> existingCells,
        Dictionary<uint, CellRecord> cellByFormId,
        RecordParserContext context)
    {
        // Source 1: runtime cell maps.
        var gridToCellByWorldspace = new Dictionary<uint, Dictionary<(int, int), uint>>();
        if (context.RuntimeWorldspaceCellMaps is { Count: > 0 })
        {
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
        }

        // Source 2: parsed CellRecords with worldspace + grid coords. Fold them into the
        // same per-worldspace grid map so a single TryGetValue covers both sources.
        foreach (var cell in existingCells)
        {
            if (cell.IsInterior || cell.IsPersistentCell || cell.IsUnresolvedBucket)
            {
                continue;
            }

            if (cell.WorldspaceFormId is not uint wsId || !cell.GridX.HasValue || !cell.GridY.HasValue)
            {
                continue;
            }

            if (!gridToCellByWorldspace.TryGetValue(wsId, out var gridMap))
            {
                gridMap = new Dictionary<(int, int), uint>();
                gridToCellByWorldspace[wsId] = gridMap;
            }

            gridMap.TryAdd((cell.GridX.Value, cell.GridY.Value), cell.FormId);
        }

        // Source 3: synthetic virtual tiles allocated on demand below.
        // Use a private FormID range to avoid colliding with Phase 2 (0xFF000001u) and
        // Unresolved buckets (0xFE100001u).
        var syntheticFormId = 0xFE800001u;
        var syntheticCellsAdded = 0;
        var moved = 0;
        var syntheticUsed = 0;

        for (var i = 0; i < existingCells.Count; i++)
        {
            var pcell = existingCells[i];
            if (!pcell.IsPersistentCell || pcell.PlacedObjects.Count == 0)
            {
                continue;
            }

            var wsId = pcell.WorldspaceFormId;
            if (wsId is null)
            {
                continue;
            }

            if (!gridToCellByWorldspace.TryGetValue(wsId.Value, out var gridMap))
            {
                gridMap = new Dictionary<(int, int), uint>();
                gridToCellByWorldspace[wsId.Value] = gridMap;
            }

            var keep = new List<PlacedReference>(pcell.PlacedObjects.Count);
            foreach (var pref in pcell.PlacedObjects)
            {
                // Only redistribute refs that landed here via ParentCellFormId reassignment.
                // Refs placed via the runtime cell list (RuntimeCellList) or grid lookup
                // (GridMap) are explicit runtime placements and must stay where the engine
                // put them, even when their world position would point at a different tile.
                if (pref.AssignmentSource != "ParentCell")
                {
                    keep.Add(pref);
                    continue;
                }

                // Refs with essentially-zero coordinates are either container refs with no
                // exterior position or uninitialized; leave them on the persistent cell.
                var hasPosition = MathF.Abs(pref.X) > 1f || MathF.Abs(pref.Y) > 1f;
                if (!hasPosition)
                {
                    keep.Add(pref);
                    continue;
                }

                var (gx, gy) = CellUtils.WorldToCellCoordinates(pref.X, pref.Y);

                CellRecord? destCell = null;
                if (gridMap.TryGetValue((gx, gy), out var destCellFormId) &&
                    cellByFormId.TryGetValue(destCellFormId, out var existing) &&
                    existing.FormId != pcell.FormId)
                {
                    destCell = existing;
                }
                else
                {
                    // Source 3: synthesize a virtual exterior tile for this worldspace.
                    var wsName = context.GetEditorId(wsId.Value) ?? $"0x{wsId.Value:X8}";
                    var synthetic = new CellRecord
                    {
                        FormId = syntheticFormId++,
                        EditorId = $"[Virtual {gx},{gy} {wsName}]",
                        GridX = gx,
                        GridY = gy,
                        WorldspaceFormId = wsId.Value,
                        PlacedObjects = [],
                        IsVirtual = true,
                        IsBigEndian = pref.IsBigEndian
                    };
                    cellByFormId[synthetic.FormId] = synthetic;
                    existingCells.Add(synthetic);
                    gridMap[(gx, gy)] = synthetic.FormId;
                    destCell = synthetic;
                    syntheticCellsAdded++;
                }

                destCell.PlacedObjects.Add(pref with
                {
                    OriginCellFormId = pcell.FormId,
                    AssignmentSource =
                    destCell.IsVirtual ? "PersistentRedistributedSynthetic" : "PersistentRedistributed"
                });
                moved++;
                if (destCell.IsVirtual)
                {
                    syntheticUsed++;
                }
            }

            if (keep.Count != pcell.PlacedObjects.Count)
            {
                existingCells[i] = pcell with { PlacedObjects = keep };
                cellByFormId[pcell.FormId] = existingCells[i];
            }
        }

        if (syntheticCellsAdded > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: synthesized {syntheticCellsAdded} virtual exterior tile(s) " +
                $"for persistent ref redistribution ({syntheticUsed} refs placed via synthesis)");
        }

        return moved;
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
