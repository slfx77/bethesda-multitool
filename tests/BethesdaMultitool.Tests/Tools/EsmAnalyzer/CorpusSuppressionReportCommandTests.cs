using BethesdaMultitool.Core.Formats.Esm.Plugin;
using EsmAnalyzer.Commands;
using Xunit;

namespace BethesdaMultitool.Tests.Tools.EsmAnalyzer;

public sealed class CorpusSuppressionReportCommandTests
{
    [Theory]
    [InlineData("INFO", "quest-variable.record-suppressed", true)]
    [InlineData("PACK", "quest-variable.record-suppressed", true)]
    [InlineData("TERM", "quest-variable.record-suppressed", false)]
    [InlineData("INFO", "quest-variable.record-suppressed-no-emitted-producer", true)]
    [InlineData("PACK", "quest-variable.record-suppressed-no-emitted-producer", true)]
    [InlineData("TERM", "quest-variable.record-suppressed-no-emitted-producer", false)]
    [InlineData("INFO", "script-variable.owner-not-emitted", true)]
    [InlineData("INFO", "inline-script.suppress-unsafe-owner", true)]
    [InlineData("PACK", "inline-script.suppress-unsafe-owner", true)]
    [InlineData("TERM", "inline-script.suppress-unsafe-owner", true)]
    [InlineData("SCPT", "inline-script.suppress-unsafe-owner", false)]
    [InlineData("SCPT", "script.suppress-unsafe-reference-table", true)]
    [InlineData("INFO", "script.suppress-unsafe-reference-table", false)]
    public void TrackedSuppressionCodes_AreRestrictedToTheirOwnerTypes(
        string recordType,
        string code,
        bool expected)
    {
        Assert.Equal(expected, CorpusSuppressionReportCommand.IsTrackedSuppression(recordType, code));
    }

    [Fact]
    public void EventMetadataCapture_UsesEffectiveGenericScope_AndMasterFirstNames()
    {
        var occurrence = new CorpusSuppressionReportCommand.SuppressionOccurrence(
            "test.dmp",
            "INFO",
            0x00123456,
            "quest-variable.record-suppressed",
            "reason",
            new Dictionary<string, string?>
            {
                ["info-topic-form-id"] = "0x000000C8",
                ["info-topic-name"] = "Transient runtime prompt",
                ["info-quest-form-id"] = "0x00001000",
                ["info-quest-name"] = "Transient quest name",
                ["info-speaker-scope-kind"] = "faction",
                ["info-speaker-scope-form-id"] = "0x00002000",
                ["info-speaker-name"] = "Transient speaker name",
                ["info-response-000-number"] = "0",
                ["info-response-000-text"] = "Line"
            });
        var master = new CorpusSuppressionReportCommand.MasterNames(
            new Dictionary<uint, string?> { [0x000000C8] = "GREETING" },
            new Dictionary<uint, string?> { [0x00001000] = "Retail quest" },
            new Dictionary<uint, string?> { [0x00002000] = "Faction: Retail faction" },
            new Dictionary<uint, string?>());

        var capture = Assert.IsType<CorpusSuppressionReportCommand.InfoCapture>(
            CorpusSuppressionReportCommand.TryCaptureFromEventMetadata(occurrence, master));

        Assert.Equal("GREETING", capture.TopicName);
        Assert.Equal("Retail quest", capture.QuestName);
        Assert.Equal(0x00002000u, capture.SpeakerFormId);
        Assert.Equal("Faction: Retail faction", capture.SpeakerName);
        Assert.Equal((byte)1, Assert.Single(capture.Responses).ResponseNumber);
    }

    [Fact]
    public void BuildDialogueRows_PreservesVariants_DeduplicatesConditions_AndAggregatesCoverage()
    {
        const uint infoId = 0x00123456;
        const uint scriptId = 0x0000ABCD;
        var occurrences = new[]
        {
            Occurrence("a.dmp", infoId, scriptId, 7, "MissingOne"),
            Occurrence("a.dmp", infoId, scriptId, 8, "MissingTwo"),
            Occurrence("b.dmp", infoId, scriptId, 7, "MissingOne"),
            // Repeated identical diagnostic must not duplicate a line or condition.
            Occurrence("b.dmp", infoId, scriptId, 7, "MissingOne")
        };
        var captures = new[]
        {
            Capture("a.dmp", infoId, (1, "Direct line")),
            Capture("b.dmp", infoId, (1, "Direct line"), (1, "Changed line"),
                (2, DialogueTextBackfill.PlaceholderText))
        };
        var csvLines = new[]
        {
            new CorpusSuppressionReportCommand.CsvDialogueLine(
                infoId, 1, "CSV must not replace direct NAM1", "Sunny", "Test quest", 0, 1),
            new CorpusSuppressionReportCommand.CsvDialogueLine(
                infoId, 2, "CSV fallback line", "Sunny", "Test quest", 0, 2)
        };

        var rows = CorpusSuppressionReportCommand.BuildDialogueRows(
            occurrences,
            captures,
            csvLines,
            new Dictionary<uint, string?> { [scriptId] = "RetainedQuestScript" });

        Assert.Collection(rows,
            changed =>
            {
                Assert.Equal((byte)1, changed.ResponseNumber);
                Assert.Equal("Changed line", changed.Text);
                Assert.Equal(1, changed.DumpCount);
                Assert.Equal("b.dmp", changed.Dumps);
                Assert.Equal("MissingOne", changed.MissingVariableNames);
            },
            direct =>
            {
                Assert.Equal((byte)1, direct.ResponseNumber);
                Assert.Equal("Direct line", direct.Text);
                Assert.Equal(2, direct.DumpCount);
                Assert.Equal("a.dmp; b.dmp", direct.Dumps);
                Assert.Equal("MissingOne; MissingTwo", direct.MissingVariableNames);
                Assert.Equal("RetainedQuestScript", direct.TargetScriptEditorIds);
            },
            fallback =>
            {
                Assert.Equal((byte)2, fallback.ResponseNumber);
                Assert.Equal("CSV fallback line", fallback.Text);
                Assert.Equal("CSV fallback", fallback.TextSources);
                Assert.Equal(2, fallback.DumpCount);
                Assert.Equal("Speaker name; Sunny", fallback.SpeakerNames);
                Assert.Equal("Quest name; Test quest", fallback.QuestNames);
            });
    }

    [Fact]
    public void BuildDialogueRows_IncludesAtomicInlineScriptSuppression_WithoutInventingVariableZero()
    {
        const uint infoId = 0x00123456;
        const uint missingTarget = 0x0100ABCD;
        var occurrence = new CorpusSuppressionReportCommand.SuppressionOccurrence(
            "inline.dmp",
            "INFO",
            infoId,
            "inline-script.suppress-unsafe-owner",
            "ResultScripts[0].SCRO[1] target does not resolve.",
            new Dictionary<string, string?>
            {
                ["script-path"] = "ResultScripts[0]",
                ["reference-field"] = "ResultScripts[0].SCRO[1]",
                ["target-source-form-id"] = $"0x{missingTarget:X8}",
                ["local-variable-id"] = null
            });

        var row = Assert.Single(CorpusSuppressionReportCommand.BuildDialogueRows(
            [occurrence],
            [Capture("inline.dmp", infoId, (1, "Recovered line"))],
            [],
            new Dictionary<uint, string?>()));

        Assert.Equal("Recovered line", row.Text);
        Assert.Equal("inline-script.suppress-unsafe-owner", row.ReasonCodes);
        Assert.Equal($"0x{missingTarget:X8}", row.TargetFormIds);
        Assert.Empty(row.MissingVariableIds);
        Assert.Equal("inline.dmp", row.Dumps);
    }

    [Fact]
    public void BuildDialogueRows_IncludesMissingProducerIdentity()
    {
        const uint infoId = 0x00123456;
        const uint questId = 0x00001111;
        const uint scriptId = 0x00002222;
        var occurrence = new CorpusSuppressionReportCommand.SuppressionOccurrence(
            "producer.dmp",
            "INFO",
            infoId,
            "quest-variable.record-suppressed-no-emitted-producer",
            "No emitted writer exists.",
            new Dictionary<string, string?>
            {
                ["target-quest-form-id"] = $"0x{questId:X8}",
                ["target-script-form-id"] = $"0x{scriptId:X8}",
                ["target-variable-index"] = "57",
                ["target-variable-name"] = "RecoveredLocal"
            });

        var row = Assert.Single(CorpusSuppressionReportCommand.BuildDialogueRows(
            [occurrence],
            [Capture("producer.dmp", infoId, (1, "Recovered line"))],
            [],
            new Dictionary<uint, string?> { [scriptId] = "RetainedScript" }));

        Assert.Equal("quest-variable.record-suppressed-no-emitted-producer", row.ReasonCodes);
        Assert.Equal("RecoveredLocal", row.MissingVariableNames);
        Assert.Equal("57", row.MissingVariableIds);
        Assert.Equal($"0x{questId:X8}", row.TargetFormIds);
        Assert.Equal($"0x{scriptId:X8}", row.TargetScriptFormIds);
        Assert.Equal("RetainedScript", row.TargetScriptEditorIds);
    }

    [Fact]
    public void BuildScriptRows_UsesSourceIdentity_AndAggregatesDumpCoverage()
    {
        const uint sourceId = 0x00104E52;
        var occurrences = new[]
        {
            ScriptOccurrence(
                "a.dmp",
                sourceId,
                0x010035C4,
                "ratAmbushCave01SCRIPT",
                "SCRO[0]",
                0x001041D6,
                0x01000F6B),
            ScriptOccurrence(
                "b.dmp",
                sourceId,
                0x02001234,
                "ratAmbushCave01SCRIPT",
                "SCRO[0]",
                0x001041D6,
                0x02005678)
        };

        var row = Assert.Single(CorpusSuppressionReportCommand.BuildScriptRows(occurrences));

        Assert.Equal(sourceId, row.SourceFormId);
        Assert.Equal("ratAmbushCave01SCRIPT", row.EditorIds);
        Assert.Equal("0x010035C4; 0x02001234", row.EmittedFormIds);
        Assert.Equal("SCRO[0]", row.ReferenceFields);
        Assert.Equal("0x001041D6", row.TargetSourceFormIds);
        Assert.Equal("0x01000F6B; 0x02005678", row.TargetEmittedFormIds);
        Assert.Equal(2, row.DumpCount);
        Assert.Equal("a.dmp; b.dmp", row.Dumps);
    }

    private static CorpusSuppressionReportCommand.SuppressionOccurrence Occurrence(
        string dump,
        uint infoId,
        uint scriptId,
        uint variableId,
        string variableName)
    {
        return new CorpusSuppressionReportCommand.SuppressionOccurrence(
            dump,
            "INFO",
            infoId,
            "quest-variable.record-suppressed",
            "wording is not parsed when metadata exists",
            new Dictionary<string, string?>
            {
                ["condition-target-form-id"] = "0x00001111",
                ["condition-target-script-form-id"] = $"0x{scriptId:X8}",
                ["condition-variable-index"] = variableId.ToString(),
                ["condition-variable-name"] = variableName
            });
    }

    private static CorpusSuppressionReportCommand.InfoCapture Capture(
        string dump,
        uint infoId,
        params (byte Number, string Text)[] responses)
    {
        return new CorpusSuppressionReportCommand.InfoCapture(
            dump,
            infoId,
            "InfoEditorId",
            "Player prompt",
            0x00002222,
            "Topic name",
            0x00003333,
            "Quest name",
            0x00004444,
            "Speaker name",
            responses.Select(response => new CorpusSuppressionReportCommand.CapturedResponse(
                response.Number,
                response.Text)).ToArray());
    }

    private static CorpusSuppressionReportCommand.SuppressionOccurrence ScriptOccurrence(
        string dump,
        uint sourceFormId,
        uint emittedFormId,
        string editorId,
        string referenceField,
        uint targetSourceFormId,
        uint targetEmittedFormId)
    {
        return new CorpusSuppressionReportCommand.SuppressionOccurrence(
            dump,
            "SCPT",
            emittedFormId,
            "script.suppress-post-verdict-reference-table",
            "The referenced target was dropped.",
            new Dictionary<string, string?>
            {
                ["script-source-form-id"] = $"0x{sourceFormId:X8}",
                ["script-emitted-form-id"] = $"0x{emittedFormId:X8}",
                ["script-editor-id"] = editorId,
                ["reference-field"] = referenceField,
                ["reference-action"] = "Resolved",
                ["target-source-form-id"] = $"0x{targetSourceFormId:X8}",
                ["target-emitted-form-id"] = $"0x{targetEmittedFormId:X8}"
            });
    }
}