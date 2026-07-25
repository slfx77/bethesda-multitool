using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class QuestVariableWriterDiagnosticsTests
{
    private const uint Quest = 0x00001000;
    private const uint TargetScript = 0x00002000;
    private const uint WriterInfoId = 0x00100050;

    [Fact]
    public void Report_names_route_excluded_writer_on_shared_overlay()
    {
        var mapping = Mapping(7, 70);
        var writer = WriterInfo();
        var overlay = new SharedDialogueInfoOverlay(
            writer,
            new MasterDialogueInfo(
                new ParsedMainRecord
                {
                    Header = new MainRecordHeader { Signature = "INFO", FormId = WriterInfoId },
                    Offset = 0
                },
                0x000000D4,
                0,
                new HashSet<uint>()));
        var combinePlan = Plan([], [overlay]);
        var sink = new RecordingSink();

        QuestVariableWriterDiagnostics.Report(
            [writer], combinePlan, [mapping], [], [], [],
            null, new NewVsOverrideClassifier([WriterInfoId]), EmptyMasterIndex(), sink);

        var rejected = Assert.Single(sink.Events,
            static evt => evt.Code == "quest-variable.writer-rejected");
        Assert.Equal(WriterInfoId, rejected.FormId);
        Assert.Contains("route=shared-overlay", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("route-excluded-would-prove-write", rejected.Message, StringComparison.Ordinal);
        var summary = Assert.Single(sink.Events,
            static evt => evt.Code == "quest-variable.channel-no-writer");
        Assert.Contains("1 dialogue candidate(s)", summary.Message, StringComparison.Ordinal);
        Assert.Contains("No producer evidence was collected", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_names_owner_fate_when_writer_was_suppressed_as_consumer()
    {
        // The fixed-point cascade: the writer's evidence WAS collected, but the gate removed
        // the writer INFO as a consumer of a different unsupported channel, so this channel
        // lost its only live owner and its consumers were suppressed in a later pass.
        var mapping = Mapping(7, 70);
        var writer = WriterInfo();
        var evidence = new QuestVariableProducerEvidence(
            mapping,
            new QuestVariableProducerOwner("INFO", WriterInfoId, "WriterInfo/ResultScripts[0]"));
        var ownerSuppression = new QuestVariableProducerGateDiagnostic(
            "quest-variable.record-suppressed-no-emitted-producer",
            "INFO",
            WriterInfoId,
            "WriterInfo",
            Quest,
            TargetScript,
            76,
            "bOtherUnwrittenFlag",
            "Suppressed the entire new record.",
            new Dictionary<string, string?>());
        var sink = new RecordingSink();

        QuestVariableWriterDiagnostics.Report(
            [writer], Plan([writer], []), [mapping], [evidence],
            [], [ownerSuppression],
            null, new NewVsOverrideClassifier([]), EmptyMasterIndex(), sink);

        var rejected = Assert.Single(sink.Events,
            static evt => evt.Code == "quest-variable.writer-rejected");
        Assert.Contains("writer-suppressed-as-consumer", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("bOtherUnwrittenFlag", rejected.Message, StringComparison.Ordinal);
        var summary = Assert.Single(sink.Events,
            static evt => evt.Code == "quest-variable.channel-no-writer");
        Assert.Contains("suppressed by the gate as a consumer of", summary.Message, StringComparison.Ordinal);
        Assert.Contains("bOtherUnwrittenFlag (ID 76)", summary.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_flags_surplus_slot_write_that_never_reaches_output()
    {
        var mapping = Mapping(7, 70);
        var writer = new DialogueRecord
        {
            FormId = WriterInfoId,
            EditorId = "SurplusSlotWriter",
            ResultScripts =
            [
                new DialogueResultScript(),
                new DialogueResultScript(),
                CompiledScript(7)
            ]
        };
        var combinePlan = Plan([writer], []);
        var sink = new RecordingSink();

        QuestVariableWriterDiagnostics.Report(
            [writer], combinePlan, [mapping], [], [], [],
            null, new NewVsOverrideClassifier([]), EmptyMasterIndex(), sink);

        var rejected = Assert.Single(sink.Events,
            static evt => evt.Code == "quest-variable.writer-rejected");
        Assert.Contains("route=new", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("surplus-slot-write-never-emitted", rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new-route-evidence-mismatch", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_attributes_atomic_validator_and_merge_sentinel_on_bad_sibling_slot()
    {
        var mapping = Mapping(7, 70);
        var writer = new DialogueRecord
        {
            FormId = WriterInfoId,
            EditorId = "AtomicallyBlockedWriter",
            ResultScripts =
            [
                CompiledScript(7),
                new DialogueResultScript
                {
                    CompiledData = [0x1D],
                    ReferencedObjects = [Quest],
                    IsIncompleteExecutableBundle = true
                }
            ]
        };
        var combinePlan = Plan([writer], []);
        var sink = new RecordingSink();

        QuestVariableWriterDiagnostics.Report(
            [writer], combinePlan, [mapping], [], [], [],
            null, new NewVsOverrideClassifier([]), EmptyMasterIndex(), sink);

        var rejected = Assert.Single(sink.Events,
            static evt => evt.Code == "quest-variable.writer-rejected");
        Assert.Contains("atomic-info-validator", rejected.Message, StringComparison.Ordinal);
        Assert.Contains("duplicate-merge-incomplete-sentinel", rejected.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("new-route-evidence-mismatch", rejected.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Report_is_silent_when_every_fresh_mapping_survived_the_gate()
    {
        var mapping = Mapping(7, 70);
        var writer = WriterInfo();
        var evidence = new QuestVariableProducerEvidence(
            mapping,
            new QuestVariableProducerOwner("INFO", WriterInfoId, "WriterInfo/ResultScripts[0]"));
        var sink = new RecordingSink();

        QuestVariableWriterDiagnostics.Report(
            [writer], Plan([writer], []), [mapping], [evidence],
            [mapping], [],
            null, new NewVsOverrideClassifier([]), EmptyMasterIndex(), sink);

        Assert.Empty(sink.Events);
    }

    private static DialogueRecord WriterInfo()
    {
        return new DialogueRecord
        {
            FormId = WriterInfoId,
            EditorId = "WriterInfo",
            ResultScripts = [CompiledScript(7)]
        };
    }

    private static DialogueCombinePlan Plan(
        IReadOnlyList<DialogueRecord> newInfos,
        IReadOnlyList<SharedDialogueInfoOverlay> overlays)
    {
        return new DialogueCombinePlan([], newInfos, overlays, new HashSet<uint>(), 0, 0, 0, []);
    }

    private static MasterDialogueIndex EmptyMasterIndex()
    {
        return MasterDialogueIndex.Build([], []);
    }

    private static DialogueResultScript CompiledScript(uint sourceVariable)
    {
        return new DialogueResultScript
        {
            CompiledData = BuildExternalSet(checked((ushort)sourceVariable)),
            ReferencedObjects = [Quest]
        };
    }

    private static QuestVariableRecoveryMapping Mapping(
        uint sourceVariable,
        uint targetVariable,
        string name = "RecoveredState")
    {
        return new QuestVariableRecoveryMapping(
            Quest,
            Quest,
            TargetScript,
            new ScriptVariableInfo(sourceVariable, name, 1),
            new ScriptVariableInfo(targetVariable, name, 1),
            ScriptVariableDeclarationKind.Short);
    }

    private static byte[] BuildExternalSet(ushort variableIndex)
    {
        var bytes = new List<byte>();
        AppendUInt16(bytes, 0x001D);
        AppendUInt16(bytes, 0);
        AppendUInt16(bytes, 0x0015);
        AppendUInt16(bytes, 15);
        bytes.Add(0x72);
        AppendUInt16(bytes, 1);
        bytes.Add(0x73);
        AppendUInt16(bytes, variableIndex);
        AppendUInt16(bytes, 7);
        bytes.Add(0x20);
        bytes.Add(0x72);
        AppendUInt16(bytes, 1);
        bytes.Add(0x73);
        AppendUInt16(bytes, variableIndex);
        AppendUInt16(bytes, 0xFFFF);
        AppendUInt16(bytes, 0);
        return [.. bytes];
    }

    private static void AppendUInt16(List<byte> bytes, ushort value)
    {
        bytes.Add((byte)value);
        bytes.Add((byte)(value >> 8));
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