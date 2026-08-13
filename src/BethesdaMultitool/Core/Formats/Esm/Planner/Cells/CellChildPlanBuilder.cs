using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Builds the per-child <see cref="RecordPlan" /> set for one cell: placed refs bucketed
///     into Persistent / VWD / Temporary, plus the LAND and NAVM entries that lead the
///     Temporary bucket in vanilla file order (LAND, NAVMs, then placed refs).
///     <para>
///     Split out of <see cref="CellSectionPlanner" /> in 2026-08-12 (retirement Stage H2),
///     which was at the 500-line architecture ceiling.
///     </para>
/// </summary>
internal static class CellChildPlanBuilder
{
    /// <summary>
    ///     Walk the cell's DMP placed-refs + NAVMs and emit a <see cref="RecordPlan" />
    ///     per child, bucketed into Persistent (REFRs/ACHRs/ACREs/PGREs with IsPersistent),
    ///     VWD (currently always empty — the <c>PlacedReference</c> model doesn't expose
    ///     a VWD flag), and Temporary (everything else + LAND + NAVMs).
    ///     <para>
    ///     <c>DuplicateChildDrops</c> counts captures discarded because another capture in
    ///     the same cell already claimed their emitted FormID; the caller raises a plan
    ///     diagnostic so a silently-shrinking cell is visible.
    ///     </para>
    /// </summary>
    internal static (ImmutableArray<RecordPlan> Persistent,
        ImmutableArray<RecordPlan> Vwd,
        ImmutableArray<RecordPlan> Temporary,
        int DuplicateChildDrops) BuildChildPlans(
        CellCatalogEntry entry,
        IReadOnlyDictionary<uint, List<NavMeshRecord>> navmsByCell,
        IReadOnlyDictionary<uint, CellLandDecision> landDecisions,
        CellChildAllocator.AllocationResult allocations,
        IReadOnlySet<uint> masterFormIds)
    {
        var duplicateChildDrops = 0;
        if (entry.DmpModel is null)
        {
            return (ImmutableArray<RecordPlan>.Empty,
                ImmutableArray<RecordPlan>.Empty,
                ImmutableArray<RecordPlan>.Empty,
                duplicateChildDrops);
        }

        var persistent = ImmutableArray.CreateBuilder<RecordPlan>();
        var temporary = ImmutableArray.CreateBuilder<RecordPlan>();

        // File order inside Temporary Children is LAND, NAVM, then placed refs. Add LAND
        // first here; the writer preserves the planned prefix order.
        if (landDecisions.TryGetValue(entry.CellFormId, out var land)
            && allocations.LandByCellSourceToEmitted.TryGetValue(entry.CellFormId, out var landFormId))
        {
            temporary.Add(BuildLandPlan(land, landFormId));
        }

        if (navmsByCell.TryGetValue(entry.CellFormId, out var navms))
        {
            foreach (var navm in navms)
            {
                var plan = BuildNavmPlan(navm, allocations);
                if (plan is not null)
                {
                    temporary.Add(plan);
                }
            }
        }

        // One plan per emitted FormID. A multi-snapshot capture union can list the same
        // source ref twice in one cell; CellChildAllocator dedups the ID map, so both copies
        // resolve to the SAME emitted FormID and the writer would emit two records sharing
        // it — a duplicate-FormID file that the semantic validator rejects. Dropping the
        // second instance here also makes RefDecisions (keyed by FormID) unambiguous.
        var seenChildFormIds = new HashSet<uint>();
        foreach (var placed in entry.DmpModel.PlacedObjects)
        {
            var plan = BuildPlacedRefPlan(placed, allocations, masterFormIds);
            if (plan is null)
            {
                continue;
            }

            if (!seenChildFormIds.Add(plan.FormId))
            {
                duplicateChildDrops++;
                continue;
            }

            if (placed.IsPersistent)
            {
                persistent.Add(plan);
            }
            else
            {
                temporary.Add(plan);
            }
        }

        return (persistent.ToImmutable(), ImmutableArray<RecordPlan>.Empty, temporary.ToImmutable(),
            duplicateChildDrops);
    }

    private static RecordPlan? BuildPlacedRefPlan(
        Models.World.PlacedReference placed,
        CellChildAllocator.AllocationResult allocations,
        IReadOnlySet<uint> masterFormIds)
    {
        if (placed.RecordType is not ("REFR" or "ACHR" or "ACRE" or "PGRE") || placed.FormId == 0)
        {
            return null;
        }

        var inMaster = masterFormIds.Contains(placed.FormId);
        var allocated = allocations.PlacedRefSourceToEmitted.TryGetValue(placed.FormId, out var emit);
        if (!inMaster && !allocated)
        {
            return null; // Runtime-state ref or otherwise filtered.
        }

        var disposition = inMaster ? RecordDisposition.Override : RecordDisposition.New;
        var emitFormId = inMaster ? placed.FormId : emit;

        return new RecordPlan
        {
            Type = placed.RecordType,
            Disposition = disposition,
            FormId = emitFormId,
            SourceFormId = placed.FormId,
            Model = placed,
            Master = null,
            References = ImmutableArray<ResolvedRef>.Empty,
            OverrideSubrecords = null,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance
            {
                PolicyId = "CellSectionPlanner.PlacedRef." + disposition,
                Reason = inMaster
                    ? "DMP captured a placed ref sharing FormID with master; emit override."
                    : "DMP captured a placed ref without master counterpart; allocated plugin FormID.",
            },
        };
    }

    private static RecordPlan BuildLandPlan(CellLandDecision land, uint landFormId) => new()
    {
        Type = "LAND",
        Disposition = land.MasterLandFormId is null ? RecordDisposition.New : RecordDisposition.Override,
        FormId = landFormId,
        SourceFormId = land.MasterLandFormId,
        Model = land,
        Master = null,
        References = ImmutableArray<ResolvedRef>.Empty,
        OverrideSubrecords = null,
        ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
        Provenance = new PlanProvenance
        {
            PolicyId = land.MasterLandFormId is null ? "CellLandPlanner.New" : "CellLandPlanner.Override",
            Reason = land.MasterLandFormId is { } masterLand
                ? $"DMP capture for master CELL 0x{land.CellSourceFormId:X8} has complete {land.HeightSource} terrain; overrides master LAND 0x{masterLand:X8}."
                : $"DMP-new exterior CELL 0x{land.CellSourceFormId:X8} has {land.HeightSource} terrain.",
        },
    };

    private static RecordPlan? BuildNavmPlan(
        NavMeshRecord navm,
        CellChildAllocator.AllocationResult allocations)
    {
        if (!allocations.NavmSourceToEmitted.TryGetValue(navm.FormId, out var emit))
        {
            return null; // Master-resident NAVM or otherwise filtered.
        }

        return new RecordPlan
        {
            Type = "NAVM",
            Disposition = RecordDisposition.New,
            FormId = emit,
            SourceFormId = navm.FormId,
            Model = navm,
            Master = null,
            References = ImmutableArray<ResolvedRef>.Empty,
            OverrideSubrecords = null,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance
            {
                PolicyId = "CellSectionPlanner.Navm.New",
                Reason = "DMP captured a NAVM without master counterpart; allocated plugin FormID.",
            },
        };
    }
}
