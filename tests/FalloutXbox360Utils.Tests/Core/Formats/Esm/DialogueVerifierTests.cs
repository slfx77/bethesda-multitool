using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

public class DialogueVerifierTests
{
    #region Helper Factories

    private static TopicDialogueNode MakeTopic(uint formId, string name,
        params InfoDialogueNode[] infos)
    {
        return new TopicDialogueNode
        {
            TopicFormId = formId,
            TopicName = name,
            InfoChain = infos.ToList()
        };
    }

    private static InfoDialogueNode MakeInfo(uint formId, uint? questId = null,
        uint? speakerId = null, bool saidOnce = false,
        List<uint>? linkToTopics = null, List<uint>? addTopics = null,
        params string[] responseTexts)
    {
        return new InfoDialogueNode
        {
            Info = new DialogueRecord
            {
                FormId = formId,
                QuestFormId = questId,
                SpeakerFormId = speakerId,
                SaidOnce = saidOnce,
                LinkToTopics = linkToTopics ?? [],
                AddTopics = addTopics ?? [],
                Responses = responseTexts
                    .Select(t => new DialogueResponse { Text = t })
                    .ToList()
            },
            ChoiceTopics = [],
            AddedTopics = []
        };
    }

    private static DialogueTreeResult MakeTree(
        Dictionary<uint, QuestDialogueNode>? quests = null,
        List<TopicDialogueNode>? orphans = null)
    {
        return new DialogueTreeResult
        {
            QuestTrees = quests ?? new Dictionary<uint, QuestDialogueNode>(),
            OrphanTopics = orphans ?? []
        };
    }

    private static QuestDialogueNode MakeQuest(uint formId, string name,
        params TopicDialogueNode[] topics)
    {
        return new QuestDialogueNode
        {
            QuestFormId = formId,
            QuestName = name,
            Topics = topics.ToList()
        };
    }

    #endregion

    [Fact]
    public void Verify_MatchingTrees_AllMatch()
    {
        var info1 = MakeInfo(100, responseTexts: ["Hello there."]);
        var info2 = MakeInfo(101, responseTexts: ["Goodbye."]);
        var topic = MakeTopic(1, "Greeting", info1, info2);
        var quest = MakeQuest(10, "MainQuest", topic);

        var tree1 = MakeTree(quests: new Dictionary<uint, QuestDialogueNode> { [10] = quest });
        var tree2 = MakeTree(quests: new Dictionary<uint, QuestDialogueNode> { [10] = quest });

        var result = DialogueVerifier.Compare(tree1, tree2);

        Assert.Equal(1, result.TopicsMatched);
        Assert.Equal(0, result.TopicsMissing);
        Assert.Equal(0, result.TopicsExtra);
        Assert.Equal(2, result.InfosMatched);
        Assert.Equal(0, result.InfosMissing);
        Assert.Equal(0, result.InfosExtra);
        Assert.Equal(0, result.FlowMismatches);
        Assert.Equal(2, result.ResponseTextMatches);
        Assert.Equal(0, result.ResponseTextMissing);
        Assert.Empty(result.TopicDiffs);
    }

    [Fact]
    public void Verify_MissingTopic_Detected()
    {
        var topic1 = MakeTopic(1, "TopicA", MakeInfo(100));
        var topic2 = MakeTopic(2, "TopicB", MakeInfo(101));

        var dmpTree = MakeTree(orphans: [topic1]);
        var esmTree = MakeTree(orphans: [topic1, topic2]);

        var result = DialogueVerifier.Compare(dmpTree, esmTree);

        Assert.Equal(1, result.TopicsMatched);
        Assert.Equal(1, result.TopicsMissing);
        Assert.Contains(result.TopicDiffs, d => d.DiffType == "Missing" && d.TopicFormId == 2);
    }

    [Fact]
    public void Verify_ExtraTopic_Detected()
    {
        var topic1 = MakeTopic(1, "TopicA", MakeInfo(100));
        var topic2 = MakeTopic(2, "TopicB", MakeInfo(101));

        var dmpTree = MakeTree(orphans: [topic1, topic2]);
        var esmTree = MakeTree(orphans: [topic1]);

        var result = DialogueVerifier.Compare(dmpTree, esmTree);

        Assert.Equal(1, result.TopicsMatched);
        Assert.Equal(1, result.TopicsExtra);
        Assert.Contains(result.TopicDiffs, d => d.DiffType == "Extra" && d.TopicFormId == 2);
    }

    [Fact]
    public void Verify_FlowMismatch_TCLTDifference()
    {
        var dmpInfo = MakeInfo(100, linkToTopics: [2u, 3u]);
        var esmInfo = MakeInfo(100, linkToTopics: [2u, 4u]);

        var dmpTree = MakeTree(orphans: [MakeTopic(1, "Topic", dmpInfo)]);
        var esmTree = MakeTree(orphans: [MakeTopic(1, "Topic", esmInfo)]);

        var result = DialogueVerifier.Compare(dmpTree, esmTree);

        Assert.Equal(1, result.FlowMismatches);
        Assert.Contains(result.TopicDiffs, d => d.DiffType == "FlowMismatch");
    }

    [Fact]
    public void Verify_ResponseTextMissing_Detected()
    {
        var dmpInfo = MakeInfo(100); // No response text
        var esmInfo = MakeInfo(100, responseTexts: ["Some dialogue line."]);

        var dmpTree = MakeTree(orphans: [MakeTopic(1, "Topic", dmpInfo)]);
        var esmTree = MakeTree(orphans: [MakeTopic(1, "Topic", esmInfo)]);

        var result = DialogueVerifier.Compare(dmpTree, esmTree);

        Assert.Equal(0, result.ResponseTextMatches);
        Assert.Equal(1, result.ResponseTextMissing);
    }

    [Fact]
    public void Verify_SaidOnce_CountsCorrectly()
    {
        var info1 = MakeInfo(100, saidOnce: true);
        var info2 = MakeInfo(101, saidOnce: false);
        var info3 = MakeInfo(102, saidOnce: true);

        var dmpTree = MakeTree(orphans: [MakeTopic(1, "Topic", info1, info2, info3)]);
        var esmTree = MakeTree(orphans: [MakeTopic(1, "Topic",
            MakeInfo(100), MakeInfo(101), MakeInfo(102))]);

        var result = DialogueVerifier.Compare(dmpTree, esmTree);

        Assert.Equal(2, result.SaidOnceCount);
        Assert.Equal(3, result.TotalDmpInfos);
    }

    [Fact]
    public void Verify_AddTopicMismatch_Detected()
    {
        var dmpInfo = MakeInfo(100, addTopics: [5u]);
        var esmInfo = MakeInfo(100, addTopics: [5u, 6u]);

        var dmpTree = MakeTree(orphans: [MakeTopic(1, "Topic", dmpInfo)]);
        var esmTree = MakeTree(orphans: [MakeTopic(1, "Topic", esmInfo)]);

        var result = DialogueVerifier.Compare(dmpTree, esmTree);

        Assert.Equal(1, result.AddTopicMismatches);
        Assert.Equal(0, result.AddTopicMatches);
    }

    [Fact]
    public void Verify_QuestFilter_LimitsScope()
    {
        var topic1 = MakeTopic(1, "QuestATopic", MakeInfo(100));
        var topic2 = MakeTopic(2, "QuestBTopic", MakeInfo(101));

        var questA = MakeQuest(10, "QuestA", topic1);
        var questB = MakeQuest(20, "QuestB", topic2);

        var tree = MakeTree(quests: new Dictionary<uint, QuestDialogueNode>
        {
            [10] = questA,
            [20] = questB
        });

        // Filter to quest 10 — should only see topic1
        var result = DialogueVerifier.Compare(tree, tree, questFilter: 10);

        Assert.Equal(1, result.TopicsMatched);
        Assert.Equal(0, result.TopicsMissing);
        Assert.Equal(0, result.TopicsExtra);
    }

    [Fact]
    public void Verify_MissingInfosWithinTopic_Detected()
    {
        var dmpTopic = MakeTopic(1, "Topic", MakeInfo(100), MakeInfo(101));
        var esmTopic = MakeTopic(1, "Topic", MakeInfo(100), MakeInfo(101), MakeInfo(102));

        var dmpTree = MakeTree(orphans: [dmpTopic]);
        var esmTree = MakeTree(orphans: [esmTopic]);

        var result = DialogueVerifier.Compare(dmpTree, esmTree);

        Assert.Equal(1, result.TopicsMatched);
        Assert.Equal(2, result.InfosMatched);
        Assert.Equal(1, result.InfosMissing);
        Assert.Contains(result.TopicDiffs, d => d.DiffType == "MissingINFOs");
    }

    [Fact]
    public void Verify_EmptyTrees_NoErrors()
    {
        var result = DialogueVerifier.Compare(MakeTree(), MakeTree());

        Assert.Equal(0, result.TopicsMatched);
        Assert.Equal(0, result.TopicsMissing);
        Assert.Equal(0, result.TopicsExtra);
        Assert.Empty(result.TopicDiffs);
    }
}
