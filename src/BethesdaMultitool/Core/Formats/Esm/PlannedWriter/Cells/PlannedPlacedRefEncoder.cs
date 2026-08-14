using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.World;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Placed-ref (REFR/ACHR/ACRE) encoding for the planner cell writer. Ports the legacy
///     per-ref filter chain from <c>PluginConversionPipeline</c>'s cell-children merge loop — runtime-state
///     skip, render-culling-marker drop, sparse-cell (PersistentOnly) preservation, parent-cell
///     mismatch guard, base remap-then-validate — plus the override-merge emission path
///     (<c>TryEncodeOverrideRef</c>: DMP live position merged onto master subrecords).
/// </summary>
internal static class PlannedPlacedRefEncoder
{
    internal const uint CompressedFlag = 0x00040000u;

    /// <summary>
    ///     Encode one placed-ref child plan. Returns null when the ref is dropped by policy
    ///     (counted via drop reasons). For overrides, <paramref name="routeGroupType" /> is
    ///     set to the master child-GRUP type so the record lands in master's original bucket.
    /// </summary>
    public static byte[]? Encode(
        RecordPlan child,
        CellChildEncodeContext context,
        CellEncodeState state,
        ref int routeGroupType)
    {
        if (child.Model is not PlacedReference placed)
        {
            return null;
        }

        // Every placed-ref child carries a planner-settled verdict: CellChildVerdictPlanner
        // walks the same buckets with the same type/model filters this writer does, and no
        // pass adds children after it runs. The transitional decision chain that used to sit
        // here was deleted in the 2026-08-11 retirement (Stage H1) after a tripwire build
        // confirmed zero production records reached it on xex21/xex44 — it had also silently
        // diverged (no StripStreetLightPolicy, no Reparented handling), so keeping it was a
        // latent hazard rather than a safety net.
        if (!state.RefDecisions.TryGetValue(child.FormId, out var verdict))
        {
            throw new InvalidOperationException(
                $"{child.Type} 0x{child.FormId:X8} reached the cell writer without a planner " +
                "verdict. CellChildVerdictPlanner and PlanCellSectionBuilder must walk the " +
                "same buckets with the same filters — one of them was widened without the other.");
        }

        return VerdictPlacedRefEncoder.Encode(child, placed, verdict, context, state, ref routeGroupType);
    }

    /// <summary>Record-header flag: ref is persistent (must be set on Persistent Children GRUP members).</summary>
    internal const uint PersistentFlag = 0x00000400u;
    internal const uint InitiallyDisabledFlag = 0x00000800u;

    /// <summary>Record-header flag: visible when distant (set on VWD Children GRUP members).</summary>
    internal const uint VisibleWhenDistantFlag = 0x00008000u;

    /// <summary>
    ///     XEMI (emittance link) is eager-resolved when the plugin is ESM-flagged and was a
    ///     known fault source in the ESM-era builds; the carry-forward path strips it via
    ///     ReconstructRecordBytes, so the merge path must too or overrides re-import it.
    /// </summary>
    internal static ParsedMainRecord StripXemi(ParsedMainRecord masterRecord) =>
        masterRecord.Subrecords.Any(s => s.Signature == "XEMI")
            ? masterRecord with
            {
                Subrecords = masterRecord.Subrecords.Where(s => s.Signature != "XEMI").ToList()
            }
            : masterRecord;

    /// <summary>
    ///     Surface the optional-link sanitation performed inside <see cref="RefrEncoder" />.
    ///     Recording both emitted and skipped links lets the v122 census distinguish a
    ///     live parent from an allocated-but-dropped capture, runtime ID, or honest data gap.
    /// </summary>
    internal static void RecordEnableParentOutcome(
        PlacedReference placed,
        IReadOnlyList<EncodedSubrecord> emittedSubrecords,
        CellChildEncodeContext context)
    {
        if (placed.EnableParentFormId is { } sourceParent)
        {
            RecordEnableParentOutcome(
                sourceParent,
                emittedSubrecords.Any(sub => sub.Signature == "XESP"),
                context);
        }
    }

    internal static void RecordEnableParentOutcome(
        uint sourceParentFormId,
        bool xespEmitted,
        CellChildEncodeContext context)
    {
        if (context.Stats is not { } stats)
        {
            return;
        }

        var resolution = context.XespClassifier.Classify(sourceParentFormId);
        stats.RecordPlannerXesp(
            sourceParentFormId,
            resolution.FinalParentFormId,
            xespEmitted,
            resolution.Status,
            resolution.Reason);
    }
}
