using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Xunit;

namespace FalloutXbox360Utils.Tests.Core.Formats.Esm;

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

    #region CollectLinkedTopics Tests

    [Fact]
    public void CollectLinkedTopics_NormalLinks_ReturnsAll()
    {
        var topicB = MakeTopic(2, "TopicB");
        var topicC = MakeTopic(3, "TopicC");
        var info1 = MakeInfo(100, linkedTopics: [topicB, topicC]);
        var chain = new List<InfoDialogueNode> { info1 };

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, excludeTopicFormId: 1);

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

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, excludeTopicFormId: 1);

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

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, excludeTopicFormId: 1);

        Assert.Empty(result);
    }

    [Fact]
    public void CollectLinkedTopics_EmptyChain_ReturnsEmpty()
    {
        var result = DialogueViewerHelper.CollectLinkedTopics([], excludeTopicFormId: 1);

        Assert.Empty(result);
    }

    [Fact]
    public void CollectLinkedTopics_DeduplicatesByFormId()
    {
        var topicB = MakeTopic(2, "TopicB");
        var info1 = MakeInfo(100, linkedTopics: [topicB]);
        var info2 = MakeInfo(101, linkedTopics: [topicB]);
        var chain = new List<InfoDialogueNode> { info1, info2 };

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, excludeTopicFormId: 1);

        Assert.Single(result);
        // Should keep the first occurrence (info1)
        Assert.Equal(100u, result[2].SourceInfo.Info.FormId);
    }

    #endregion

    #region FilterInfoChain Tests

    [Fact]
    public void FilterInfoChain_NoFilters_ReturnsFullChain()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, questId: 10, speakerId: 100),
            MakeInfo(2, questId: 20, speakerId: 200)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, null, null);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void FilterInfoChain_QuestFilter_ScopesToQuest()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, questId: 10, speakerId: 100),
            MakeInfo(2, questId: 20, speakerId: 200),
            MakeInfo(3, questId: 10, speakerId: 300)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, questFilter: 10, speakerFilter: null);

        Assert.Equal(2, result.Count);
        Assert.All(result, info => Assert.Equal(10u, info.Info.QuestFormId));
    }

    [Fact]
    public void FilterInfoChain_SpeakerFilter_ScopesToSpeaker()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, questId: 10, speakerId: 100),
            MakeInfo(2, questId: 20, speakerId: 200),
            MakeInfo(3, questId: 10, speakerId: 100)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, questFilter: null, speakerFilter: 100);

        Assert.Equal(2, result.Count);
        Assert.All(result, info => Assert.Equal(100u, info.Info.SpeakerFormId));
    }

    [Fact]
    public void FilterInfoChain_BothFilters_AppliesBoth()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, questId: 10, speakerId: 100),
            MakeInfo(2, questId: 20, speakerId: 100),
            MakeInfo(3, questId: 10, speakerId: 200)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, questFilter: 10, speakerFilter: 100);

        Assert.Single(result);
        Assert.Equal(1u, result[0].Info.FormId);
    }

    [Fact]
    public void FilterInfoChain_QuestFilterMatchesNothing_FallsBackToFullChain()
    {
        var chain = new List<InfoDialogueNode>
        {
            MakeInfo(1, questId: 10),
            MakeInfo(2, questId: 20)
        };

        var result = DialogueViewerHelper.FilterInfoChain(chain, questFilter: 99, speakerFilter: null);

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

        var result = DialogueViewerHelper.FilterInfoChain(chain, questFilter: null, speakerFilter: 999);

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

        var result = DialogueViewerHelper.CollectLinkedTopics(chain, excludeTopicFormId: 1);

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
