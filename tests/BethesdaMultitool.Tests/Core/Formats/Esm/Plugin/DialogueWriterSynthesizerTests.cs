using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Planner;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Reporting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Plugin;

public sealed class DialogueWriterSynthesizerTests
{
    private const uint Quest = 0x000B16D0;
    private const uint SiteInfoId = 0x0013408B;

    private static readonly DialogueWriterSynthesizer.Rule TestRule = new(
        SiteInfoId,
        Quest,
        24,
        "bUlyssesHired",
        "set VNPCFollowers.bUlyssesHired to 1",
        "Let's go.",
        "test");

    [Fact]
    public void Bytecode_matches_retail_compiler_output_shape_exactly()
    {
        // Oracle: FalloutNV.esm INFO 0x0011759C, "set VNPCFollowers.RexAvailable to 1"
        // (SCRO index 2, local index 16).
        byte[] retail = [0x15, 0x00, 0x0A, 0x00, 0x72, 0x02, 0x00, 0x73, 0x10, 0x00, 0x02, 0x00, 0x20, 0x31];

        Assert.Equal(retail, DialogueWriterSynthesizer.BuildSetQuestVariableBytecode(2, 16));
    }

    [Fact]
    public void Synthesized_script_is_structurally_proven_by_the_production_scan()
    {
        var dialogues = new List<DialogueRecord> { new() { FormId = SiteInfoId } };
        DialogueWriterSynthesizer.Apply(dialogues, [TestRule], NullConversionProgressSink.Instance);
        var info = Assert.Single(dialogues);
        var mapping = Mapping(24, 77);

        var evidence = QuestVariableBytecodeRemapper.FindInfoProducerWrites(
            [info],
            [mapping],
            null,
            QuestVariableBytecodeRemapper.ProducerOperandIdentity.Source);

        var proof = Assert.Single(evidence);
        Assert.Equal(mapping, proof.Mapping);
        Assert.Equal("INFO", proof.Owner.RecordType);
        Assert.Equal(SiteInfoId, proof.Owner.SourceFormId);
    }

    [Fact]
    public void Apply_adds_fallback_text_only_when_no_meaningful_response_exists()
    {
        var bare = new List<DialogueRecord> { new() { FormId = SiteInfoId } };
        DialogueWriterSynthesizer.Apply(bare, [TestRule], NullConversionProgressSink.Instance);
        var synthesized = Assert.Single(bare);
        var response = Assert.Single(synthesized.Responses);
        Assert.Equal("Let's go.", response.Text);
        Assert.True(synthesized.HasResultScript);
        Assert.Equal("set VNPCFollowers.bUlyssesHired to 1",
            Assert.Single(synthesized.ResultScripts).SourceText);

        var withText = new List<DialogueRecord>
        {
            new()
            {
                FormId = SiteInfoId,
                Responses = [new DialogueResponse { ResponseNumber = 1, Text = "Then we walk." }]
            }
        };
        DialogueWriterSynthesizer.Apply(withText, [TestRule], NullConversionProgressSink.Instance);
        Assert.Equal("Then we walk.", Assert.Single(Assert.Single(withText).Responses).Text);
    }

    [Fact]
    public void Apply_never_replaces_captured_script_material()
    {
        var captured = new DialogueResultScript
        {
            CompiledData = [0x15, 0x00],
            ReferencedObjects = [Quest]
        };
        var dialogues = new List<DialogueRecord>
        {
            new() { FormId = SiteInfoId, ResultScripts = [captured] }
        };

        DialogueWriterSynthesizer.Apply(dialogues, [TestRule], NullConversionProgressSink.Instance);

        Assert.Same(captured, Assert.Single(Assert.Single(dialogues).ResultScripts));

        var incomplete = new List<DialogueRecord>
        {
            new()
            {
                FormId = SiteInfoId,
                ResultScripts = [new DialogueResultScript { IsIncompleteExecutableBundle = true }]
            }
        };
        DialogueWriterSynthesizer.Apply(incomplete, [TestRule], NullConversionProgressSink.Instance);
        Assert.True(Assert.Single(Assert.Single(incomplete).ResultScripts).IsIncompleteExecutableBundle);
    }

    [Fact]
    public void Apply_is_a_no_op_when_the_site_is_absent()
    {
        var dialogues = new List<DialogueRecord> { new() { FormId = 0x00000123 } };

        DialogueWriterSynthesizer.Apply(dialogues, [TestRule], NullConversionProgressSink.Instance);

        var untouched = Assert.Single(dialogues);
        Assert.Empty(untouched.ResultScripts);
        Assert.Empty(untouched.Responses);
    }

    private static QuestVariableRecoveryMapping Mapping(uint sourceVariable, uint targetVariable)
    {
        return new QuestVariableRecoveryMapping(
            Quest,
            Quest,
            0x001209D1,
            new ScriptVariableInfo(sourceVariable, "bUlyssesHired", 1),
            new ScriptVariableInfo(targetVariable, "bUlyssesHired", 1),
            ScriptVariableDeclarationKind.Short);
    }
}