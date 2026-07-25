using System.Buffers.Binary;
using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter.Cells;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Cells;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Cells;

public sealed class PlannerXespObservabilityTests
{
    [Fact]
    public void Override_Sanitation_Records_Skipped_Xesp()
    {
        const uint missingParent = 0xAA00DEAD;
        var xesp = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(xesp, missingParent);
        var stats = new ConversionPipelineStats();
        var context = MakeContext(MakeEmptyPlan(), stats);

        var sanitized = PlannedPlacedRefEncoder.SanitizeOverrideSubrecords(
            [new EncodedSubrecord("XESP", xesp)], context);

        Assert.DoesNotContain(sanitized, subrecord => subrecord.Signature == "XESP");
        Assert.Equal(1, stats.PlannerXespObserved);
        Assert.Equal(0, stats.PlannerXespEmitted);
        Assert.Equal(1, stats.PlannerXespSkipped);
        var observation = Assert.Single(stats.PlannerXespObservations);
        Assert.False(observation.XespEmitted);
        Assert.Equal(missingParent, observation.SourceParentFormId);
        Assert.Equal(missingParent, observation.FinalParentFormId);
        Assert.Equal(PlannerXespParentStatus.Absent, observation.ParentStatus);
        Assert.Equal("absent-from-master-and-capture", observation.Reason);
    }

    [Theory]
    [InlineData(PlacedRefEmitVerdict.Emit, PlannerXespParentStatus.LiveEmitted, "planner-emit")]
    [InlineData(PlacedRefEmitVerdict.Drop, PlannerXespParentStatus.CapturedDropped,
        "refr.diagnostic-skip-new")]
    public void Captured_Parent_Uses_Planner_Verdict(
        PlacedRefEmitVerdict verdict,
        PlannerXespParentStatus expectedStatus,
        string expectedReason)
    {
        const uint sourceParent = 0xAA001234;
        const uint emittedParent = 0x01000802;
        var plan = MakePlanWithCapturedParent(sourceParent, emittedParent, verdict);
        var stats = new ConversionPipelineStats();
        var context = MakeContext(plan, stats);

        // The emitted=true/drop-verdict case captures the current phantom-valid-set shape:
        // observability must flag the parent even if sanitation still writes the XESP.
        PlannedPlacedRefEncoder.RecordEnableParentOutcome(sourceParent, true, context);

        var observation = Assert.Single(stats.PlannerXespObservations);
        Assert.True(observation.XespEmitted);
        Assert.Equal(sourceParent, observation.SourceParentFormId);
        Assert.Equal(emittedParent, observation.FinalParentFormId);
        Assert.Equal(expectedStatus, observation.ParentStatus);
        Assert.Equal(expectedReason, observation.Reason);
    }

    [Theory]
    [InlineData(0xFF001234u, PlannerXespParentStatus.RuntimeState, "runtime-formid")]
    [InlineData(0xAA001234u, PlannerXespParentStatus.Absent, "absent-from-master-and-capture")]
    public void Missing_Parent_Distinguishes_Runtime_From_Absent(
        uint sourceParent,
        PlannerXespParentStatus expectedStatus,
        string expectedReason)
    {
        var stats = new ConversionPipelineStats();
        var context = MakeContext(MakeEmptyPlan(), stats);

        PlannedPlacedRefEncoder.RecordEnableParentOutcome(sourceParent, false, context);

        var observation = Assert.Single(stats.PlannerXespObservations);
        Assert.False(observation.XespEmitted);
        Assert.Equal(expectedStatus, observation.ParentStatus);
        Assert.Equal(expectedReason, observation.Reason);
        Assert.Equal([sourceParent], stats.PlannerXespMissingParentFormIds);
    }

    private static CellChildEncodeContext MakeContext(
        EmitPlan plan,
        ConversionPipelineStats stats)
    {
        var masterByFormId = new Dictionary<uint, ParsedMainRecord>();
        var masterRefFormIds = new HashSet<uint>();
        var index = PlannerXespParentClassifier.BuildIndex(
            plan, masterByFormId, masterRefFormIds);
        return new CellChildEncodeContext(
            plan,
            masterByFormId,
            [.. plan.EmittedFormIds],
            new PluginBuildOptions(),
            stats,
            null,
            masterRefFormIds,
            null,
            index);
    }

    private static EmitPlan MakePlanWithCapturedParent(
        uint sourceParent,
        uint emittedParent,
        PlacedRefEmitVerdict verdict)
    {
        const uint cellFormId = 0x000ABCDE;
        var parent = new PlacedReference
        {
            FormId = sourceParent,
            BaseFormId = 0x0000001F,
            RecordType = "REFR",
            IsPersistent = true
        };
        var parentPlan = MakeRecordPlan(
            "REFR", RecordDisposition.New, emittedParent, parent, sourceParent);
        var decision = verdict == PlacedRefEmitVerdict.Emit
            ? new PlacedRefDecision
            {
                Verdict = verdict,
                FinalBaseFormId = parent.BaseFormId,
                TargetGroupType = 8
            }
            : new PlacedRefDecision
            {
                Verdict = verdict,
                DropReason = "refr.diagnostic-skip-new"
            };
        var cellPlan = new CellPlan
        {
            CellFormId = cellFormId,
            CellRecordPlan = MakeRecordPlan(
                "CELL", RecordDisposition.Override, cellFormId, null),
            Context = new PcEsmCellContext
            {
                CellFormId = cellFormId,
                IsInterior = true
            },
            PersistentChildren = [parentPlan],
            VwdChildren = ImmutableArray<RecordPlan>.Empty,
            TemporaryChildren = ImmutableArray<RecordPlan>.Empty,
            Emits = true,
            RefDecisions = ImmutableDictionary<uint, PlacedRefDecision>.Empty
                .Add(emittedParent, decision)
        };

        return MakeEmptyPlan() with
        {
            CellsByFormId = ImmutableDictionary<uint, CellPlan>.Empty.Add(cellFormId, cellPlan),
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(sourceParent, emittedParent),
            EmittedFormIds = ImmutableHashSet.Create(emittedParent)
        };
    }

    private static RecordPlan MakeRecordPlan(
        string type,
        RecordDisposition disposition,
        uint formId,
        object? model,
        uint? sourceFormId = null)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = disposition,
            FormId = formId,
            SourceFormId = sourceFormId,
            Model = model,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static EmitPlan MakeEmptyPlan()
    {
        return new EmitPlan
        {
            Records = ImmutableArray<RecordPlan>.Empty,
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty,
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x800,
                PlannerCoverage = ImmutableHashSet<string>.Empty
            }
        };
    }
}