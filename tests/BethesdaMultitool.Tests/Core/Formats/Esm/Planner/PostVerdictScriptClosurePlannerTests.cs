using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class PostVerdictScriptClosurePlannerTests
{
    [Fact]
    public void Apply_SuppressesScriptWhoseAllocatedScroTargetWasDroppedByCellVerdict()
    {
        const uint scriptSource = 0x00100000;
        const uint scriptEmitted = 0x01000800;
        const uint refSource = 0x00100001;
        const uint refEmitted = 0x01000801;
        const uint cell = 0x01000802;
        var script = Plan("SCPT", scriptEmitted, scriptSource) with
        {
            Model = new ScriptRecord
            {
                FormId = scriptSource,
                EditorId = "DroppedTargetScript",
            },
            References =
            [
                new ResolvedRef
                {
                    FieldPath = "SCRO[0]", OriginalFormId = refSource,
                    Action = ResolvedRefAction.Resolved, FinalFormId = refEmitted,
                },
            ],
        };
        var child = Plan("REFR", refEmitted, refSource);
        var anchor = Plan("CELL", cell, cell);
        var cellPlan = new CellPlan
        {
            CellFormId = cell,
            CellRecordPlan = anchor,
            Context = new PcEsmCellContext { CellFormId = cell, IsInterior = true },
            PersistentChildren = ImmutableArray<RecordPlan>.Empty,
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = [child],
            Emits = true,
            RefDecisions = ImmutableDictionary<uint, PlacedRefDecision>.Empty.Add(
                refEmitted,
                new PlacedRefDecision { Verdict = PlacedRefEmitVerdict.Drop }),
        };
        var plan = new EmitPlan
        {
            Records = [script],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(scriptSource, scriptEmitted)
                .Add(refSource, refEmitted),
            EmittedFormIds = ImmutableHashSet<uint>.Empty
                .Add(scriptEmitted).Add(refEmitted).Add(cell),
            ValidScriptFormIds = ImmutableHashSet<uint>.Empty.Add(scriptEmitted),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(scriptEmitted, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x803,
                PlannerCoverage = ImmutableHashSet<string>.Empty,
            },
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(cell, cellPlan),
        };

        var result = PostVerdictScriptClosurePlanner.Apply(plan, []);

        Assert.Empty(result.Records);
        Assert.DoesNotContain(scriptEmitted, result.EmittedFormIds);
        Assert.DoesNotContain(refEmitted, result.EmittedFormIds);
        Assert.DoesNotContain(scriptSource, result.SourceToEmittedFormId.Keys);
        Assert.DoesNotContain(refSource, result.SourceToEmittedFormId.Keys);
        Assert.Empty(result.ValidScriptFormIds);
        var diagnostic = Assert.Single(result.Diagnostics, diagnostic =>
            diagnostic.Code == "script.suppress-post-verdict-reference-table"
            && diagnostic.Message.Contains(
                "[script-source=0x00100000;script-emitted=0x01000800;script-edid=DroppedTargetScript]",
                StringComparison.Ordinal)
            && diagnostic.Message.Contains(
                "SCRO[0][target-source=0x00100001;target-emitted=0x01000801;action=Resolved] target was dropped",
                StringComparison.Ordinal));
        Assert.Equal("DroppedTargetScript", diagnostic.Metadata!["script-editor-id"]);
        Assert.Equal("0x00100000", diagnostic.Metadata["script-source-form-id"]);
        Assert.Equal("0x01000800", diagnostic.Metadata["script-emitted-form-id"]);
        Assert.Equal("SCRO[0]", diagnostic.Metadata["reference-field"]);
        Assert.Equal("Resolved", diagnostic.Metadata["reference-action"]);
        Assert.Equal("0x00100001", diagnostic.Metadata["target-source-form-id"]);
        Assert.Equal("0x01000801", diagnostic.Metadata["target-emitted-form-id"]);
    }

    [Fact]
    public void Apply_DuplicateCaptureFormIdsKeepLastPointLookupLikeInitialPlan()
    {
        const uint duplicateFormId = 0x01002752;
        var plan = new EmitPlan
        {
            Records =
            [
                Plan("DIAL", duplicateFormId, 0x00100001),
                Plan("DIAL", duplicateFormId, 0x00100002),
            ],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(0x00100001, duplicateFormId)
                .Add(0x00100002, duplicateFormId),
            EmittedFormIds = ImmutableHashSet<uint>.Empty.Add(duplicateFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(duplicateFormId, 1),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty,
            },
        };

        var result = PostVerdictScriptClosurePlanner.Apply(plan, []);

        Assert.Equal(2, result.Records.Length);
        Assert.Equal(1, result.RecordIndexByEmittedFormId[duplicateFormId]);
    }

    [Fact]
    public void Apply_SkipScriptIsNeitherActuallyLiveNorAValidScriTarget()
    {
        const uint source = 0x00100000;
        const uint emitted = 0x01000800;
        var skipped = Plan("SCPT", emitted, source) with
        {
            Disposition = RecordDisposition.Skip,
            Model = new ScriptRecord { FormId = source },
        };
        var plan = new EmitPlan
        {
            Records = [skipped],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty.Add(source, emitted),
            EmittedFormIds = ImmutableHashSet<uint>.Empty.Add(emitted),
            ValidScriptFormIds = ImmutableHashSet<uint>.Empty.Add(emitted),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(emitted, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x801,
                PlannerCoverage = ImmutableHashSet<string>.Empty,
            },
        };

        var result = PostVerdictScriptClosurePlanner.Apply(plan, []);

        Assert.DoesNotContain(emitted, result.EmittedFormIds);
        Assert.DoesNotContain(emitted, result.ValidScriptFormIds);
        Assert.DoesNotContain(source, result.SourceToEmittedFormId.Keys);
    }

    private static RecordPlan Plan(string type, uint emitted, uint source) => new()
    {
        Type = type,
        Disposition = RecordDisposition.New,
        FormId = emitted,
        SourceFormId = source,
        References = ImmutableArray<ResolvedRef>.Empty,
        ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
        Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" },
    };
}
