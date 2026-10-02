using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class SharedGreetingLabelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SharedGreeting_DoesNotPromoteAChildPromptOrResponseIntoEveryActorsTopicName(bool prompt)
    {
        var context = new RecordParserContext(new EsmRecordScanResult(), new() { [0xC8] = "GREETING" });
        var handler = new DialogueRecordHandler(context);
        var topics = new List<DialogTopicRecord> { new() { FormId = 0xC8, EditorId = "GREETING", FullName = "GREETING" } };
        var first = new DialogueRecord { FormId = 0x1000, TopicFormId = 0xC8, QuestFormId = 0x2000, SpeakerFormId = 0x3000,
            PromptText = prompt ? "First actor prompt" : null, Responses = [new DialogueResponse { Text = "First actor response" }] };
        var second = new DialogueRecord { FormId = 0x1001, TopicFormId = 0xC8, QuestFormId = 0x2001, SpeakerFormId = 0x3001,
            Responses = [new DialogueResponse { Text = "Second actor response" }] };
        handler.BackfillDialogTopicPromptText([first, second], topics);
        Assert.Equal("GREETING", topics[0].FullName);
        Assert.Null(topics[0].DummyPrompt);
        Assert.False(context.FormIdToFullName.ContainsKey(0xC8));

        // Even a previously recovered/derived display name must remain metadata, not masquerade as
        // the second actor's greeting caption. INFO bodies stay intact.
        var topic = new TopicDialogueNode { TopicFormId = 0xC8,
            Topic = topics[0] with { FullName = "First actor response" },
            InfoChain = [new InfoDialogueNode { Info = first }, new InfoDialogueNode { Info = second }] };
        Assert.Equal("GREETING", DialogueViewerHelper.ResolveTopicDisplayText(topic));
        Assert.Equal("First actor response", topic.Topic.FullName);
        var resolver = new FormIdResolver(new() { [0xC8] = "GREETING" }, new() { [0xC8] = "First actor response" });
        var report = GeckDialogueWriter.GenerateDialogueReport([first, second], resolver);
        Assert.Contains("Topic:          GREETING (0x000000C8)", report, StringComparison.Ordinal);
        Assert.DoesNotContain("Topic:          First actor response", report, StringComparison.Ordinal);
        Assert.Contains("First actor response", report, StringComparison.Ordinal);
        Assert.Contains("Second actor response", report, StringComparison.Ordinal);
        if (prompt) { Assert.Contains("First actor prompt", report, StringComparison.Ordinal); }
    }
}
