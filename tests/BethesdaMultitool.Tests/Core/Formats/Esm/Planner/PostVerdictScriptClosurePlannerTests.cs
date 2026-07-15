using System.Collections.Immutable;
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
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.Code == "script.suppress-post-verdict-reference-table"
            && diagnostic.Message.Contains("SCRO[0]=0x01000801 was dropped", StringComparison.Ordinal));
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
