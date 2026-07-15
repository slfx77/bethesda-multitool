using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Core.Formats.Esm.Planner.References;

/// <summary>
///     Reasserts script-reference closure after cell-child verdicts. Initial reference
///     resolution must see every allocation so a potentially valid child can be planned;
///     the verdict pass can subsequently drop that child. A new SCPT may not keep an SCRO
///     to such an allocation because FNV disables the entire script at load time.
/// </summary>
internal static class PostVerdictScriptClosurePlanner
{
    public static EmitPlan Apply(EmitPlan plan, IReadOnlyList<ParsedMainRecord> masterRecords)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(masterRecords);

        var surviving = plan.Records;
        var diagnostics = ImmutableArray.CreateBuilder<PlanDiagnostic>();
        while (true)
        {
            var live = BuildActuallyLiveFormIds(plan, masterRecords, surviving);
            var unsafeScripts = FindUnsafeScripts(surviving, live);
            if (unsafeScripts.Count == 0)
            {
                return RebuildPlan(plan, masterRecords, surviving, live, diagnostics.ToImmutable());
            }

            foreach (var (script, issues) in unsafeScripts.Values)
            {
                diagnostics.Add(new PlanDiagnostic
                {
                    Kind = PlanDiagnosticKind.Warning,
                    Phase = "References",
                    Code = "script.suppress-post-verdict-reference-table",
                    RecordType = "SCPT",
                    FormId = script.FormId,
                    Message = $"Suppressed new SCPT 0x{script.FormId:X8} after final cell verdicts: "
                              + string.Join(", ", issues) + ".",
                });
            }

            surviving = surviving
                .Where(record => !unsafeScripts.ContainsKey(record.FormId))
                .ToImmutableArray();
        }
    }

    private static Dictionary<uint, (RecordPlan Script, List<string> Issues)> FindUnsafeScripts(
        ImmutableArray<RecordPlan> records,
        IReadOnlySet<uint> liveFormIds)
    {
        var unsafeScripts = new Dictionary<uint, (RecordPlan, List<string>)>();
        foreach (var record in records)
        {
            if (record.Type != "SCPT" || record.Disposition != RecordDisposition.New)
            {
                continue;
            }

            var issues = record.References
                .Where(reference => reference.FieldPath.StartsWith("SCRO[", StringComparison.Ordinal))
                .Where(reference => reference.Action != ResolvedRefAction.Resolved
                                    || reference.FinalFormId is null or 0u
                                    || !liveFormIds.Contains(reference.FinalFormId.Value))
                .Select(reference => reference.Action == ResolvedRefAction.Resolved
                    ? $"{reference.FieldPath}=0x{reference.FinalFormId ?? 0:X8} was dropped"
                    : $"{reference.FieldPath}=0x{reference.OriginalFormId ?? 0:X8} is unresolved")
                .ToList();
            if (issues.Count > 0)
            {
                unsafeScripts[record.FormId] = (record, issues);
            }
        }

        return unsafeScripts;
    }

    private static ImmutableHashSet<uint> BuildActuallyLiveFormIds(
        EmitPlan plan,
        IReadOnlyList<ParsedMainRecord> masterRecords,
        ImmutableArray<RecordPlan> topLevelRecords)
    {
        var live = masterRecords.Select(record => record.Header.FormId).ToHashSet();
        live.UnionWith(RuntimeStateRecordPolicy.EngineFormIds);
        live.UnionWith(topLevelRecords.Select(record => record.FormId));

        foreach (var worldspace in plan.WorldspacesByFormId.Values)
        {
            if (worldspace.WorldspaceRecordPlan.Disposition != RecordDisposition.Skip)
            {
                live.Add(worldspace.WorldspaceFormId);
            }
        }

        foreach (var cell in plan.CellsByFormId.Values)
        {
            if (cell.Emits == false)
            {
                continue;
            }

            if (cell.CellRecordPlan.Disposition != RecordDisposition.Skip)
            {
                live.Add(cell.CellFormId);
            }

            AddLiveChildren(cell, cell.PersistentChildren, live);
            AddLiveChildren(cell, cell.VwdChildren, live);
            AddLiveChildren(cell, cell.TemporaryChildren, live);
        }

        return live.ToImmutableHashSet();
    }

    private static void AddLiveChildren(
        CellPlan cell,
        ImmutableArray<RecordPlan> children,
        HashSet<uint> live)
    {
        foreach (var child in children)
        {
            // Match PlanCellSectionBuilder.EncodeBucketChildren exactly. Other catalogued
            // child types (notably captured PGREs) are not yet serialized by that writer
            // and therefore cannot make an SCRO target live merely by being allocated.
            if (child.Type is not ("LAND" or "NAVM" or "REFR" or "ACHR" or "ACRE")
                || child.Disposition == RecordDisposition.Skip
                || child.Type == "NAVM" && cell.NavmOnlySuppressed)
            {
                continue;
            }

            if (child.Type is "REFR" or "ACHR" or "ACRE"
                && (!cell.RefDecisions.TryGetValue(child.FormId, out var verdict)
                    || verdict.Verdict != PlacedRefEmitVerdict.Emit))
            {
                continue;
            }

            live.Add(child.FormId);
        }
    }

    private static EmitPlan RebuildPlan(
        EmitPlan plan,
        IReadOnlyList<ParsedMainRecord> masterRecords,
        ImmutableArray<RecordPlan> records,
        ImmutableHashSet<uint> liveFormIds,
        ImmutableArray<PlanDiagnostic> diagnostics)
    {
        var aliases = plan.SourceToEmittedFormId
            .Where(pair => liveFormIds.Contains(pair.Value))
            .ToImmutableDictionary();
        var index = records
            .Select((record, position) => (record.FormId, position))
            .ToImmutableDictionary(pair => pair.FormId, pair => pair.position);
        return plan with
        {
            Records = records,
            SourceToEmittedFormId = aliases,
            EmittedFormIds = liveFormIds,
            ValidScriptFormIds = ScriptReferenceSafetyPlanner.BuildValidScriptFormIds(
                masterRecords, records),
            RecordIndexByEmittedFormId = index,
            Diagnostics = plan.Diagnostics.AddRange(diagnostics),
        };
    }
}
