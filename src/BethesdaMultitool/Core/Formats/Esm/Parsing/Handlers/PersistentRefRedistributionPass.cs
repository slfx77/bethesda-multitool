using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Phase 0.75 redistribution pass: moves persistent refs with valid world positions into the
///     real exterior tile they belong to. Extracted from <see cref="PersistentRefRedistributor" />,
///     which keeps the public entrypoint and delegates here.
/// </summary>
internal static class PersistentRefRedistributionPass
{
    /// <summary>
    ///     Phase 0.75: Move persistent refs with valid world positions into the real exterior
    ///     tile they belong to. Refs keep IsPersistent=true and gain
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
    internal static int Run(
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
            if (pcell.PlacedObjects.Count == 0)
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
                if (!ShouldRedistributePersistentRef(pcell, pref))
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
                    cellByFormId.TryGetValue(destCellFormId, out var existing))
                {
                    if (existing.FormId == pcell.FormId)
                    {
                        keep.Add(pref);
                        continue;
                    }

                    destCell = existing;
                }
                else
                {
                    // Source 3: synthesize a virtual exterior tile for this worldspace — but ONLY for DMP
                    // captures. Synthesis exists so persistent refs from a dump whose streaming cache is
                    // empty (main menu / interior cell / loading screen) still land in their owning
                    // exterior tile. For a plain ESM parse (no DMP), a worldspace with no real exterior
                    // cell at this grid is genuinely interior-only (e.g. Oblivion's ArkvedsTowerWorld);
                    // its persistent refs carry interior-local coords near the origin, so fabricating a
                    // (0,0) tile produces a phantom virtual cell. Keep the ref on its persistent container.
                    if (context.MinidumpInfo is null)
                    {
                        keep.Add(pref);
                        continue;
                    }

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

    private static bool ShouldRedistributePersistentRef(CellRecord cell, PlacedReference pref)
    {
        // Refs placed via runtime cell lists or runtime grid lookup are explicit engine
        // placements and must stay where the dump put them, even if their world position
        // would fall in another tile.
        if (pref.AssignmentSource is not ("ParentCell" or "CellGrup"))
        {
            return false;
        }

        if (cell.IsPersistentCell)
        {
            return true;
        }

        // Xbox/converted ESMs can carry persistent REFR/ACHR/ACRE records under a normal
        // exterior cell's persistent child group. Those refs still need the same coordinate
        // rebucketing as worldspace persistent-cell refs so DMP and ESM reports compare the
        // door/object in the visible exterior cell row.
        return pref.IsPersistent &&
               !cell.IsInterior &&
               !cell.IsVirtual &&
               !cell.IsUnresolvedBucket &&
               cell.WorldspaceFormId.HasValue &&
               cell.GridX.HasValue &&
               cell.GridY.HasValue;
    }
}
