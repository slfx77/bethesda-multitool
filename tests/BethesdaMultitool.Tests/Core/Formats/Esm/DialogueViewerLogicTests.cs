using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm;

public class DialogueViewerLogicTests
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
        uint? speakerId = null, params TopicDialogueNode[] linkedTopics)
    {
        return new InfoDialogueNode
        {
            Info = new DialogueRecord
            {
                FormId = formId,
                QuestFormId = questId,
                SpeakerFormId = speakerId
            },
            ChoiceTopics = linkedTopics.ToList()
        };
    }

    private static InfoDialogueNode MakeInfoWithResponses(uint formId, uint? questId = null,
        uint? speakerId = null, string? promptText = null, params string[] responseTexts)
    {
        return new InfoDialogueNode
        {
            Info = new DialogueRecord
            {
                FormId = formId,
                QuestFormId = questId,
                SpeakerFormId = speakerId,
                PromptText = promptText,
                Responses = responseTexts
                    .Select(t => new DialogueResponse { Text = t })
                    .ToList()
            },
            ChoiceTopics = []
        };
    }

    #endregion

    #region DialogueMetadataBuilder Tests

    [Fact]
    public void BuildTopicsBySpeaker_SharedTopicIsIndexedForEveryExplicitSpeaker()
    {
        var sharedGreeting = MakeTopic(1, "GREETING",
            MakeInfo(100, speakerId: 0xA),
            MakeInfo(101, speakerId: 0xB),
            MakeInfo(102, speakerId: 0xA));
        var tree = new DialogueTreeResult
        {
            OrphanTopics = [sharedGreeting]
        };

        var result = DialogueMetadataBuilder.BuildTopicsBySpeaker(tree, [0xA, 0xB]);

        Assert.Equal(2, result.Count);
        Assert.Same(sharedGreeting, Assert.Single(result[0xA]));
        Assert.Same(sharedGreeting, Assert.Single(result[0xB]));
    }

    [Fact]
    public void BuildTopicsBySpeaker_TopicLevelSpeakerRemainsIndexedWithoutInfoSpeaker()
    {
        var topic = MakeTopic(1, "SpecificTopic", MakeInfo(100)) with
        {
            Topic = new DialogTopicRecord
            {
                FormId = 1,
                SpeakerFormId = 0xA
            }
        };
        var tree = new DialogueTreeResult
        {
            OrphanTopics = [topic]
        };

        var result = DialogueMetadataBuilder.BuildTopicsBySpeaker(tree, [0xA]);

        Assert.Same(topic, Assert.Single(result[0xA]));
    }

    #endregion

    #region CollectLinkedTopics Tests

    [Fact]
    public void CollectLinkedTopics_NormalLinks_ReturnsAll()
    {
        var topicB = MakeTopic(2, "TopicB");
        var topicC = MakeTopic(3, "TopicC");
        var info1 = MakeInfo(100, linkedTopics: [topicB, topicC]);
        var chain = new List<InfoDialogueNode> { info1 };

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, 1);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey(2));
        Assert.True(result.ContainsKey(3));
    }

    [Fact]
    public void CollectLinkedTopics_SelfReference_Excluded()
    {
        var topicA = MakeTopic(1, "TopicA");
        var topicB = MakeTopic(2, "TopicB");
        var info1 = MakeInfo(100, linkedTopics: [topicA, topicB]);
        var chain = new List<InfoDialogueNode> { info1 };

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, 1);

        Assert.Single(result);
        Assert.True(result.ContainsKey(2));
        Assert.False(result.ContainsKey(1));
    }

    [Fact]
    public void CollectLinkedTopics_AllSelfReferences_ReturnsEmpty()
    {
        var topicA = MakeTopic(1, "TopicA");
        var info1 = MakeInfo(100, linkedTopics: [topicA]);
        var chain = new List<InfoDialogueNode> { info1 };

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, 1);

        Assert.Empty(result);
    }

    [Fact]
    public void CollectLinkedTopics_PreservesTopicsWithoutInfos()
    {
        var emptyTopic = MakeTopic(2, "DeadTopic");
        var liveTopic = MakeTopic(3, "LiveTopic", MakeInfo(300));
        var source = MakeInfo(100, linkedTopics: [emptyTopic, liveTopic]);

        var result = DialogueViewerHelper.CollectLinkedTopics([source], 1);

        Assert.Equal(2, result.Count);
        Assert.True(result.ContainsKey(2));
        Assert.True(result.ContainsKey(3));
    }

    [Fact]
    public void CollectLinkedTopics_EmptyChain_ReturnsEmpty()
    {
        var result = DialogueViewerHelper.CollectLinkedTopics([], 1);

        Assert.Empty(result);
    }

    [Fact]
    public void CollectLinkedTopics_DeduplicatesByFormId()
    {
        var topicB = MakeTopic(2, "TopicB");
        var info1 = MakeInfo(100, linkedTopics: [topicB]);
        var info2 = MakeInfo(101, linkedTopics: [topicB]);
        var chain = new List<InfoDialogueNode> { info1, info2 };

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, 1);

        Assert.Single(result);
        // Should keep the first occurrence (info1)
        Assert.Equal(100u, result[2].SourceInfo.Info.FormId);
    }

    [Fact]
    public void CollectLinkedTopics_IncludesFollowUpInfoChoices()
    {
        var choiceTopic = MakeTopic(2, "ChoiceTopic");
        var followUp = MakeInfo(101, linkedTopics: [choiceTopic]);
        var source = MakeInfo(100);
        source.FollowUpInfos.Add(followUp);

        var result = DialogueViewerHelper.CollectLinkedTopics([source], 1);

        Assert.Single(result);
        Assert.True(result.ContainsKey(2));
        Assert.Equal(101u, result[2].SourceInfo.Info.FormId);
    }

    [Fact]
    public void CollectLinkedTopics_FollowUpCycle_DoesNotLoop()
    {
        var choiceTopic = MakeTopic(2, "ChoiceTopic");
        var source = MakeInfo(100);
        var followUp = MakeInfo(101, linkedTopics: [choiceTopic]);
        source.FollowUpInfos.Add(followUp);
        followUp.FollowUpInfos.Add(source);

        var result = DialogueViewerHelper.CollectLinkedTopics([source], 1);

        Assert.Single(result);
        Assert.True(result.ContainsKey(2));
    }

    #endregion

    #region FilterInfoChain Tests

    [Fact]
    public void FilterInfoChain_NoFilters_ReturnsFullChain()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, 10, 100),
            MakeInfo(2, 20, 200)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, null, null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterInfoChain_QuestFilter_ScopesToQuest()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, 10, 100),
            MakeInfo(2, 20, 200),
            MakeInfo(3, 10, 300)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, 10, null);

        Assert.Equal(2, result.Count);
        Assert.All(result, info => Assert.Equal(10u, info.Info.QuestFormId));
    }

    [Fact]
    public void FilterInfoChain_SpeakerFilter_ScopesToSpeaker()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, 10, 100),
            MakeInfo(2, 20, 200),
            MakeInfo(3, 10, 100)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, null, 100);

        Assert.Equal(2, result.Count);
        Assert.All(result, info => Assert.Equal(100u, info.Info.SpeakerFormId));
    }

    [Fact]
    public void FilterInfoChain_BothFilters_AppliesBoth()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, 10, 100),
            MakeInfo(2, 20, 100),
            MakeInfo(3, 10, 200)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, 10, 100);

        Assert.Single(result);
        Assert.Equal(1u, result[0].Info.FormId);
    }

    [Fact]
    public void FilterInfoChain_QuestFilterMatchesNothing_FallsBackToFullChain()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, 10),
            MakeInfo(2, 20)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, 99, null);

        // Falls back to full chain since no INFOs match quest 99
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterInfoChain_SpeakerFilterMatchesNothing_FallsBackToChain()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, speakerId: 100),
            MakeInfo(2, speakerId: 200)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, null, 999);

        Assert.Equal(2, result.Count);
    }

    #endregion

    #region FindParentTopicIn Tests

    [Fact]
    public void FindParentTopicIn_FindsParentThatLinksToChild()
    {
        var childTopic = MakeTopic(2, "Child");
        var parentInfo = MakeInfo(100, linkedTopics: [childTopic]);
        var parentTopic = MakeTopic(1, "Parent", parentInfo);

        var result = DialogueViewerHelper.FindParentTopicIn(childTopic, [parentTopic]);

        Assert.NotNull(result);
        Assert.Equal(1u, result.TopicFormId);
    }

    [Fact]
    public void FindParentTopicIn_SkipsSelf()
    {
        var topicA = MakeTopic(1, "TopicA");
        var infoLinkingToSelf = MakeInfo(100, linkedTopics: [topicA]);
        var topicWithSelfLink = new TopicDialogueNode
        {
            TopicFormId = 1,
            TopicName = "TopicA",
            InfoChain = [infoLinkingToSelf]
        };

        var result = DialogueViewerHelper.FindParentTopicIn(topicA, [topicWithSelfLink]);

        Assert.Null(result);
    }

    [Fact]
    public void FindParentTopicIn_NoParent_ReturnsNull()
    {
        var childTopic = MakeTopic(2, "Child");
        var unrelatedInfo = MakeInfo(100);
        var unrelatedTopic = MakeTopic(3, "Unrelated", unrelatedInfo);

        var result = DialogueViewerHelper.FindParentTopicIn(childTopic, [unrelatedTopic]);

        Assert.Null(result);
    }

    [Fact]
    public void FindParentTopicIn_MultipleParents_ReturnsFirst()
    {
        var childTopic = MakeTopic(3, "Child");
        var parent1 = MakeTopic(1, "Parent1", MakeInfo(100, linkedTopics: [childTopic]));
        var parent2 = MakeTopic(2, "Parent2", MakeInfo(101, linkedTopics: [childTopic]));

        var result = DialogueViewerHelper.FindParentTopicIn(childTopic, [parent1, parent2]);

        Assert.NotNull(result);
        Assert.Equal(1u, result.TopicFormId);
    }

    [Fact]
    public void FindParentTopicIn_OnlyMatchesChoiceTopics_NotAddedTopics()
    {
        var childTopic = MakeTopic(2, "Child");
        // Parent has child in AddedTopics, NOT ChoiceTopics
        var parentInfo = new InfoDialogueNode
        {
            Info = new DialogueRecord { FormId = 100 },
            ChoiceTopics = [],
            AddedTopics = [childTopic]
        };
        var parentTopic = MakeTopic(1, "Parent", parentInfo);

        var result = DialogueViewerHelper.FindParentTopicIn(childTopic, [parentTopic]);

        // Should NOT find parent because child is only in AddedTopics
        Assert.Null(result);
    }

    [Fact]
    public void FindParentTopicIn_FindsParentThroughFollowUpInfo()
    {
        var childTopic = MakeTopic(2, "Child");
        var followUp = MakeInfo(101, linkedTopics: [childTopic]);
        var parentInfo = MakeInfo(100);
        parentInfo.FollowUpInfos.Add(followUp);
        var parentTopic = MakeTopic(1, "Parent", parentInfo);

        var result = DialogueViewerHelper.FindParentTopicIn(childTopic, [parentTopic]);

        Assert.NotNull(result);
        Assert.Equal(1u, result.TopicFormId);
    }

    #endregion

    #region CollectLinkedTopics TCLT/NAME Separation Tests

    [Fact]
    public void CollectLinkedTopics_OnlyCollectsChoiceTopics_NotAddedTopics()
    {
        var choiceTopic = MakeTopic(2, "Choice");
        var addedTopic = MakeTopic(3, "Added");
        var info = new InfoDialogueNode
        {
            Info = new DialogueRecord { FormId = 100 },
            ChoiceTopics = [choiceTopic],
            AddedTopics = [addedTopic]
        };
        var chain = new List<InfoDialogueNode> { info };

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, 1);

        Assert.Single(result);
        Assert.True(result.ContainsKey(2));
        Assert.False(result.ContainsKey(3));
    }

    [Fact]
    public void HasResultScript_DetectedOnDialogueRecord()
    {
        var record = new DialogueRecord
        {
            FormId = 1,
            HasResultScript = true
        };

        Assert.True(record.HasResultScript);
    }

    [Fact]
    public void HasResultScript_DefaultsFalse()
    {
        var record = new DialogueRecord { FormId = 1 };

        Assert.False(record.HasResultScript);
    }

    [Fact]
    public void CollectUnlockedTopics_IncludesFollowUpAddedTopics()
    {
        var addedTopic = MakeTopic(3, "Added");
        var source = MakeInfo(100);
        var followUp = new InfoDialogueNode
        {
            Info = new DialogueRecord { FormId = 101 },
            AddedTopics = [addedTopic]
        };
        source.FollowUpInfos.Add(followUp);

        var result = DialogueViewerHelper.CollectUnlockedTopics([source]);

        Assert.Single(result);
        Assert.True(result.ContainsKey(3));
    }

    #endregion

    #region CollectLoopbackTopicList Tests

    [Fact]
    public void CollectLoopbackTopicList_WithSpeakerFilter_UsesSpeakerTopicList()
    {
        var current = MakeTopic(1, "Current", MakeInfo(100, speakerId: 500));
        var sameSpeaker = MakeTopic(2, "SameSpeaker", MakeInfo(101, speakerId: 500));
        var otherSpeaker = MakeTopic(3, "OtherSpeaker", MakeInfo(102, speakerId: 999));
        var tree = new DialogueTreeResult
        {
            QuestTrees = new Dictionary<uint, QuestDialogueNode>
            {
                [10] = new() { QuestFormId = 10, Topics = [current, sameSpeaker, otherSpeaker] }
            }
        };
        var topicsBySpeaker = new Dictionary<uint, List<TopicDialogueNode>>
        {
            [500] = [current, sameSpeaker],
            [999] = [otherSpeaker]
        };

        var result = DialogueViewerHelper.CollectLoopbackTopicList(
            tree, topicsBySpeaker, current, null, 500);

        Assert.Single(result);
        Assert.Equal(2u, result[0].TopicFormId);
    }

    [Fact]
    public void CollectLoopbackTopicList_WithQuestFilter_UsesQuestTopicList()
    {
        var current = MakeTopic(1, "Current", MakeInfo(100, 10));
        var sameQuest = MakeTopic(2, "SameQuest", MakeInfo(101, 10));
        var otherQuest = MakeTopic(3, "OtherQuest", MakeInfo(102, 20));
        var tree = new DialogueTreeResult
        {
            QuestTrees = new Dictionary<uint, QuestDialogueNode>
            {
                [10] = new() { QuestFormId = 10, Topics = [current, sameQuest] },
                [20] = new() { QuestFormId = 20, Topics = [otherQuest] }
            }
        };

        var result = DialogueViewerHelper.CollectLoopbackTopicList(
            tree, null, current, 10, null);

        Assert.Single(result);
        Assert.Equal(2u, result[0].TopicFormId);
    }

    #endregion

    #region ResolveHeaderQuest Tests

    [Fact]
    public void ResolveHeaderQuest_NoFilter_UsesTopicTreeGrouping()
    {
        var topic = MakeTopic(1, "SharedTopic",
            MakeInfo(100, 0xBBBB));
        var tree = new DialogueTreeResult
        {
            QuestTrees = new Dictionary<uint, QuestDialogueNode>
            {
                [0xAAAA] = new()
                {
                    QuestFormId = 0xAAAA,
                    QuestName = "QuestA",
                    Topics = [topic]
                }
            }
        };

        var result = DialogueViewerHelper.ResolveHeaderQuest(
            topic, topic.InfoChain, tree, false);

        Assert.Equal(0xAAAAu, result);
    }

    [Fact]
    public void ResolveHeaderQuest_WithFilter_UsesFilteredInfoQuest()
    {
        var topic = MakeTopic(1, "SharedTopic",
            MakeInfo(100, 0xAAAA),
            MakeInfo(101, 0xBBBB));
        var tree = new DialogueTreeResult
        {
            QuestTrees = new Dictionary<uint, QuestDialogueNode>
            {
                [0xAAAA] = new()
                {
                    QuestFormId = 0xAAAA,
                    QuestName = "TopicParentQuest",
                    Topics = [topic]
                }
            }
        };
        var filteredChain = new List<InfoDialogueNode>
        {
            MakeInfo(101, 0xBBBB)
        };

        var result = DialogueViewerHelper.ResolveHeaderQuest(
            topic, filteredChain, tree, true);

        Assert.Equal(0xBBBBu, result);
    }

    [Fact]
    public void ResolveHeaderQuest_WithFilter_NoQuestOnInfo_FallsBackToTree()
    {
        var topic = MakeTopic(1, "SharedTopic",
            MakeInfo(100));
        var tree = new DialogueTreeResult
        {
            QuestTrees = new Dictionary<uint, QuestDialogueNode>
            {
                [0xAAAA] = new()
                {
                    QuestFormId = 0xAAAA,
                    QuestName = "FallbackQuest",
                    Topics = [topic]
                }
            }
        };

        var result = DialogueViewerHelper.ResolveHeaderQuest(
            topic, topic.InfoChain, tree, true);

        Assert.Equal(0xAAAAu, result);
    }

    #endregion

    #region ResolvePromptText Tests

    [Fact]
    public void ResolvePromptText_ResponseTextBeforeEditorId()
    {
        // Topic with only EditorId but has response text in InfoChain
        var topic = new TopicDialogueNode
        {
            TopicFormId = 1,
            TopicName = "188AlexanderAboutGunRunners",
            Topic = new DialogTopicRecord
            {
                FormId = 1,
                EditorId = "188AlexanderAboutGunRunners"
            },
            InfoChain =
            [
                MakeInfoWithResponses(100, responseTexts: ["Tell me about the Gun Runners."])
            ]
        };

        var sourceInfo = MakeInfo(200);
        var result = DialogueViewerHelper.ResolvePromptText(sourceInfo, topic);

        Assert.Equal("Tell me about the Gun Runners.", result);
    }

    [Fact]
    public void ResolvePromptText_SourcePromptText_TakesPriority()
    {
        var topic = MakeTopic(1, "SomeTopic");
        var sourceInfo = new InfoDialogueNode
        {
            Info = new DialogueRecord
            {
                FormId = 200,
                PromptText = "What about the Gun Runners?"
            },
            ChoiceTopics = []
        };

        var result = DialogueViewerHelper.ResolvePromptText(sourceInfo, topic);

        Assert.Equal("What about the Gun Runners?", result);
    }

    [Fact]
    public void ResolvePromptText_FullName_BeforeResponseText()
    {
        var topic = new TopicDialogueNode
        {
            TopicFormId = 1,
            TopicName = "About Gun Runners",
            Topic = new DialogTopicRecord
            {
                FormId = 1,
                FullName = "About Gun Runners",
                EditorId = "188AlexanderAboutGunRunners"
            },
            InfoChain =
            [
                MakeInfoWithResponses(100, responseTexts: ["Tell me about the Gun Runners."])
            ]
        };

        var sourceInfo = MakeInfo(200);
        var result = DialogueViewerHelper.ResolvePromptText(sourceInfo, topic);

        Assert.Equal("About Gun Runners", result);
    }

    [Fact]
    public void ResolvePromptText_LongResponseText_Truncated()
    {
        var longText = new string('A', 150);
        var topic = new TopicDialogueNode
        {
            TopicFormId = 1,
            Topic = new DialogTopicRecord { FormId = 1, EditorId = "SomeTopic" },
            InfoChain =
            [
                MakeInfoWithResponses(100, responseTexts: [longText])
            ]
        };

        var sourceInfo = MakeInfo(200);
        var result = DialogueViewerHelper.ResolvePromptText(sourceInfo, topic);

        Assert.Equal(100, result.Length);
        Assert.EndsWith("...", result);
    }

    [Fact]
    public void ResolvePromptText_NoResponseNoFullName_FallsBackToEditorId()
    {
        var topic = new TopicDialogueNode
        {
            TopicFormId = 1,
            TopicName = "SomeTopic",
            Topic = new DialogTopicRecord { FormId = 1, EditorId = "SomeTopic" },
            InfoChain = []
        };

        var sourceInfo = MakeInfo(200);
        var result = DialogueViewerHelper.ResolvePromptText(sourceInfo, topic);

        Assert.Equal("SomeTopic", result);
    }

    #endregion
}
