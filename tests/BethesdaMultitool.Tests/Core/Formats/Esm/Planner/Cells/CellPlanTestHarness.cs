using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

/// <summary>
///     Brings a hand-built <see cref="EmitPlan" /> up to the state the production planner
///     guarantees before the writer ever sees it: every cell has a settled merge mode, and
///     every placed-ref child has a settled verdict.
///     <para>
///     Retirement Stage H1 (2026-08-11) deleted the writer-side fallbacks that used to
///     re-derive both — <c>CellDecisionFallback</c> and <c>PlannedPlacedRefEncoder</c>'s
///     transitional decision chain. Those fallbacks were what let fixtures skip planning and
///     still emit, so fixtures now run the real passes instead. That is strictly better
///     coverage: the decisions under test are the ones production actually makes.
///     </para>
/// </summary>
internal static class CellPlanTestHarness
{
    /// <summary>
    ///     Overload for fixtures that carry no <see cref="MasterRecordIndex" />: synthesizes a
    ///     minimal one from the master dictionary (no child locations, so every master record
    ///     is treated as a non-child).
    /// </summary>
    internal static EmitPlan Settle(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        PluginBuildOptions? options = null)
    {
        var index = new MasterRecordIndex
        {
            Records = masterByFormId.Values.ToList(),
            RecordsByFormId = masterByFormId.ToDictionary(kv => kv.Key, kv => kv.Value),
            FormIds = [.. masterByFormId.Keys],
            FormIdsByType = [],
            EditorIdToFormIdByType = [],
            StemToFormIdsByType = [],
            ChildLocations = [],
            RefToCell = [],
            RefsByCell = [],
            NavmsByCell = [],
            LandsByCell = [],
            CellContexts = []
        };
        return Settle(plan, masterByFormId, index, options);
    }

    /// <summary>
    ///     Settle merge modes (mirroring <c>CellSectionPlanner.PlanMergeMode</c>) and then run
    ///     the real <see cref="CellChildVerdictPlanner" /> over the plan.
    /// </summary>
    internal static EmitPlan Settle(
        EmitPlan plan,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterByFormId,
        MasterRecordIndex masterIndex,
        PluginBuildOptions? options = null,
        bool recoverLeveledSpawnActors = true,
        IReadOnlyDictionary<uint, string>? dmpBaseTypes = null)
    {
        options ??= new PluginBuildOptions();
        var masterRefFormIds = new HashSet<uint>(masterIndex.RefToCell.Keys);

        var withModes = ImmutableDictionary.CreateBuilder<uint, CellPlan>();
        foreach (var (formId, cellPlan) in plan.CellsByFormId)
        {
            withModes[formId] = cellPlan.Mode is not null
                ? cellPlan
                : cellPlan with
                {
                    Mode = ResolveMode(cellPlan, masterRefFormIds),
                    DropRenderCullingMarkers = DropMarkers(cellPlan, masterRefFormIds, options),
                };
        }

        var preset = withModes
            .Where(kv => !kv.Value.RefDecisions.IsEmpty)
            .ToDictionary(kv => kv.Key, kv => kv.Value.RefDecisions);

        var settled = CellChildVerdictPlanner.Apply(
            withModes.ToImmutable(),
            masterByFormId,
            plan.SourceToEmittedFormId,
            plan.EmittedFormIds,
            new CellVerdictInputs
            {
                MasterIndex = masterIndex,
                RecoverLeveledSpawnActors = recoverLeveledSpawnActors,
                DmpBaseTypes = dmpBaseTypes,
                EnableRefrBaseEditorIdRemap = options.EnableRefrBaseEditorIdRemap,
                DiagnosticSkipCellNewRefs = options.DiagnosticSkipCellNewRefs,
            });

        // A fixture that hand-authored verdicts is asserting on those exact decisions; keep
        // them rather than letting the planner pass overwrite what the test is pinning.
        foreach (var (formId, decisions) in preset)
        {
            settled = settled.SetItem(formId, settled[formId] with { RefDecisions = decisions });
        }

        // Retirement Stage H6 (2026-08-12): the closing cell phases settle every outgoing link
        // and the navmesh connectivity graph. The writer now reads those decisions instead of
        // resolving FormIDs while encoding, so a fixture that skipped them would hand the
        // encoder a subrecord it has no verdict for.
        return CellPlanFinalizer.Apply(
            plan with
            {
                CellsByFormId = settled,
                EmittedNavmFormIds = PlanNavmEmission.Compute(settled, options.DiagnosticSkipCellNavm),
            },
            masterByFormId);
    }

    private static CellMergeMode ResolveMode(CellPlan cellPlan, IReadOnlySet<uint> masterRefFormIds)
    {
        if (cellPlan.CellRecordPlan.Master is null)
        {
            return CellMergeMode.LoadedReplacement; // Brand-new cells own all their children.
        }

        return cellPlan.CellRecordPlan.Model is CellRecord dmpCell
            ? CellMerger.Classify(dmpCell, masterRefFormIds)
            : CellMergeMode.Skip;
    }

    private static bool DropMarkers(
        CellPlan cellPlan, IReadOnlySet<uint> masterRefFormIds, PluginBuildOptions options)
    {
        return cellPlan.CellRecordPlan.Master is not null
               && cellPlan.Context.IsInterior
               && ResolveMode(cellPlan, masterRefFormIds) == CellMergeMode.LoadedReplacement
               && !options.ReplaceCellTemporariesOnOverride;
    }
}
