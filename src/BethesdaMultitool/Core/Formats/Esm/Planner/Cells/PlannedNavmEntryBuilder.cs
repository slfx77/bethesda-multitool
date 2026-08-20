using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Builds the PlannedNavmEntry list the writer hands to NavInfoMapBuilder so master's
///     NAVI gets extended with NVMI/NVCI rows pointing at our new NAVM FormIDs. Without
///     this the FNV runtime null-derefs at FalloutNV+0x0069E09A during plugin load when
///     NavMeshInfoMap tries to resolve a new NAVM FormID. Mirrors the legacy emission
///     pattern in the retired legacy cell loop.
/// </summary>
internal static class PlannedNavmEntryBuilder
{
    public static ImmutableArray<PlannedNavmEntry> Build(
        IReadOnlyList<CellRecord> dmpCells,
        IReadOnlyList<NavMeshRecord> dmpNavmeshes,
        ImmutableDictionary<uint, uint> navmSourceToEmitted,
        ImmutableDictionary<uint, uint> cellSourceToEmitted,
        ImmutableDictionary<uint, uint> worldspaceSourceToEmitted,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        bool emitMasterCellNavmAugmentation,
        IReadOnlyDictionary<uint, uint> protoToMasterCellAlias)
    {
        if (navmSourceToEmitted.IsEmpty)
        {
            return ImmutableArray<PlannedNavmEntry>.Empty;
        }

        var cellsByFormId = new Dictionary<uint, CellRecord>(dmpCells.Count);
        foreach (var cell in dmpCells)
        {
            cellsByFormId[cell.FormId] = cell;
        }

        var builder = ImmutableArray.CreateBuilder<PlannedNavmEntry>(navmSourceToEmitted.Count);
        foreach (var navm in dmpNavmeshes)
        {
            if (!navmSourceToEmitted.TryGetValue(navm.FormId, out var emittedNavmFormId))
            {
                continue; // Master-resident NAVM or filtered out by CellChildAllocator.
            }

            if (!cellsByFormId.TryGetValue(navm.CellFormId, out var parentCell))
            {
                continue; // Orphan NAVM — no parent cell captured. Skip rather than crash.
            }

            var aliasedCellFormId = protoToMasterCellAlias.TryGetValue(navm.CellFormId, out var aliasTarget)
                ? aliasTarget
                : navm.CellFormId;

            // Master-cell NAVM augmentation (default on): emit the proto's NAVM for an
            // overridden master cell so NPCs pathfind on the reshaped layout. Master's own
            // NAVMs are never copied — they load from master via the cell file-list merge
            // (see PlanCellSectionBuilder.EncodeNavm). Plan() pre-filters these when the
            // flag is off; this guard stays as defense in depth.
            if (!emitMasterCellNavmAugmentation && masterContexts.ContainsKey(aliasedCellFormId))
            {
                continue;
            }

            // LocationFormId: interior cells use the cell FormID; exterior cells use the
            // parent worldspace FormID. For new worldspaces we must use the EMITTED FormID
            // (post-allocation), otherwise NavMeshInfoMap setup at FalloutNV+0x0069DFDC
            // looks up a non-existent FormID and crashes. Matches legacy logic in
            // the retired legacy cell loop.
            //
            // Cell FormIDs follow the same emitted-FormID rule: if Pass 0 reallocated
            // the parent cell (proto FormID didn't match a master cell), the runtime
            // sees the cell at its emitted FormID; coord-paired cells resolve through
            // the proto→master alias.
            uint locationFid;
            if (parentCell.IsInterior)
            {
                locationFid = cellSourceToEmitted.TryGetValue(parentCell.FormId, out var emittedCellFid)
                    ? emittedCellFid
                    : aliasedCellFormId;
            }
            else if (parentCell.WorldspaceFormId is { } srcWrldId
                     && worldspaceSourceToEmitted.TryGetValue(srcWrldId, out var emittedWrldId))
            {
                locationFid = emittedWrldId;
            }
            else if (parentCell.WorldspaceFormId is { } masterWrldId)
            {
                locationFid = masterWrldId;
            }
            else
            {
                locationFid = cellSourceToEmitted.TryGetValue(parentCell.FormId, out var emittedCellFid)
                    ? emittedCellFid
                    : aliasedCellFormId;
            }

            var nvvxBytes = navm.RawSubrecords
                .FirstOrDefault(s => s.Signature == "NVVX").Bytes ?? [];

            builder.Add(new PlannedNavmEntry
            {
                NavmFormId = emittedNavmFormId,
                LocationFormId = locationFid,
                IsInterior = parentCell.IsInterior,
                GridX = parentCell.IsInterior ? 0 : parentCell.GridX ?? 0,
                GridY = parentCell.IsInterior ? 0 : parentCell.GridY ?? 0,
                NvvxBytes = nvvxBytes
            });
        }

        return builder.ToImmutable();
    }
}
