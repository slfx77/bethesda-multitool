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
/// </summary>
/// <remarks>
///     The first pass preserves the historical overlap guard: a captured door whose master
///     copy sits in the SAME worldspace within 1 unit of the captured position is not cloned.
///     The second pass repairs a narrower prototype/master identity collision. Some captured
///     XTEL destinations reuse retail REFR FormIDs whose retail NAME is a STAT, while the DMP
///     contains a reciprocal DOOR-base placement at that identity. When that counterpart is
///     unique and captured under a live cell, it too is cloned and both XTELs are rewritten to
///     the allocated pair. The retail static remains untouched. If the counterpart cannot be
///     proved safe, the incompatible XTEL is removed and diagnosed instead of pointing the
///     engine at a non-door REFR.
/// </remarks>
public static class OverrideDoorCloning
{
    private const float OverlapDistanceSquared = 1.0f;
    private const string ClonePolicyId = "OverrideDoorCloning";

    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? masterRefToCell,
        FormIdAllocator allocator) =>
        Apply(cells, masterContexts, masterRecordsByFormId, masterRefToCell, allocator,
            out _, out _);

    public static ImmutableDictionary<uint, CellPlan> Apply(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? masterRefToCell,
        FormIdAllocator allocator,
        out ImmutableHashSet<uint> clonedFormIds,
        out ImmutableArray<PlanDiagnostic> diagnostics)
    {
        if (cells.IsEmpty)
        {
            clonedFormIds = ImmutableHashSet<uint>.Empty;
            diagnostics = ImmutableArray<PlanDiagnostic>.Empty;
            return cells;
        }

        var clonesBySource = new Dictionary<uint, uint>();
        var result = CloneNewWorldspaceDoors(
            cells, masterContexts, masterRecordsByFormId, masterRefToCell, allocator,
            clonesBySource);

        result = DoorTeleportTargetRescue.Apply(
            result, masterRecordsByFormId, allocator, clonesBySource, out diagnostics);

        clonedFormIds = clonesBySource.Values.ToImmutableHashSet();
        return result;
    }

    private static ImmutableDictionary<uint, CellPlan> CloneNewWorldspaceDoors(
        ImmutableDictionary<uint, CellPlan> cells,
        IReadOnlyDictionary<uint, PcEsmCellContext> masterContexts,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, uint>? masterRefToCell,
        FormIdAllocator allocator,
        Dictionary<uint, uint> clonesBySource)
    {
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
                masterRecordsByFormId, masterRefToCell, allocator, clonesBySource, ref mutated);
            var temporary = CloneBucket(cellPlan.TemporaryChildren, cellPlan, masterContexts,
                masterRecordsByFormId, masterRefToCell, allocator, clonesBySource, ref mutated);
            var vwd = CloneBucket(cellPlan.VwdChildren, cellPlan, masterContexts,
                masterRecordsByFormId, masterRefToCell, allocator, clonesBySource, ref mutated);
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
        Dictionary<uint, uint> clonesBySource,
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

            if (clonesBySource.ContainsKey(child.FormId))
            {
                throw new InvalidOperationException(
                    $"Door REFR 0x{child.FormId:X8} was captured in more than one new-worldspace cell; "
                    + "a reciprocal XTEL rewrite would be ambiguous.");
            }

            var emittedFormId = allocator.Allocate();
            clonesBySource.Add(child.FormId, emittedFormId);
            builder ??= SeedBuilder(bucket, i);
            builder.Add(child with
            {
                Disposition = RecordDisposition.New,
                FormId = emittedFormId,
                SourceFormId = child.FormId,
                Master = null,
                Provenance = new PlanProvenance
                {
                    PolicyId = ClonePolicyId,
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
            || !IsDoorBase(model.BaseFormId, masterRecordsByFormId)
            || !masterRecordsByFormId.TryGetValue(child.FormId, out var masterRef))
        {
            return false;
        }

        return !OverlapsMasterPlacement(child.FormId, model, cellPlan, masterContexts,
            masterRef, masterRefToCell);
    }

    private static bool IsDoorBase(
        uint baseFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId) =>
        masterRecordsByFormId.TryGetValue(baseFormId, out var baseRecord)
        && baseRecord.Header.Signature == "DOOR";

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
