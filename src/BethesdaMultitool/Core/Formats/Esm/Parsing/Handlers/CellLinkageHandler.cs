using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Static utility methods for linking cells to worldspaces, inferring worldspace membership,
///     and creating virtual cells for orphan placed references in DMP mode.
/// </summary>
internal static class CellLinkageHandler
{
    private const string SourceNoBoundsFallback = "NoBoundsFallback";
    private const string SourceUniqueBounds = "UniqueBounds";
    private const string SourceAmbiguousBounds = "AmbiguousBounds";
    private const string SourceFragmentRun = "FragmentRun";
    private const string SourceRuntimeCellMap = "RuntimeCellMap";

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

        var worldspaceBounds = CellWorldspaceBoundsResolver.BuildWorldspaceBounds(worldspaces);

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
                    cells[i] = cell with
                    {
                        WorldspaceFormId = fallback.FormId,
                        WorldspaceAssignmentSource = SourceNoBoundsFallback,
                        CandidateWorldspaceFormIds = [fallback.FormId]
                    };
                }
            }

            Logger.Instance.Debug(
                $"  [Semantic] InferCellWorldspaces: no bounds data, assigned all exterior cells to {fallback.EditorId ?? $"0x{fallback.FormId:X8}"}");
            return;
        }

        var inferredCount = 0;
        var ambiguousCount = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsInterior || !cell.GridX.HasValue || cell.WorldspaceFormId is > 0)
            {
                continue;
            }

            var gx = cell.GridX.Value;
            var gy = cell.GridY!.Value;
            var candidates = CellWorldspaceBoundsResolver.FindCandidateWorldspaces(worldspaceBounds, gx, gy);
            if (candidates.Count == 0)
            {
                continue;
            }

            if (candidates.Count == 1)
            {
                cells[i] = cell with
                {
                    WorldspaceFormId = candidates[0],
                    WorldspaceAssignmentSource = SourceUniqueBounds,
                    CandidateWorldspaceFormIds = candidates
                };
                inferredCount++;
            }
            else
            {
                cells[i] = cell with
                {
                    WorldspaceAssignmentSource = SourceAmbiguousBounds,
                    CandidateWorldspaceFormIds = candidates
                };
                ambiguousCount++;
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] InferCellWorldspaces: inferred {inferredCount} cells, left {ambiguousCount} ambiguous across {worldspaceBounds.Count} worldspaces");
    }

    /// <summary>
    ///     Resolve ambiguous DMP cells by connected FormID/grid runs anchored by runtime pCellMap ownership.
    /// </summary>
    internal static int ResolveRuntimeAnchoredCellRuns(
        List<CellRecord> cells,
        List<WorldspaceRecord> worldspaces,
        IReadOnlyDictionary<uint, RuntimeWorldspaceData>? runtimeWorldspaceMaps)
    {
        if (cells.Count == 0 || runtimeWorldspaceMaps is not { Count: > 0 })
        {
            return 0;
        }

        var bounds = CellWorldspaceBoundsResolver.BuildWorldspaceBounds(worldspaces);
        var runtimeOwnerByCell = new Dictionary<uint, uint>();
        foreach (var (worldspaceFormId, worldspaceData) in runtimeWorldspaceMaps)
        {
            foreach (var entry in worldspaceData.Cells)
            {
                runtimeOwnerByCell.TryAdd(entry.CellFormId, worldspaceFormId);
            }
        }

        if (runtimeOwnerByCell.Count == 0)
        {
            return 0;
        }

        var indexByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < cells.Count; i++)
        {
            if (!cells[i].IsInterior && cells[i].GridX.HasValue && cells[i].GridY.HasValue)
            {
                indexByFormId.TryAdd(cells[i].FormId, i);
            }
        }

        var visited = new HashSet<uint>();
        var reassigned = 0;
        var startFormIds = cells
            .Where(c => indexByFormId.ContainsKey(c.FormId))
            .Select(c => c.FormId)
            .ToList();
        foreach (var startFormId in startFormIds)
        {
            if (!visited.Add(startFormId))
            {
                continue;
            }

            var component = CellWorldspaceBoundsResolver.BuildAdjacentFormIdComponent(
                startFormId, cells, indexByFormId, visited);
            var anchorWorldspaces = component
                .Select(index => cells[index].FormId)
                .Where(runtimeOwnerByCell.ContainsKey)
                .Select(formId => runtimeOwnerByCell[formId])
                .Distinct()
                .ToList();
            if (anchorWorldspaces.Count != 1)
            {
                continue;
            }

            var anchorWorldspace = anchorWorldspaces[0];
            foreach (var index in component)
            {
                var candidate = cells[index];
                if (runtimeOwnerByCell.ContainsKey(candidate.FormId) ||
                    candidate.WorldspaceAssignmentSource is "CellGrup" or SourceRuntimeCellMap)
                {
                    continue;
                }

                if (!CellWorldspaceBoundsResolver.CanAssignToAnchoredWorldspace(candidate, anchorWorldspace, bounds))
                {
                    continue;
                }

                var candidates = candidate.CandidateWorldspaceFormIds.Count > 0
                    ? candidate.CandidateWorldspaceFormIds
                    : CellWorldspaceBoundsResolver.FindCandidateWorldspaces(
                        bounds, candidate.GridX!.Value, candidate.GridY!.Value);
                cells[index] = candidate with
                {
                    WorldspaceFormId = anchorWorldspace,
                    WorldspaceAssignmentSource = SourceFragmentRun,
                    CandidateWorldspaceFormIds = candidates.Count > 0 ? candidates : [anchorWorldspace]
                };
                reassigned++;
            }
        }

        if (reassigned > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] ResolveRuntimeAnchoredCellRuns: reassigned {reassigned} ambiguous cells from runtime-anchored fragments");
        }

        return reassigned;
    }

    /// <summary>
    ///     DMP fallback: assign worldspace ownership directly from the runtime pCellMap to any
    ///     exterior cell still lacking a worldspace. <see cref="ResolveRuntimeAnchoredCellRuns" />
    ///     only anchors ambiguous cells reachable from a runtime-owned FormID/grid run; an isolated
    ///     cell that <em>is</em> in a walked pCellMap but has no neighbors and no bounds candidates
    ///     would otherwise stay null. The pCellMap owner is authoritative (see
    ///     <c>RuntimeDataEnricher.EnrichWorldspaceCellMaps</c>), so this is a safe last-step backfill
    ///     mirroring the direct lookup the cell-inventory path already trusts. Grid is backfilled
    ///     from the map entry only when the cell lacks it (exterior cells need grid to emit).
    /// </summary>
    internal static int AssignRuntimeCellMapOwners(
        List<CellRecord> cells,
        IReadOnlyDictionary<uint, RuntimeWorldspaceData>? runtimeWorldspaceMaps)
    {
        if (cells.Count == 0 || runtimeWorldspaceMaps is not { Count: > 0 })
        {
            return 0;
        }

        var ownerByCell = new Dictionary<uint, RuntimeCellMapEntry>();
        var worldspaceByCell = new Dictionary<uint, uint>();
        foreach (var (worldspaceFormId, worldspaceData) in runtimeWorldspaceMaps)
        {
            foreach (var entry in worldspaceData.Cells)
            {
                if (worldspaceByCell.TryAdd(entry.CellFormId, worldspaceFormId))
                {
                    ownerByCell[entry.CellFormId] = entry;
                }
            }
        }

        if (worldspaceByCell.Count == 0)
        {
            return 0;
        }

        var assigned = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsInterior || cell.IsPersistentCell || cell.WorldspaceFormId is > 0)
            {
                continue;
            }

            if (!worldspaceByCell.TryGetValue(cell.FormId, out var owner) || owner == 0)
            {
                continue;
            }

            var entry = ownerByCell[cell.FormId];
            cells[i] = cell with
            {
                WorldspaceFormId = owner,
                WorldspaceAssignmentSource = SourceRuntimeCellMap,
                GridX = cell.GridX ?? entry.GridX,
                GridY = cell.GridY ?? entry.GridY,
                CandidateWorldspaceFormIds = cell.CandidateWorldspaceFormIds.Count > 0
                    ? cell.CandidateWorldspaceFormIds
                    : [owner]
            };
            assigned++;
        }

        if (assigned > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] AssignRuntimeCellMapOwners: assigned {assigned} exterior cell(s) from runtime pCellMap");
        }

        return assigned;
    }

    /// <summary>
    ///     DMP fallback: reclassify structurally-interior cells that were captured with the interior
    ///     flag cleared. A valid <em>exterior</em> cell always carries both a parent worldspace and
    ///     grid coordinates; a cell flagged exterior yet missing <em>both</em> a worldspace and grid
    ///     cannot be exterior and is treated as interior so it emits via the interior path instead of
    ///     being skipped for "missing parent worldspace." Surfaced by cut/dev cells such as the
    ///     <c>HiddenValley*Sim</c> VR areas (cCellFlags 0x52 — no 0x01 interior bit, pWorldSpace null,
    ///     no grid). Runs after all worldspace inference so a cell that legitimately resolved a
    ///     worldspace (and therefore is exterior) is never flipped. Virtual orphan-recovery cells are
    ///     left untouched.
    /// </summary>
    internal static int NormalizeStructurallyInteriorCells(List<CellRecord> cells)
    {
        var reclassified = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (cell.IsInterior ||
                cell.IsVirtual ||
                cell.IsPersistentCell ||
                cell.WorldspaceFormId is > 0 ||
                cell.GridX.HasValue ||
                cell.GridY.HasValue)
            {
                continue;
            }

            cells[i] = cell with { Flags = (byte)(cell.Flags | 0x01) };
            reclassified++;
        }

        if (reclassified > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] NormalizeStructurallyInteriorCells: reclassified {reclassified} worldspace-less, grid-less exterior cell(s) as interior");
        }

        return reclassified;
    }

    /// <summary>
    ///     DMP fallback: merge orphan-recovery VIRTUAL cells into a co-located non-virtual cell of
    ///     the same worldspace and grid. Orphan refs whose parent cell couldn't be resolved are
    ///     placed in synthetic virtual cells keyed by world position; when a non-virtual cell already
    ///     occupies that (worldspace, grid) the emission-time grid-collision dedup
    ///     (<c>PluginBuilder._emittedExteriorCellCoords</c>) drops the virtual cell and loses its
    ///     refs. Seen in the cut <c>TheStripWorld</c>: the VStreetFluff travel markers were recovered
    ///     into a virtual (0,0) cell colliding with the worldspace persistent cell, so the markers
    ///     never emitted and every package referencing them dangled. Consolidate the refs into the
    ///     surviving non-virtual cell (deduping by FormID) and drop the now-redundant virtual cell so
    ///     the refs emit. Strictly recovers refs the dedup would otherwise discard.
    ///     <para>
    ///     <paramref name="masterFormIds" /> (master ESM record FormIDs) excludes master cells from
    ///     being keepers — recovery targets cut/proto content only. Merging orphan refs into a MASTER
    ///     cell (e.g. a live worldspace's persistent cell such as TheStripWorldNew's 0x0013B310)
    ///     corrupts a real game cell and crashes on GridCellArray attach; for master worldspaces the
    ///     orphan refs are redundant with master, so dropping them (the prior behavior) is correct.
    ///     </para>
    /// </summary>
    internal static int MergeColocatedVirtualOrphanCells(
        List<CellRecord> cells,
        IReadOnlySet<uint>? masterFormIds = null)
    {
        var keeperByKey = new Dictionary<(uint Worldspace, int GridX, int GridY), int>();
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (c.IsVirtual || c.IsInterior || c.WorldspaceFormId is not > 0 ||
                !c.GridX.HasValue || !c.GridY.HasValue ||
                masterFormIds?.Contains(c.FormId) == true)
            {
                continue;
            }

            keeperByKey.TryAdd((c.WorldspaceFormId.Value, c.GridX.Value, c.GridY.Value), i);
        }

        if (keeperByKey.Count == 0)
        {
            return 0;
        }

        var mergedRefs = 0;
        var consumedVirtual = new HashSet<int>();
        for (var i = 0; i < cells.Count; i++)
        {
            var c = cells[i];
            if (!c.IsVirtual || c.IsInterior || c.IsUnresolvedBucket || c.WorldspaceFormId is not > 0 ||
                !c.GridX.HasValue || !c.GridY.HasValue || c.PlacedObjects.Count == 0)
            {
                continue;
            }

            if (!keeperByKey.TryGetValue((c.WorldspaceFormId.Value, c.GridX.Value, c.GridY.Value),
                    out var keeperIndex) || keeperIndex == i)
            {
                continue;
            }

            var keeper = cells[keeperIndex];
            var seen = new HashSet<uint>(keeper.PlacedObjects.Select(p => p.FormId));
            var additions = c.PlacedObjects.Where(p => seen.Add(p.FormId)).ToList();
            if (additions.Count > 0)
            {
                var newPlaced = new List<PlacedReference>(keeper.PlacedObjects);
                newPlaced.AddRange(additions);
                cells[keeperIndex] = keeper with { PlacedObjects = newPlaced };
                mergedRefs += additions.Count;
            }

            consumedVirtual.Add(i);
        }

        if (consumedVirtual.Count == 0)
        {
            return 0;
        }

        var kept = new List<CellRecord>(cells.Count - consumedVirtual.Count);
        for (var i = 0; i < cells.Count; i++)
        {
            if (!consumedVirtual.Contains(i))
            {
                kept.Add(cells[i]);
            }
        }

        cells.Clear();
        cells.AddRange(kept);

        Logger.Instance.Debug(
            $"  [Semantic] MergeColocatedVirtualOrphanCells: merged {mergedRefs} placed ref(s) from " +
            $"{consumedVirtual.Count} virtual cell(s) into co-located non-virtual cells");
        return mergedRefs;
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
        var parentSignalStubsCreated =
            PersistentRefRedistributor.CreateReferencedCellStubs(orphans, existingCells, cellByFormId, context);

        // Persistent-ref redistribution is hoisted to a separate top-level pass invoked from
        // RecordParser AFTER CreateVirtualCells returns. By then Phase 1 has already moved
        // persistent refs into their owning persistent cell containers via ParentCellFormId,
        // and the redistribution has refs to actually move. Calling it here would run before
        // those refs exist.

        // Phase Interior: Group interior orphans by their parent cell FormID.
        var interiorCellsCreated =
            PersistentRefRedistributor.AssignInteriorOrphans(interiorOrphans, existingCells, cellByFormId, context);
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

        var virtualCells = new List<CellRecord>();

        // Phase 1.25: some DMP fragments preserve REFR runs immediately before the first
        // CELL record in the same authored worldspace fragment. The forward-only CELL offset
        // proximity fallback cannot attach those refs to a real cell because the owning CELL
        // record is missing from the dump. When a whole orphan offset cluster sits next to
        // exactly one worldspace's parsed cells, create a worldspace-scoped virtual tile at
        // the refs' actual grid instead of leaving them in the global unresolved bucket.
        var offsetClusterResolved = OffsetClusterOrphanResolver.ResolveOffsetClusteredExteriorOrphans(
            trueOrphans,
            existingCells,
            context,
            out var offsetClusterVirtualCells);
        if (offsetClusterResolved > 0)
        {
            virtualCells.AddRange(offsetClusterVirtualCells);
            var offsetClusterResolvedFormIds = offsetClusterVirtualCells
                .SelectMany(cell => cell.PlacedObjects)
                .Select(placed => placed.FormId)
                .ToHashSet();
            trueOrphans = trueOrphans
                .Where(orphan => !offsetClusterResolvedFormIds.Contains(orphan.Header.FormId))
                .ToList();

            Logger.Instance.Debug(
                $"  [Semantic] CreateVirtualCells: {offsetClusterResolved} orphan ref(s) resolved via offset-cluster worldspace adjacency");

            if (trueOrphans.Count == 0)
            {
                Logger.Instance.Debug(
                    "  [Semantic] CreateVirtualCells: all orphans resolved, no unresolved buckets needed");
                return virtualCells;
            }
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
                    return virtualCells;
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
                        WorldspaceAssignmentSource = "Virtual",
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
            MaterialSwapFormId = r.MaterialSwapFormId,
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
            TeleportPosRot = r.TeleportPosRot,
            TeleportFlags = r.TeleportFlags,
            IsMapMarker = r.IsMapMarker,
            MarkerType = r.MarkerType.HasValue ? (MapMarkerType)r.MarkerType.Value : null,
            // Resolve the marker's FULL through the localized .STRINGS table (the table isn't loaded during the
            // descriptor scan, so the eager MarkerName is a 4-byte string ID misread as garbage for Skyrim/FO4).
            // ReadFullName resolves the ID and falls back to inline text for non-localized FNV/Xbox plugins.
            MarkerName = r.MarkerNameRaw is { Length: > 0 } rawMarkerName
                ? context.ReadFullName(rawMarkerName)
                : r.MarkerName,
            LinkedRefKeywordFormId = r.LinkedRefKeywordFormId,
            LinkedRefFormId = r.LinkedRefFormId,
            LinkedRefChildrenFormIds = r.LinkedRefChildrenFormIds,
            StructuralData = r.StructuralData,
            EditorId = r.EditorId,
            Offset = r.Header.Offset,
            IsBigEndian = r.Header.IsBigEndian,
            AssignmentSource = assignmentSource
        };
    }
}
