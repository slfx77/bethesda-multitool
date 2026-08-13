using BethesdaMultitool.Core.Formats.Esm.Export.Geck;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Export;

/// <summary>
///     Pins the dialogue-tree report's report-wide topic deduplication. The old per-quest visited
///     sets re-rendered a shared topic's FULL INFO chain under every linked quest — quadratic in
///     shared-topic size × quest links. Retail Fallout3.esm links one DIAL into 107 quests with
///     GREETING carrying 2,542 INFOs, and `esm reports` died with OutOfMemoryException; FNV's
///     smaller topology merely fit. Each topic now renders fully exactly once and every later
///     encounter emits the "(see above)" stub.
/// </summary>
public sealed class GeckDialogueTreeReportTests
{
    private static TopicDialogueNode BuildTopic(uint formId, string name, int infoCount)
    {
        var topic = new TopicDialogueNode { TopicFormId = formId, TopicName = name };
        for (var i = 0; i < infoCount; i++)
        {
            topic.InfoChain.Add(new InfoDialogueNode
            {
                Info = new DialogueRecord
                {
                    FormId = (uint)(formId + 0x1000 + i),
                    Responses = [new DialogueResponse { Text = $"Response {i} of {name}" }],
                },
            });
        }

        return topic;
    }

    private static DialogueTreeResult SharedTopicAcrossQuests(int questCount, int infoCount)
    {
        var shared = BuildTopic(0x00014E9C, "GREETING", infoCount);
        var tree = new DialogueTreeResult();
        for (var q = 0; q < questCount; q++)
        {
            tree.QuestTrees[(uint)(0x100 + q)] = new QuestDialogueNode
            {
                QuestFormId = (uint)(0x100 + q),
                QuestName = $"Quest{q:D3}",
                Topics = [shared],
            };
        }

        return tree;
    }

    [Fact]
    public void SharedTopicRendersFullyOnceAndStubsAfterwards()
    {
        var report = GeckDialogueWriter.GenerateDialogueTreeReport(
            SharedTopicAcrossQuests(questCount: 3, infoCount: 2), FormIdResolver.Empty);

        // Full render exactly once; the two other quests show the dedup stub.
        Assert.Equal(1, CountOccurrences(report, "Response 0 of GREETING"));
        Assert.Equal(2, CountOccurrences(report, "(see above)"));
        // All three quests still appear.
        Assert.Contains("Quest000", report, StringComparison.Ordinal);
        Assert.Contains("Quest002", report, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The size law itself: doubling the number of quests sharing one large topic must add
    ///     only stub lines (linear, small), never re-render the chain (quadratic). This is the
    ///     shape that OOM'd on retail Fallout3.esm.
    /// </summary>
    [Fact]
    public void ReportSizeStaysLinearWhenManyQuestsShareOneLargeTopic()
    {
        var few = GeckDialogueWriter.GenerateDialogueTreeReport(
            SharedTopicAcrossQuests(questCount: 5, infoCount: 200), FormIdResolver.Empty);
        var many = GeckDialogueWriter.GenerateDialogueTreeReport(
            SharedTopicAcrossQuests(questCount: 50, infoCount: 200), FormIdResolver.Empty);

        // 45 extra quests must add only headers + stubs. Under the old per-quest sets the
        // growth would be ~45 × the full 200-INFO chain (hundreds of KB here, GBs on retail).
        var growth = many.Length - few.Length;
        Assert.True(growth < 45 * 400,
            $"expected stub-only growth, got {growth} chars for 45 extra quests");
        Assert.Equal(1, CountOccurrences(many, "Response 199 of GREETING"));
    }

    [Fact]
    public void OrphanTopicsShareTheReportWideDedupSet()
    {
        var shared = BuildTopic(0x2222, "SharedTopic", 1);
        var tree = new DialogueTreeResult
        {
            QuestTrees =
            {
                [0x100] = new QuestDialogueNode
                {
                    QuestFormId = 0x100, QuestName = "QuestA", Topics = [shared],
                },
            },
            OrphanTopics = { shared },
        };

        var report = GeckDialogueWriter.GenerateDialogueTreeReport(tree, FormIdResolver.Empty);

        Assert.Equal(1, CountOccurrences(report, "Response 0 of SharedTopic"));
        Assert.Equal(1, CountOccurrences(report, "(see above)"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
