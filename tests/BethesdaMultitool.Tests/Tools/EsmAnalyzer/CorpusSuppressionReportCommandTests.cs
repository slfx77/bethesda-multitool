using EsmAnalyzer.Commands;
using BethesdaMultitool.Core.Formats.Esm.Plugin;
using Xunit;

namespace BethesdaMultitool.Tests.Tools.EsmAnalyzer;

public sealed class CorpusSuppressionReportCommandTests
{
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
                ["info-response-000-text"] = "Line",
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
            Occurrence("b.dmp", infoId, scriptId, 7, "MissingOne"),
        };
        var captures = new[]
        {
            Capture("a.dmp", infoId, (1, "Direct line")),
            Capture("b.dmp", infoId, (1, "Direct line"), (1, "Changed line"),
                (2, DialogueTextBackfill.PlaceholderText)),
        };
        var csvLines = new[]
        {
            new CorpusSuppressionReportCommand.CsvDialogueLine(
                infoId, 1, "CSV must not replace direct NAM1", "Sunny", "Test quest", 0, 1),
            new CorpusSuppressionReportCommand.CsvDialogueLine(
                infoId, 2, "CSV fallback line", "Sunny", "Test quest", 0, 2),
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

    private static CorpusSuppressionReportCommand.SuppressionOccurrence Occurrence(
        string dump,
        uint infoId,
        uint scriptId,
        uint variableId,
        string variableName) =>
        new(
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
                ["condition-variable-name"] = variableName,
            });

    private static CorpusSuppressionReportCommand.InfoCapture Capture(
        string dump,
        uint infoId,
        params (byte Number, string Text)[] responses) =>
        new(
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
