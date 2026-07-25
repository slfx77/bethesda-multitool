using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.Records;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class DialogueTopicParentProvenanceTests
{
    [Fact]
    public void LinkInfoToTopics_RawOnlyParentFillsMissingRuntimeAttribution()
    {
        const uint topicId = 0x00102000;
        const uint infoId = 0x00102001;
        const uint questId = 0x00102002;
        var dialogues = new List<DialogueRecord> { new() { FormId = infoId } };
        var topics = new List<DialogTopicRecord>
        {
            new() { FormId = topicId, QuestFormId = questId }
        };

        Link(dialogues, topics, new Dictionary<uint, List<uint>> { [topicId] = [infoId] });

        var linked = Assert.Single(dialogues);
        Assert.Equal(topicId, linked.TopicFormId);
        Assert.Equal(topicId, linked.RawParentTopicFormId);
        Assert.Equal([topicId], linked.RawParentTopicFormIds);
        Assert.Equal(questId, linked.QuestFormId);
        Assert.False(linked.HasAmbiguousRawParentTopic);
        Assert.False(linked.HasRawRuntimeTopicConflict);
        Assert.Equal(1, Assert.Single(topics).ResponseCount);
    }

    [Fact]
    public void LinkInfoToTopics_RuntimeAndRawAgreementPreservesBoth()
    {
        const uint topicId = 0x00102100;
        const uint infoId = 0x00102101;
        var dialogues = new List<DialogueRecord>
        {
            new() { FormId = infoId, TopicFormId = topicId }
        };

        Link(dialogues, [new DialogTopicRecord { FormId = topicId }],
            new Dictionary<uint, List<uint>> { [topicId] = [infoId, infoId] });

        var linked = Assert.Single(dialogues);
        Assert.Equal(topicId, linked.TopicFormId);
        Assert.Equal(topicId, linked.RawParentTopicFormId);
        Assert.Equal([topicId], linked.RawParentTopicFormIds);
        Assert.False(linked.HasAmbiguousRawParentTopic);
        Assert.False(linked.HasRawRuntimeTopicConflict);
    }

    [Fact]
    public void LinkInfoToTopics_RuntimeAndRawDisagreementDoesNotRedirectRuntimeParent()
    {
        const uint runtimeTopic = 0x00102200;
        const uint rawTopic = 0x00102210;
        const uint runtimeQuest = 0x00102220;
        const uint rawQuest = 0x00102230;
        const uint infoId = 0x00102201;
        var dialogues = new List<DialogueRecord>
        {
            new() { FormId = infoId, TopicFormId = runtimeTopic }
        };
        var topics = new List<DialogTopicRecord>
        {
            new() { FormId = runtimeTopic, QuestFormId = runtimeQuest },
            new() { FormId = rawTopic, QuestFormId = rawQuest }
        };

        Link(dialogues, topics, new Dictionary<uint, List<uint>> { [rawTopic] = [infoId] });

        var linked = Assert.Single(dialogues);
        Assert.Equal(runtimeTopic, linked.TopicFormId);
        Assert.Equal(rawTopic, linked.RawParentTopicFormId);
        Assert.Null(linked.QuestFormId);
        Assert.True(linked.HasRawRuntimeTopicConflict);
        Assert.False(linked.HasAmbiguousRawParentTopic);
    }

    [Fact]
    public void LinkInfoToTopics_AmbiguousRawParentsRemainUnresolved()
    {
        const uint lowerTopic = 0x00102300;
        const uint higherTopic = 0x00102310;
        const uint infoId = 0x00102301;
        var dialogues = new List<DialogueRecord> { new() { FormId = infoId } };
        var topics = new List<DialogTopicRecord>
        {
            new() { FormId = lowerTopic, QuestFormId = 0x00102320 },
            new() { FormId = higherTopic, QuestFormId = 0x00102330 }
        };
        var rawParents = new Dictionary<uint, List<uint>>
        {
            [higherTopic] = [infoId],
            [lowerTopic] = [infoId]
        };

        Link(dialogues, topics, rawParents);

        var linked = Assert.Single(dialogues);
        Assert.Null(linked.TopicFormId);
        Assert.Null(linked.RawParentTopicFormId);
        Assert.Equal([lowerTopic, higherTopic], linked.RawParentTopicFormIds);
        Assert.Null(linked.QuestFormId);
        Assert.True(linked.HasAmbiguousRawParentTopic);
        Assert.False(linked.HasRawRuntimeTopicConflict);
    }

    private static void Link(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics,
        Dictionary<uint, List<uint>> topicToInfoMap)
    {
        var context = new RecordParserContext(new EsmRecordScanResult
        {
            TopicToInfoMap = topicToInfoMap
        });
        var merger = new DialogueTopicMerger(context);
        merger.LinkInfoToTopicsByGroupOrder(dialogues, topics);
    }
}