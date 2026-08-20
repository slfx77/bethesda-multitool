using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Container-cell plan shapes used by <see cref="PersistentCellReparenting" /> when a
///     worldspace needs a persistent-container plan to host re-homed refs: an Override onto the
///     master's own container, or a minimal NEW container for plugin-new worldspaces without one.
/// </summary>
internal static class PersistentContainerPlans
{
    /// <summary>
    ///     Override plan for a master worldspace's persistent-container cell that had no
    ///     captured plan of its own — hosts only the moved children (v113 shape).
    /// </summary>
    internal static CellPlan BuildMasterContainerOverridePlan(
        uint containerFormId,
        ParsedMainRecord masterCell,
        PcEsmCellContext containerContext,
        List<RecordPlan> children)
    {
        return new CellPlan
        {
            CellFormId = containerFormId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.Override,
                FormId = containerFormId,
                Model = null,
                Master = masterCell,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance
                {
                    PolicyId = "PersistentCellReparenting",
                    Reason = "Container override created to host re-parented exterior persistent refs + map markers."
                }
            },
            Context = containerContext,
            PersistentChildren = [.. children],
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            ParentWorldspaceFormId = containerContext.WorldspaceFormId,
            // Persistent-only content by construction; carry-forward skips master's own
            // GroupType-8 children, so this override never bloats with master persistents.
            Mode = CellMergeMode.PersistentOnly,
            DropRenderCullingMarkers = false
        };
    }

    /// <summary>
    ///     Minimal NEW persistent-container cell for a worldspace with neither a master nor
    ///     a captured proto container (matches the CK's behavior when a worldspace gains its
    ///     first persistent ref): no EditorId, exterior, no water claim.
    /// </summary>
    internal static CellPlan BuildNewContainerPlan(
        uint containerFormId,
        uint worldspaceFormId,
        List<RecordPlan> children)
    {
        return new CellPlan
        {
            CellFormId = containerFormId,
            CellRecordPlan = new RecordPlan
            {
                Type = "CELL",
                Disposition = RecordDisposition.New,
                FormId = containerFormId,
                Model = new CellRecord
                {
                    FormId = containerFormId,
                    WorldspaceFormId = worldspaceFormId,
                    IsPersistentCell = true
                },
                Master = null,
                References = ImmutableArray<ResolvedRef>.Empty,
                ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
                Provenance = new PlanProvenance
                {
                    PolicyId = "PersistentCellReparenting",
                    Reason = "New persistent-container cell synthesized for a worldspace without one."
                }
            },
            Context = new PcEsmCellContext
            {
                CellFormId = containerFormId,
                IsInterior = false,
                WorldspaceFormId = worldspaceFormId,
                BlockGroupType = 0,
                SubblockGroupType = 0,
                BlockLabel = null,
                SubblockLabel = null
            },
            PersistentChildren = [.. children],
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            ParentWorldspaceFormId = worldspaceFormId,
            Mode = CellMergeMode.LoadedReplacement,
            DropRenderCullingMarkers = false
        };
    }
}
