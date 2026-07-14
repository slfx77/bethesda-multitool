using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Clones master-FormID DOOR placements captured in NEW (non-master-anchored) exterior
///     cells into fresh NEW REFRs. A proto worldspace like TheStripWorld re-places retail
///     doors that master parents in a DIFFERENT worldspace (TheStripWorldNew); as Overrides
///     those refs die at the verdict parent-cell guard and the Strip loses every door.
///     Cloning re-emits the captured placement under a plugin FormID with the model verbatim
///     — position AND the XTEL teleport destination, which stays the MASTER door REFR and
///     validates against the master-inclusive valid set. Return travel is one-way by user
///     decision: exiting the retail interior leads to the retail Strip.
/// </summary>
/// <remarks>
///     Overlap guard: a captured door whose master copy sits in the SAME worldspace within
///     1 unit of the captured position is NOT cloned (master's own door suffices; a clone
///     would double it). Scope: DOOR-base REFRs only — other world-object overrides in new
///     cells stay dropped (counted by the verdict stats as parent-cell mismatches; separate
///     policy question). Runs BEFORE <see cref="PersistentCellReparenting" /> so a cloned
///     persistent-bucket door is re-homed to the worldspace container like any NEW
///     persistent REFR.
/// </remarks>
public static class OverrideDoorCloning
{
    private const float OverlapDistanceSquared = 1.0f;

    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? masterRefToCell,
        FormIdAllocator allocator)
    {
        if (cells.IsEmpty)
        {
            return cells;
        }

        var result = cells.ToBuilder();
        var anyChanged = false;

        foreach (var (cellFormId, cellPlan) in cells)
        {
            // New-cell exteriors only: master-anchored cells keep their (rare) mismatched
            // doors dropped-and-counted; orphan buckets are removed downstream anyway.
            if (cellPlan.Context.IsInterior
                || cellPlan.CellRecordPlan.Master is not null
                || cellPlan.CellRecordPlan.Model is CellRecord { IsVirtual: true })
            {
                continue;
            }

            var mutated = false;
            var persistent = CloneBucket(cellPlan.PersistentChildren, cellPlan, masterContexts,
                masterRecordsByFormId, masterRefToCell, allocator, ref mutated);
            var temporary = CloneBucket(cellPlan.TemporaryChildren, cellPlan, masterContexts,
                masterRecordsByFormId, masterRefToCell, allocator, ref mutated);
            var vwd = CloneBucket(cellPlan.VwdChildren, cellPlan, masterContexts,
                masterRecordsByFormId, masterRefToCell, allocator, ref mutated);
            if (!mutated)
            {
                continue;
            }

            anyChanged = true;
            result[cellFormId] = cellPlan with
            {
                PersistentChildren = persistent,
                TemporaryChildren = temporary,
                VwdChildren = vwd,
            };
        }

        return anyChanged ? result.ToImmutable() : cells;
    }

    private static ImmutableArray<RecordPlan> CloneBucket(
        ImmutableArray<RecordPlan> bucket,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? masterRefToCell,
        FormIdAllocator allocator,
        ref bool mutated)
    {
        if (bucket.IsEmpty)
        {
            return bucket;
        }

        ImmutableArray<RecordPlan>.Builder? builder = null;
        for (var i = 0; i < bucket.Length; i++)
        {
            var child = bucket[i];
            if (!ShouldClone(child, cellPlan, masterContexts, masterRecordsByFormId, masterRefToCell,
                    out var placed))
            {
                builder?.Add(child);
                continue;
            }

            builder ??= SeedBuilder(bucket, i);
            builder.Add(child with
            {
                Disposition = RecordDisposition.New,
                FormId = allocator.Allocate(),
                SourceFormId = child.FormId,
                Master = null,
                Provenance = new PlanProvenance
                {
                    PolicyId = "OverrideDoorCloning",
                    Reason = "Master door re-placed by the proto in a new-worldspace cell; "
                             + "cloned as NEW with the captured placement + teleport preserved "
                             + $"(master REFR 0x{child.FormId:X8}, base 0x{placed.BaseFormId:X8}).",
                },
            });
            mutated = true;
        }

        return builder?.ToImmutable() ?? bucket;
    }

    private static ImmutableArray<RecordPlan>.Builder SeedBuilder(
        ImmutableArray<RecordPlan> bucket, int upToExclusive)
    {
        var builder = ImmutableArray.CreateBuilder<RecordPlan>(bucket.Length);
        for (var i = 0; i < upToExclusive; i++)
        {
            builder.Add(bucket[i]);
        }

        return builder;
    }

    private static bool ShouldClone(
        RecordPlan child,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? masterRefToCell,
        out PlacedReference placed)
    {
        placed = null!;
        if (child.Disposition != RecordDisposition.Override
            || child.Type != "REFR"
            || child.Model is not PlacedReference model)
        {
            return false;
        }

        placed = model;
        if (model.IsMapMarker
            || !masterRecordsByFormId.TryGetValue(model.BaseFormId, out var baseRecord)
            || baseRecord.Header.Signature != "DOOR"
            || !masterRecordsByFormId.TryGetValue(child.FormId, out var masterRef))
        {
            return false;
        }

        return !OverlapsMasterPlacement(child.FormId, model, cellPlan, masterContexts,
            masterRef, masterRefToCell);
    }

    /// <summary>
    ///     True when master places the same door REFR in the SAME worldspace within 1 unit
    ///     of the captured position — cloning there would double the door.
    /// </summary>
    private static bool OverlapsMasterPlacement(
        uint refFormId,
        PlacedReference placed,
        CellPlan cellPlan,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        ParsedMainRecord masterRef,
        IReadOnlyDictionary<uint, uint>? masterRefToCell)
    {
        if (masterRefToCell is null
            || !masterRefToCell.TryGetValue(refFormId, out var masterParentCell)
            || !masterContexts.TryGetValue(masterParentCell, out var masterCellContext)
            || masterCellContext.WorldspaceFormId != cellPlan.Context.WorldspaceFormId)
        {
            return false; // Different (or unknown) worldspace — positions can't collide.
        }

        if (!PluginBuilder.TryReadPlacementData(masterRef, out var masterPosition))
        {
            return false;
        }

        var dx = placed.X - masterPosition.X;
        var dy = placed.Y - masterPosition.Y;
        var dz = placed.Z - masterPosition.Z;
        return (dx * dx) + (dy * dy) + (dz * dz) <= OverlapDistanceSquared;
    }
}
