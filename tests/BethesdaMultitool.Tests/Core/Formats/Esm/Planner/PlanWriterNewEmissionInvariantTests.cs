using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class PlanWriterNewEmissionInvariantTests
{
    [Fact]
    public void NewRecordWhoseEncoderReturnsNoSubrecords_ThrowsContractFailure()
    {
        const uint sourceFormId = 0x00F00001;
        const uint emittedFormId = 0x01000800;
        var record = new RecordPlan
        {
            Type = "TST0",
            Disposition = RecordDisposition.New,
            FormId = emittedFormId,
            SourceFormId = sourceFormId,
            Model = new object(),
            References = [],
            ContainedBy = [],
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "tripwire" },
        };
        var plan = new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(sourceFormId, emittedFormId),
            EmittedFormIds = ImmutableHashSet.Create(emittedFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty
                .Add(emittedFormId, 0),
            Diagnostics = [],
            Meta = new PlanMetadata
            {
                NextObjectId = 0x801,
                PlannerCoverage = ImmutableHashSet.Create("TST0"),
            },
        };
        var writer = new PlanWriter(new PlannedEncoderRegistry([new EmptyEncoder()]));

        var exception = Assert.Throws<InvalidOperationException>(() =>
            writer.BuildGrupForType("TST0", plan, new PluginBuildOptions()));

        Assert.Contains("Planned new TST0", exception.Message, StringComparison.Ordinal);
        Assert.Contains("planner/writer emission policies have diverged", exception.Message,
            StringComparison.Ordinal);
    }

    private sealed class EmptyEncoder : IPlannedRecordEncoder<object>
    {
        public string RecordType => "TST0";

        public EncodedRecord Encode(object model, RecordPlan plan, PlanReferenceLookup refs) =>
            new() { Subrecords = [], Warnings = [] };
    }
}
