using FalloutXbox360Utils.Core.Formats.Esm.Models;
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
    public static List<TreeViewNode> BuildQuestPickerNodes(
        DialogueTreeResult tree, string? searchQuery,
        Func<uint, string>? resolveFormName = null)
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

            var questName = resolveFormName != null
                ? resolveFormName(quest.QuestFormId)
                : quest.QuestName ?? $"0x{quest.QuestFormId:X8}";
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
        bool _pickerByQuest,
        Func<uint, string> _resolveFormName,
        bool showEditorIds = false)
    {
        if (!args.Node.HasUnrealizedChildren || args.Node.Content is not EsmBrowserNode node)
        {
            return;
        }

        // NPC mode: speaker root → category sub-nodes (Topic, Conversation, Combat, etc.)
        if (node.DataObject is SpeakerPickerData speakerData)
        {
            ExpandSpeakerIntoCategoryNodes(args.Node, speakerData);
            args.Node.HasUnrealizedChildren = false;
            return;
        }

        // NPC mode: category sub-node → individual topics
        if (node.DataObject is SpeakerCategoryPickerData categoryData)
        {
            ExpandTopicList(args.Node, categoryData.Topics,
                categoryData.SpeakerFormId, null, showEditorIds);
            args.Node.HasUnrealizedChildren = false;
            return;
        }

        List<TopicDialogueNode> topics;
        uint? questFormId = null;
        if (node.DataObject is QuestPickerData questData)
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

        ExpandTopicList(args.Node, topics, null, questFormId, showEditorIds);
        args.Node.HasUnrealizedChildren = false;
    }

    /// <summary>
    ///     Expands a speaker root node into category sub-nodes grouped by TopicType,
    ///     matching the GECK's dialogue tree organization (Topic, Conversation, Combat, etc.).
    /// </summary>
    private static void ExpandSpeakerIntoCategoryNodes(TreeViewNode parentNode, SpeakerPickerData speakerData)
    {
        // GECK display order for topic types
        ReadOnlySpan<byte> categoryOrder = [0, 1, 2, 3, 4, 5, 6, 7];

        var topicsByType = new Dictionary<byte, List<TopicDialogueNode>>();
        foreach (var topic in speakerData.Topics)
        {
            var topicType = topic.Topic?.TopicType ?? 0;
            if (!topicsByType.TryGetValue(topicType, out var list))
            {
                list = [];
                topicsByType[topicType] = list;
            }

            list.Add(topic);
        }

        foreach (var typeCode in categoryOrder)
        {
            if (!topicsByType.TryGetValue(typeCode, out var topics) || topics.Count == 0)
            {
                continue;
            }

            var typeName = DialogTopicRecord.GetTopicTypeName(typeCode);
            var categoryNode = new EsmBrowserNode
            {
                DisplayName = typeName,
                Detail = $"({topics.Count})",
                NodeType = "Category",
                IconGlyph = DialogueMetadataBuilder.GetTopicTypeIcon(typeCode),
                HasUnrealizedChildren = true,
                DataObject = new SpeakerCategoryPickerData(speakerData.SpeakerFormId, typeCode, topics)
            };

            parentNode.Children.Add(new TreeViewNode { Content = categoryNode, HasUnrealizedChildren = true });
        }
    }

    /// <summary>
    ///     Expands a node by adding individual topic children.
    /// </summary>
    private static void ExpandTopicList(
        TreeViewNode parentNode,
        List<TopicDialogueNode> topics,
        uint? speakerFormId,
        uint? questFormId,
        bool showEditorIds = false)
    {
        foreach (var topic in topics
                     .OrderByDescending(t => t.Topic?.Priority ?? 0f)
                     .ThenBy(t => t.TopicName ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var displayName = showEditorIds
                ? DialogueViewerHelper.ResolveEditorIdDisplay(topic)
                : ResolveTopicDisplayName(topic);
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

            parentNode.Children.Add(new TreeViewNode { Content = topicNode });
        }
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
            SpeakerCategoryPickerData sc => sc.Topics.Any(t => t.TopicFormId == topicFormId),
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

    private static string ResolveTopicDisplayName(TopicDialogueNode topic)
    {
        // Use topic name for both quest and NPC browse modes — consistent with GECK's tree view.
        // FullName is preferred (player-visible prompt text), then TopicName, EditorId, FormID.
        var fullName = topic.Topic?.FullName ?? topic.TopicName;
        var editorId = topic.Topic?.EditorId;

        // For topics with 0 INFOs (failed reads from DMP), append EditorId to disambiguate
        // duplicates like "Goodbye." that all share the same FullName
        if (fullName != null && editorId != null && topic.InfoChain.Count == 0)
        {
            return $"{fullName} ({editorId})";
        }

        return fullName ?? editorId ?? $"0x{topic.TopicFormId:X8}";
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

    internal sealed record SpeakerCategoryPickerData(
        uint SpeakerFormId,
        byte TopicType,
        List<TopicDialogueNode> Topics);

    internal sealed record QuestTopicPickerData(uint QuestFormId, TopicDialogueNode Topic);

    internal sealed record SpeakerTopicPickerData(uint SpeakerFormId, TopicDialogueNode Topic);
}
