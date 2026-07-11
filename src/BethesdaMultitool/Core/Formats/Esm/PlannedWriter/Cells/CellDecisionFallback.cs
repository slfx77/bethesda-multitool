using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Transitional bridge for the writer→planner decision migration: prefers the
///     planner-settled decisions on <see cref="CellPlan" /> (Mode, render-culling-marker
///     policy) and falls back to computing them writer-side when the plan predates the
///     mode-planning stage (older test fixtures, partial planner runs). Delete this class
///     once every producer populates <see cref="CellPlan.Mode" />.
/// </summary>
internal static class CellDecisionFallback
{
    /// <summary>
    ///     Resolve the cell's merge mode + marker-drop policy. Planned values win; the
    ///     fallback mirrors legacy: master cells classify via <see cref="CellMerger.Classify" />
    ///     (any non-persistent master-resident capture ⇒ LoadedReplacement; persistent-only ⇒
    ///     PersistentOnly); brand-new cells emit all children (LoadedReplacement semantics).
    /// </summary>
    public static (CellMergeMode Mode, bool DropRenderCullingMarkers) Resolve(
        CellPlan cellPlan,
        bool isMasterAnchored,
        CellRecord? dmpCell,
        CellChildEncodeContext context)
    {
        if (cellPlan.Mode is { } plannedMode)
        {
            return (plannedMode, cellPlan.DropRenderCullingMarkers);
        }

        var mode = ResolveMergeMode(isMasterAnchored, dmpCell, context);

        // Replaced interiors drop captured render-culling markers (their final-game bounds
        // mis-cull the proto layout) unless the diagnostic replace-temporaries mode is on.
        var dropRenderCullingMarkers = isMasterAnchored
            && cellPlan.Context.IsInterior
            && mode == CellMergeMode.LoadedReplacement
            && !context.Options.ReplaceCellTemporariesOnOverride;

        return (mode, dropRenderCullingMarkers);
    }

    private static CellMergeMode ResolveMergeMode(
        bool isMasterAnchored, CellRecord? dmpCell, CellChildEncodeContext context)
    {
        if (!isMasterAnchored)
        {
            return CellMergeMode.LoadedReplacement;
        }

        if (dmpCell is null || context.MasterIndex is null)
        {
            return CellMergeMode.Skip;
        }

        return CellMerger.Classify(dmpCell, context.MasterRefFormIds);
    }
}
