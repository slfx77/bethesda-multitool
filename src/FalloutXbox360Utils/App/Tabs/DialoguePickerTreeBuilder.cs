using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Subtitles;
using Microsoft.UI.Xaml.Controls;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds the dialogue picker TreeView node hierarchy from dialogue tree data.
///     Handles both Quest-grouped and NPC-grouped modes with search filtering.
/// </summary>
internal static class DialoguePickerTreeBuilder
{
    /// <summary>
    ///     Builds quest-grouped root nodes for the dialogue picker tree.
    /// </summary>
    public static List<TreeViewNode> BuildQuestPickerNodes(DialogueTreeResult tree, string? searchQuery)
    {
        var nodes = new List<TreeViewNode>();

        var quests = tree.QuestTrees.Values
            .Where(q => q.Topics.Count > 0)
            .OrderBy(q => q.QuestName ?? "", StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var quest in quests)
        {
            var matchingTopics = DialogueMetadataBuilder.FilterTopics(quest.Topics, searchQuery);
            if (matchingTopics.Count == 0)
            {
                continue;
            }

            var questName = quest.QuestName ?? $"0x{quest.QuestFormId:X8}";
            var questNode = new EsmBrowserNode
            {
                DisplayName = questName,
                Detail = $"({matchingTopics.Count})",
                NodeType = "Category",
                IconGlyph = "\uE8BD",
                HasUnrealizedChildren = true,
                DataObject = new QuestPickerData(quest.QuestFormId, matchingTopics)
            };

            nodes.Add(new TreeViewNode { Content = questNode, HasUnrealizedChildren = true });
        }

        var orphanTopics = DialogueMetadataBuilder.FilterTopics(tree.OrphanTopics, searchQuery);
        if (orphanTopics.Count > 0)
        {
            var orphanNode = new EsmBrowserNode
            {
                DisplayName = "Unassigned Topics",
                Detail = $"({orphanTopics.Count})",
                NodeType = "Category",
                IconGlyph = "\uE8BD",
                HasUnrealizedChildren = true,
                DataObject = orphanTopics
            };

            nodes.Add(new TreeViewNode { Content = orphanNode, HasUnrealizedChildren = true });
        }

        return nodes;
    }

    /// <summary>
    ///     Builds NPC-grouped root nodes for the dialogue picker tree.
    /// </summary>
    public static List<TreeViewNode> BuildNpcPickerNodes(
        Dictionary<uint, List<TopicDialogueNode>> topicsBySpeaker,
        Func<uint, string> resolveFormName,
        string? searchQuery)
    {
        var nodes = new List<TreeViewNode>();

        var speakers = topicsBySpeaker
            .Select(kv => (
                FormId: kv.Key,
                Name: resolveFormName(kv.Key),
                Topics: DialogueMetadataBuilder.FilterTopics(kv.Value, searchQuery)))
            .Where(s => s.Topics.Count > 0)
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var speaker in speakers)
        {
            var speakerNode = new EsmBrowserNode
            {
                DisplayName = speaker.Name,
                Detail = $"({speaker.Topics.Count})",
                NodeType = "Category",
                IconGlyph = "\uE77B",
                HasUnrealizedChildren = true,
                DataObject = new SpeakerPickerData(speaker.FormId, speaker.Topics)
            };

            nodes.Add(new TreeViewNode { Content = speakerNode, HasUnrealizedChildren = true });
        }

        return nodes;
    }

    /// <summary>
    ///     Expands a category node, creating child topic nodes. Called from TreeView_Expanding.
    /// </summary>
    public static void ExpandCategoryNode(
        TreeViewExpandingEventArgs args,
        bool pickerByQuest,
        Func<uint, SubtitleEntry?>? subtitleLookup,
        Func<uint, string> resolveFormName)
    {
        if (!args.Node.HasUnrealizedChildren || args.Node.Content is not EsmBrowserNode node)
        {
            return;
        }

        List<TopicDialogueNode> topics;
        uint? speakerFormId = null;
        uint? questFormId = null;
        if (node.DataObject is SpeakerPickerData speakerData)
        {
            topics = speakerData.Topics;
            speakerFormId = speakerData.SpeakerFormId;
        }
        else if (node.DataObject is QuestPickerData questData)
        {
            topics = questData.Topics;
            questFormId = questData.QuestFormId;
        }
        else if (node.DataObject is List<TopicDialogueNode> orphanTopics)
        {
            topics = orphanTopics;
        }
        else
        {
            return;
        }

        foreach (var topic in topics.OrderBy(t => t.TopicName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var displayName = ResolveTopicDisplayName(topic, pickerByQuest, subtitleLookup);
            var detail = BuildTopicDetail(topic);
            var topicDataObject = WrapTopicData(topic, speakerFormId, questFormId);

            var topicNode = new EsmBrowserNode
            {
                DisplayName = displayName,
                Detail = detail,
                NodeType = "Record",
                IconGlyph = DialogueMetadataBuilder.GetTopicTypeIcon(topic.Topic?.TopicType ?? 0),
                DataObject = topicDataObject
            };

            args.Node.Children.Add(new TreeViewNode { Content = topicNode });
        }

        args.Node.HasUnrealizedChildren = false;
    }

    /// <summary>
    ///     Extracts the topic from a picker data wrapper, returning null for non-topic nodes.
    /// </summary>
    public static (TopicDialogueNode? Topic, uint? SpeakerFilter, uint? QuestFilter) ExtractTopicFromInvocation(
        EsmBrowserNode browserNode)
    {
        if (browserNode.DataObject is SpeakerTopicPickerData speakerData)
        {
            return (speakerData.Topic, speakerData.SpeakerFormId, null);
        }

        if (browserNode.DataObject is QuestTopicPickerData questData)
        {
            return (questData.Topic, null, questData.QuestFormId);
        }

        if (browserNode.DataObject is TopicDialogueNode topic)
        {
            return (topic, null, null);
        }

        return (null, null, null);
    }

    /// <summary>
    ///     Checks whether a category node's topics contain a given topic FormID.
    /// </summary>
    public static bool CategoryContainsTopic(EsmBrowserNode categoryNode, uint topicFormId)
    {
        return categoryNode.DataObject switch
        {
            QuestPickerData qd => qd.Topics.Any(t => t.TopicFormId == topicFormId),
            SpeakerPickerData sd => sd.Topics.Any(t => t.TopicFormId == topicFormId),
            List<TopicDialogueNode> list => list.Any(t => t.TopicFormId == topicFormId),
            _ => false
        };
    }

    /// <summary>
    ///     Extracts the topic FormID from a child tree node's data wrapper.
    /// </summary>
    public static uint? GetChildTopicFormId(EsmBrowserNode topicNode)
    {
        return topicNode.DataObject switch
        {
            QuestTopicPickerData qt => qt.Topic.TopicFormId,
            SpeakerTopicPickerData st => st.Topic.TopicFormId,
            TopicDialogueNode t => t.TopicFormId,
            _ => null
        };
    }

    private static string ResolveTopicDisplayName(
        TopicDialogueNode topic, bool pickerByQuest, Func<uint, SubtitleEntry?>? subtitleLookup)
    {
        var topicName = topic.TopicName ?? topic.Topic?.EditorId ?? $"0x{topic.TopicFormId:X8}";

        if (pickerByQuest)
        {
            return topicName;
        }

        // NPC view: show first response text
        var firstText = topic.InfoChain
            .SelectMany(info => info.Info.Responses)
            .Select(r => r.Text)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));

        // Subtitle fallback for picker display
        if (firstText == null && subtitleLookup != null)
        {
            firstText = topic.InfoChain
                .Select(info => subtitleLookup(info.Info.FormId)?.Text)
                .FirstOrDefault(t => !string.IsNullOrEmpty(t));
        }

        if (firstText != null && !DialogueMetadataBuilder.IsSimilarText(firstText, topicName))
        {
            return firstText.Length > 80 ? firstText[..77] + "..." : firstText;
        }

        return topicName;
    }

    private static string BuildTopicDetail(TopicDialogueNode topic)
    {
        var infoCount = topic.InfoChain.Count;
        var topicType = topic.Topic?.TopicTypeName;
        return topicType != null ? $"{topicType} ({infoCount})" : $"({infoCount})";
    }

    private static object WrapTopicData(TopicDialogueNode topic, uint? speakerFormId, uint? questFormId)
    {
        if (speakerFormId.HasValue)
        {
            return new SpeakerTopicPickerData(speakerFormId.Value, topic);
        }

        if (questFormId.HasValue)
        {
            return new QuestTopicPickerData(questFormId.Value, topic);
        }

        return topic;
    }

    // Typed wrappers for TreeView DataObject to disambiguate quest vs NPC picker context
    internal sealed record QuestPickerData(uint QuestFormId, List<TopicDialogueNode> Topics);

    internal sealed record SpeakerPickerData(uint SpeakerFormId, List<TopicDialogueNode> Topics);

    internal sealed record QuestTopicPickerData(uint QuestFormId, TopicDialogueNode Topic);

    internal sealed record SpeakerTopicPickerData(uint SpeakerFormId, TopicDialogueNode Topic);
}
