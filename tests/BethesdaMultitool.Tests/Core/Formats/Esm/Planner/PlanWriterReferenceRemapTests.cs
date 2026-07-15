using System.Collections.Immutable;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
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
}
