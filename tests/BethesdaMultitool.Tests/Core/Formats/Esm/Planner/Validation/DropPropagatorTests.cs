using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Validation;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.Validation;

public sealed class DropPropagatorTests
{
    [Fact]
    public void Propagate_PreservesReferenceToSkippedEngineOwnedFormId()
    {
        const uint gameHour = 0x00000038;
        var records = ImmutableArray.Create(
            Plan("GLOB", gameHour, RecordDisposition.Skip),
            Plan("SCPT", 0x01000800, RecordDisposition.New) with
            {
                References = [Resolved("SCRO[0]", gameHour)]
            });

        var result = DropPropagator.Propagate(records, RuntimeStateRecordPolicy.EngineFormIds);

        var reference = Assert.Single(result[1].References);
        Assert.Equal(ResolvedRefAction.Resolved, reference.Action);
        Assert.Equal(gameHour, reference.FinalFormId);
    }

    [Fact]
    public void Propagate_DropsReferenceToOrdinarySkippedRecord()
    {
        const uint skippedTarget = 0x01000801;
        var records = ImmutableArray.Create(
            Plan("IMAD", skippedTarget, RecordDisposition.Skip),
            Plan("SCPT", 0x01000800, RecordDisposition.New) with
            {
                References = [Resolved("SCRO[0]", skippedTarget)]
            });

        var result = DropPropagator.Propagate(records, RuntimeStateRecordPolicy.EngineFormIds);

        var reference = Assert.Single(result[1].References);
        Assert.Equal(ResolvedRefAction.DropSubrecord, reference.Action);
        Assert.Null(reference.FinalFormId);
        Assert.Contains("Skip-dispositioned upstream", reference.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_FiltersCapturedEngineRecordButKeepsDependentReferenceResolved()
    {
        const uint gameDaysPassed = 0x00000039;
        const uint script = 0x01000802;
        var records = ImmutableArray.Create(
            Plan("GLOB", gameDaysPassed, RecordDisposition.Skip),
            Plan("SCPT", script, RecordDisposition.New) with
            {
                References = [Resolved("SCRO[0]", gameDaysPassed)]
            });

        var (validated, diagnostics) = PlanValidator.Validate(
            records, RuntimeStateRecordPolicy.EngineFormIds);

        Assert.Empty(diagnostics);
        var survivingScript = Assert.Single(validated);
        Assert.Equal(script, survivingScript.FormId);
        var reference = Assert.Single(survivingScript.References);
        Assert.Equal(ResolvedRefAction.Resolved, reference.Action);
        Assert.Equal(gameDaysPassed, reference.FinalFormId);
    }

    private static RecordPlan Plan(string type, uint formId, RecordDisposition disposition)
    {
        return new RecordPlan
        {
            Type = type,
            Disposition = disposition,
            FormId = formId,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
    }

    private static ResolvedRef Resolved(string fieldPath, uint formId)
    {
        return new ResolvedRef
        {
            FieldPath = fieldPath,
            OriginalFormId = formId,
            Action = ResolvedRefAction.Resolved,
            FinalFormId = formId
        };
    }
}