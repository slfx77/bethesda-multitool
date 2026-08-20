using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;

/// <summary>
///     Builds the planner-side evidence used by the XESP census. It deliberately does not
///     alter emission: a link that currently validates against a phantom allocated FormID
///     is reported as captured-and-dropped even if the writer still writes its XESP.
/// </summary>
internal sealed class PlannerXespParentClassifier
{
    private readonly IReadOnlyDictionary<uint, Resolution> _index;
    private readonly IReadOnlyDictionary<uint, ParsedMainRecord> _masterByFormId;
    private readonly IReadOnlySet<uint> _masterRefFormIds;

    private readonly EmitPlan _plan;

    /// <summary>
    ///     Indexes every captured cell child once, then answers per-XESP queries from plan
    ///     data alone. Instance-based since 2026-08-12 (retirement Stage H4) so the
    ///     classification no longer reaches back into the writer's encode context — it is a
    ///     planner query that merely runs alongside the writer.
    /// </summary>
    public PlannerXespParentClassifier(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        IReadOnlySet<uint> masterRefFormIds,
        bool indexCapturedChildren = true)
    {
        _plan = plan;
        _masterByFormId = masterByFormId;
        _masterRefFormIds = masterRefFormIds;
        _index = indexCapturedChildren
            ? BuildIndex(plan, masterByFormId, masterRefFormIds)
            : new Dictionary<uint, Resolution>();
    }

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

    public Resolution Classify(uint sourceParentFormId)
    {
        if (_index.TryGetValue(sourceParentFormId, out var direct))
        {
            return direct;
        }

        if (_plan.SourceToEmittedFormId.TryGetValue(sourceParentFormId, out var remapped))
        {
            if (_index.TryGetValue(remapped, out var remappedResolution))
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

        if (_masterRefFormIds.Contains(sourceParentFormId)
            || (_masterByFormId.TryGetValue(sourceParentFormId, out var master)
                && master.Header.Signature is "REFR" or "ACHR" or "ACRE"))
        {
            return new Resolution(
                sourceParentFormId,
                PlannerXespParentStatus.LiveMaster,
                "master-ref");
        }

        if (_plan.EmittedFormIds.Contains(sourceParentFormId))
        {
            // A top-level record (not a cell child) the plan emits — e.g. an activator or
            // container base an XESP legitimately points at.
            return new Resolution(
                sourceParentFormId,
                PlannerXespParentStatus.LiveEmitted,
                "emitted-top-level");
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
            if (child.Type is not ("REFR" or "ACHR" or "ACRE" or "PGRE")
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

        // Every placed-ref child carries a verdict (the writer throws otherwise), so reaching
        // here means a non-placed child type (LAND/NAVM) or a KeepMaster/Skip disposition.
        return new Resolution(
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

    private static int Priority(PlannerXespParentStatus status)
    {
        return status switch
        {
            PlannerXespParentStatus.LiveEmitted => 3,
            PlannerXespParentStatus.CapturedDropped => 2,
            PlannerXespParentStatus.LiveMaster => 1,
            _ => 0
        };
    }

    internal sealed record Resolution(
        uint FinalParentFormId,
        PlannerXespParentStatus Status,
        string Reason);
}
