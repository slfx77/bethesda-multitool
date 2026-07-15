using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Builds the planner-side evidence used by the XESP census. It deliberately does not
///     alter emission: a link that currently validates against a phantom allocated FormID
///     is reported as captured-and-dropped even if the writer still writes its XESP.
/// </summary>
internal static class PlannerXespParentClassifier
{
    internal sealed record Resolution(
        uint FinalParentFormId,
        PlannerXespParentStatus Status,
        string Reason);

    public static IReadOnlyDictionary<uint, Resolution> BuildIndex(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlySet<uint> masterRefFormIds)
    {
        var result = new Dictionary<uint, Resolution>();

        foreach (var cell in plan.CellsByFormId.Values)
        {
            AddBucket(cell.PersistentChildren, cell, result);
            AddBucket(cell.VwdChildren, cell, result);
            AddBucket(cell.TemporaryChildren, cell, result);
        }

        // A captured override takes precedence: its planner verdict explains whether the
        // captured parent placement survives. Uncaptured master refs are live master data.
        foreach (var formId in masterRefFormIds)
        {
            result.TryAdd(formId, new Resolution(
                formId, PlannerXespParentStatus.LiveMaster, "master-ref"));
        }

        // Tests and reduced callers may omit MasterRecordIndex. Fall back to the parsed
        // record signature so their master REFR/ACHR/ACRE evidence is still classified.
        foreach (var (formId, record) in masterByFormId)
        {
            if (record.Header.Signature is "REFR" or "ACHR" or "ACRE")
            {
                result.TryAdd(formId, new Resolution(
                    formId, PlannerXespParentStatus.LiveMaster, "master-ref"));
            }
        }

        return result;
    }

    public static Resolution Classify(uint sourceParentFormId, CellChildEncodeContext context)
    {
        if (context.XespParentIndex.TryGetValue(sourceParentFormId, out var direct))
        {
            return direct;
        }

        if (context.Plan.SourceToEmittedFormId.TryGetValue(sourceParentFormId, out var remapped))
        {
            if (context.XespParentIndex.TryGetValue(remapped, out var remappedResolution))
            {
                return remappedResolution;
            }

            // Cell-child allocation proves this ID was captured, but absence from every
            // final cell bucket means the placement was removed before writer traversal.
            return new Resolution(
                remapped,
                PlannerXespParentStatus.CapturedDropped,
                "allocated-without-live-cell-child");
        }

        if (RuntimeStateRecordPolicy.IsRuntimeStateFormId(sourceParentFormId)
            || (sourceParentFormId & 0xFF000000u) == 0xFF000000u)
        {
            return new Resolution(
                sourceParentFormId,
                PlannerXespParentStatus.RuntimeState,
                "runtime-formid");
        }

        if (context.MasterRefFormIds.Contains(sourceParentFormId)
            || (context.MasterByFormId.TryGetValue(sourceParentFormId, out var master)
                && master.Header.Signature is "REFR" or "ACHR" or "ACRE"))
        {
            return new Resolution(
                sourceParentFormId,
                PlannerXespParentStatus.LiveMaster,
                "master-ref");
        }

        if (context.Plan.EmittedFormIds.Contains(sourceParentFormId))
        {
            // Transitional plans can expose a valid emitted ref without cell verdicts.
            return new Resolution(
                sourceParentFormId,
                PlannerXespParentStatus.LiveEmitted,
                "emit-set-fallback");
        }

        return new Resolution(
            sourceParentFormId,
            PlannerXespParentStatus.Absent,
            sourceParentFormId == 0 ? "zero-sentinel" : "absent-from-master-and-capture");
    }

    private static void AddBucket(
        IReadOnlyList<RecordPlan> children,
        CellPlan cell,
        Dictionary<uint, Resolution> result)
    {
        foreach (var child in children)
        {
            if (child.Type is not ("REFR" or "ACHR" or "ACRE")
                || child.Model is not PlacedReference placed)
            {
                continue;
            }

            var resolution = ClassifyCapturedChild(child, cell);
            AddBest(result, child.FormId, resolution);
            AddBest(result, child.SourceFormId ?? placed.FormId, resolution);
        }
    }

    private static Resolution ClassifyCapturedChild(RecordPlan child, CellPlan cell)
    {
        if (cell.CellRecordPlan.Disposition == RecordDisposition.Skip || cell.Emits == false)
        {
            return new Resolution(
                child.FormId,
                PlannerXespParentStatus.CapturedDropped,
                cell.SuppressReason ?? "containing-cell-suppressed");
        }

        if (cell.RefDecisions.TryGetValue(child.FormId, out var verdict))
        {
            return verdict.Verdict == PlacedRefEmitVerdict.Emit
                ? new Resolution(
                    child.FormId, PlannerXespParentStatus.LiveEmitted, "planner-emit")
                : new Resolution(
                    child.FormId,
                    PlannerXespParentStatus.CapturedDropped,
                    verdict.DropReason ?? "planner-drop");
        }

        return child.Disposition is RecordDisposition.New or RecordDisposition.Override
            ? new Resolution(
                child.FormId, PlannerXespParentStatus.LiveEmitted, "writer-fallback-emit")
            : new Resolution(
                child.FormId, PlannerXespParentStatus.CapturedDropped, "non-emitting-disposition");
    }

    private static void AddBest(
        Dictionary<uint, Resolution> result,
        uint formId,
        Resolution candidate)
    {
        if (formId == 0)
        {
            return;
        }

        if (!result.TryGetValue(formId, out var current)
            || Priority(candidate.Status) > Priority(current.Status))
        {
            result[formId] = candidate;
        }
    }

    private static int Priority(PlannerXespParentStatus status) => status switch
    {
        PlannerXespParentStatus.LiveEmitted => 3,
        PlannerXespParentStatus.CapturedDropped => 2,
        PlannerXespParentStatus.LiveMaster => 1,
        _ => 0,
    };
}
