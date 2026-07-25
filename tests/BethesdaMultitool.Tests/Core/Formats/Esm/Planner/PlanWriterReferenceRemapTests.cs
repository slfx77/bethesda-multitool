using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class PlanWriterReferenceRemapTests
{
    [Fact]
    public void NewDoor_ScriUsesPlannerAllocatedScriptFormId()
    {
        const uint doorFormId = 0x010030EB;
        const uint sourceScriptFormId = 0x000416DD;
        const uint emittedScriptFormId = 0x0100525E;
        var door = new DoorRecord
        {
            FormId = doorFormId,
            EditorId = "ScriptedDoor",
            Script = sourceScriptFormId
        };
        var record = new RecordPlan
        {
            Type = "DOOR",
            Disposition = RecordDisposition.New,
            FormId = doorFormId,
            SourceFormId = 0x001230EB,
            Model = door,
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
        var plan = new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty
                .Add(sourceScriptFormId, emittedScriptFormId),
            EmittedFormIds = ImmutableHashSet.Create(doorFormId, emittedScriptFormId),
            ValidScriptFormIds = ImmutableHashSet.Create(emittedScriptFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(doorFormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x525F,
                PlannerCoverage = ImmutableHashSet.Create("DOOR", "SCPT")
            }
        };

        var grup = new PlanWriter(PlannedEncoders.BuildRegistry()).BuildGrupForType(
            "DOOR", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var parsed = EsmParser.EnumerateRecords([.. tes4, .. grup]);
        var emittedDoor = Assert.Single(parsed, static parsedRecord => parsedRecord.Header.Signature == "DOOR");
        var scri = Assert.Single(emittedDoor.Subrecords, static subrecord => subrecord.Signature == "SCRI");

        Assert.Equal(emittedScriptFormId, scri.DataAsFormId);
    }

    [Fact]
    public void NewDoor_DropsScriWhenPlannerSuppressedTargetScript()
    {
        const uint doorFormId = 0x010030EB;
        const uint sourceScriptFormId = 0x000416DD;
        var record = new RecordPlan
        {
            Type = "DOOR",
            Disposition = RecordDisposition.New,
            FormId = doorFormId,
            SourceFormId = 0x001230EB,
            Model = new DoorRecord { FormId = doorFormId, Script = sourceScriptFormId },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
        var plan = new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty,
            EmittedFormIds = ImmutableHashSet.Create(doorFormId),
            ValidScriptFormIds = ImmutableHashSet<uint>.Empty,
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(doorFormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x525F,
                PlannerCoverage = ImmutableHashSet.Create("DOOR", "SCPT")
            }
        };

        var grup = new PlanWriter(PlannedEncoders.BuildRegistry()).BuildGrupForType(
            "DOOR", plan, new PluginBuildOptions { CompressRecords = false });
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var parsed = EsmParser.EnumerateRecords([.. tes4, .. grup]);
        var emittedDoor = Assert.Single(parsed, static parsedRecord => parsedRecord.Header.Signature == "DOOR");

        Assert.DoesNotContain(emittedDoor.Subrecords, static subrecord => subrecord.Signature == "SCRI");
    }

    [Fact]
    public void NewScript_WriterFailsClosedAndSurfacesWarningWhenPlannerContractIsBypassed()
    {
        const uint sourceFormId = 0x00100000;
        const uint emittedFormId = 0x01000800;
        var record = new RecordPlan
        {
            Type = "SCPT",
            Disposition = RecordDisposition.New,
            FormId = emittedFormId,
            SourceFormId = sourceFormId,
            Model = new ScriptRecord { FormId = sourceFormId },
            References = ImmutableArray<ResolvedRef>.Empty,
            ContainedBy = ImmutableArray<RecordContainmentEdge>.Empty,
            Provenance = new PlanProvenance { PolicyId = "test", Reason = "test" }
        };
        var plan = new EmitPlan
        {
            Records = [record],
            SourceToEmittedFormId = ImmutableDictionary<uint, uint>.Empty.Add(
                sourceFormId, emittedFormId),
            EmittedFormIds = ImmutableHashSet<uint>.Empty.Add(emittedFormId),
            ValidScriptFormIds = ImmutableHashSet<uint>.Empty.Add(emittedFormId),
            RecordIndexByEmittedFormId = ImmutableDictionary<uint, int>.Empty.Add(emittedFormId, 0),
            Diagnostics = ImmutableArray<PlanDiagnostic>.Empty,
            Meta = new PlanMetadata
            {
                NextObjectId = 0x801,
                PlannerCoverage = ImmutableHashSet.Create("SCPT")
            }
        };
        var sink = new RecordingSink();

        var exception = Assert.Throws<InvalidOperationException>(() =>
            new PlanWriter(PlannedEncoders.BuildRegistry(), sink).BuildGrupForType(
                "SCPT", plan, new PluginBuildOptions { CompressRecords = false }));

        Assert.Contains("planner/writer script-emission policies have diverged", exception.Message,
            StringComparison.Ordinal);
        var warning = Assert.Single(sink.Events, evt =>
            evt.Code == "planned-encoder.warning" && evt.FormType == "SCPT");
        Assert.Equal(sourceFormId, warning.FormId);
        Assert.Contains("skipping stub", warning.Message, StringComparison.Ordinal);
    }

    private sealed class RecordingSink : IConversionProgressSink
    {
        public List<ConversionProgressEvent> Events { get; } = [];

        public void OnPhaseStart(string phase, int? totalItems)
        {
        }

        public void OnEvent(ConversionProgressEvent evt)
        {
            Events.Add(evt);
        }

        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
        {
        }

        public void OnComplete(ConversionPipelineStats stats)
        {
        }
    }
}