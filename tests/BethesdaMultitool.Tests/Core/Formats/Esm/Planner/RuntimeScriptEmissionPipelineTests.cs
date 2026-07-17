using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.PlannedWriter;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition;
using BethesdaMultitool.Core.Formats.Esm.Planner.Disposition.Policies;
using BethesdaMultitool.Core.Formats.Esm.Planner.References;
using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Pipeline;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner;

public sealed class RuntimeScriptEmissionPipelineTests
{
    [Fact]
    public void RuntimeOnlySameDumpScript_IsAllocatedEmittedAndReboundFromOwningBase()
    {
        const uint sourceScriptFormId = 0x00123462;
        const uint sourceDoorFormId = 0x00123463;
        const string scriptName = "RuntimeOnlyDoorScript";
        const string sourceText = "scn RuntimeOnlyDoorScript";

        var captured = Assert.IsType<ScriptRecord>(
            ScriptRuntimeMerger.CreateScriptFromRuntimeData(new RuntimeScriptData
            {
                FormId = sourceScriptFormId,
                EditorId = scriptName,
                SourceText = sourceText,
                CompiledData = [0x00, 0x1D, 0x00, 0x00],
                DataSize = 4,
                IsCompiled = true,
                VariablesComplete = true,
                ReferencedObjectsComplete = true,
                DumpOffset = 0x1234,
            }));
        var accepted = ScriptRecordHandler.EnforceCapturedSourceCorrespondence(
            captured with { DecompiledText = $"ScriptName {scriptName}" });

        Assert.True(accepted.FromRuntime);
        Assert.Equal(ScriptSourceTextOrigin.RuntimeSameObject, accepted.SourceTextOrigin);
        Assert.Equal(ScriptSourceCorrespondenceStatus.Accepted,
            accepted.SourceTextCorrespondenceStatus);

        var records = new RecordCollection
        {
            Scripts = [accepted],
            Doors =
            [
                new DoorRecord
                {
                    FormId = sourceDoorFormId,
                    EditorId = "RuntimeOnlyScriptedDoor",
                    Script = sourceScriptFormId,
                },
            ],
        };
        var plan = BuildPlanner().Build(
            masterRecords: [],
            dmpRecords: records,
            enabledTypes: new HashSet<string>(StringComparer.Ordinal) { "SCPT", "DOOR" },
            masterFormIds: new HashSet<uint>(),
            masterPath: null);

        var scriptPlan = Assert.Single(plan.Records,
            record => record.Type == "SCPT" && record.SourceFormId == sourceScriptFormId);
        Assert.Equal(RecordDisposition.New, scriptPlan.Disposition);
        Assert.Null(scriptPlan.Master);
        Assert.Equal(scriptPlan.FormId, plan.SourceToEmittedFormId[sourceScriptFormId]);
        Assert.NotEqual(sourceScriptFormId, scriptPlan.FormId);
        Assert.Equal(FormIdAllocator.PluginIndex, (byte)(scriptPlan.FormId >> 24));

        var sink = new RecordingSink();
        var writer = new PlanWriter(PlannedEncoders.BuildRegistry(), sink);
        var options = new PluginBuildOptions { CompressRecords = false };
        var scptGrup = writer.BuildGrupForType("SCPT", plan, options);
        var doorGrup = writer.BuildGrupForType("DOOR", plan, options);
        var tes4 = PluginRecordByteBuilder.BuildNewRecordBytes("TES4", 0, 0, []);
        var emittedRecords = EsmParser.EnumerateRecords([.. tes4, .. scptGrup, .. doorGrup]);

        var emittedScript = Assert.Single(emittedRecords,
            record => record.Header.Signature == "SCPT" && record.Header.FormId == scriptPlan.FormId);
        Assert.Equal(
            new byte[] { 0x1D, 0x00, 0x00, 0x00 },
            Assert.Single(emittedScript.Subrecords,
                subrecord => subrecord.Signature == "SCDA").Data);
        Assert.Equal(
            sourceText,
            Assert.Single(emittedScript.Subrecords,
                subrecord => subrecord.Signature == "SCTX").DataAsString);

        var emittedDoorFormId = plan.SourceToEmittedFormId[sourceDoorFormId];
        var emittedDoor = Assert.Single(emittedRecords,
            record => record.Header.Signature == "DOOR" && record.Header.FormId == emittedDoorFormId);
        Assert.Equal(
            scriptPlan.FormId,
            Assert.Single(emittedDoor.Subrecords,
                subrecord => subrecord.Signature == "SCRI").DataAsFormId);

        var provenance = Assert.Single(sink.Events,
            evt => evt.Code == "script.source-provenance" && evt.FormId == scriptPlan.FormId);
        Assert.Equal("runtime-same-object", provenance.Metadata!["source-origin"]);
        Assert.Equal("proven-zero-nontolerated-mismatches",
            provenance.Metadata["sctx-scda-semantic-match"]);
    }

    private static EsmPlanner BuildPlanner()
    {
        var disposition = new DispositionEngine(
        [
            new ScriptDispositionPolicy(),
            new DefaultDispositionPolicy(),
        ]);
        var degradation = new DegradationPolicy();
        degradation.SetDefaultForType("SCPT", DanglingAction.DropSubrecord);
        var references = new ReferenceResolver([new ScriptReferenceWalker()], degradation);
        return new EsmPlanner(disposition, new FormIdAllocator(), references);
    }

    private sealed class RecordingSink : IConversionProgressSink
    {
        public List<ConversionProgressEvent> Events { get; } = [];

        public void OnPhaseStart(string phase, int? totalItems)
        {
        }

        public void OnEvent(ConversionProgressEvent evt) => Events.Add(evt);

        public void OnPhaseEnd(string phase, ConversionPipelineStats partialStats)
        {
        }

        public void OnComplete(ConversionPipelineStats stats)
        {
        }
    }
}
