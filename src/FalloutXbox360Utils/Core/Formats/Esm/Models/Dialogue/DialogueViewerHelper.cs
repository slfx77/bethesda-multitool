namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Static helpers for dialogue viewer logic, extracted for testability.
///     These methods are pure functions with no UI dependencies.
/// </summary>
internal static class DialogueViewerHelper
{
    /// <summary>
    ///     Collects linked topics from an InfoChain, excluding a specific topic FormId
    ///     to prevent self-references and infinite cycles.
    /// </summary>
    public static Dictionary<uint, (TopicDialogueNode Topic, InfoDialogueNode SourceInfo)>
        CollectLinkedTopics(List<InfoDialogueNode> chain, uint excludeTopicFormId)
    {
        var result = new Dictionary<uint, (TopicDialogueNode Topic, InfoDialogueNode SourceInfo)>();
        foreach (var infoNode in chain)
        {
            foreach (var linked in infoNode.ChoiceTopics)
            {
                if (linked.TopicFormId != excludeTopicFormId)
                {
                    result.TryAdd(linked.TopicFormId, (linked, infoNode));
                }
            }
        }

        return result;
    }

    /// <summary>
    ///     Filters an InfoChain by quest and/or speaker, falling back to the full chain
    ///     if filters produce no results.
    /// </summary>
    public static List<InfoDialogueNode> FilterInfoChain(
        List<InfoDialogueNode> chain, uint? questFilter, uint? speakerFilter)
    {
        var result = chain;

        if (questFilter != null)
        {
            var questFiltered = result
                .Where(i => i.Info.QuestFormId == questFilter.Value)
                .ToList();
            if (questFiltered.Count > 0)
            {
                result = questFiltered;
            }
        }

        if (speakerFilter != null)
        {
            var speakerFiltered = result
                .Where(i => i.Info.SpeakerFormId == speakerFilter.Value)
                .ToList();
            if (speakerFiltered.Count > 0)
            {
                result = speakerFiltered;
            }
        }

        return result;
    }

    /// <summary>
    ///     Finds the topic that links TO the given topic within a specific set of topics.
    /// </summary>
    public static TopicDialogueNode? FindParentTopicIn(
        TopicDialogueNode currentTopic, IEnumerable<TopicDialogueNode> candidates)
    {
        foreach (var candidate in candidates)
        {
            if (candidate.TopicFormId == currentTopic.TopicFormId)
            {
                continue;
            }

            if (candidate.InfoChain.Any(info =>
                    info.ChoiceTopics.Any(linked => linked.TopicFormId == currentTopic.TopicFormId)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     Resolves the best prompt text for a player choice button, using a priority chain:
    ///     SourceInfo PromptText → shared topic chain → "[Continue]"
    /// </summary>
    public static string ResolvePromptText(InfoDialogueNode sourceInfo, TopicDialogueNode linkedTopic)
    {
        if (!string.IsNullOrEmpty(sourceInfo.Info.PromptText))
        {
            return sourceInfo.Info.PromptText;
        }

        return ResolveTopicText(linkedTopic) ?? "[Continue]";
    }

    /// <summary>
    ///     Resolves the best display text for a topic without relying on a source INFO.
    ///     Used for "topics unlocked" where the source INFO is the current conversation's INFO
    ///     (not a TCLT link), so its PromptText is irrelevant.
    /// </summary>
    public static string ResolveTopicDisplayText(TopicDialogueNode topic)
    {
        return ResolveTopicText(topic) ?? $"0x{topic.TopicFormId:X8}";
    }

    /// <summary>
    ///     Core topic text resolution chain shared by ResolvePromptText and ResolveTopicDisplayText.
    ///     Priority: INFO PromptText → DummyPrompt → FullName → Response text → TopicName → EditorId.
    ///     Returns null if none match, letting the caller provide a final fallback.
    /// </summary>
    private static string? ResolveTopicText(TopicDialogueNode topic)
    {
        var firstWithPrompt = topic.InfoChain
            .FirstOrDefault(i => !string.IsNullOrEmpty(i.Info.PromptText));
        if (firstWithPrompt != null)
        {
            return firstWithPrompt.Info.PromptText!;
        }

        if (!string.IsNullOrEmpty(topic.Topic?.DummyPrompt))
        {
            return topic.Topic.DummyPrompt;
        }

        if (!string.IsNullOrEmpty(topic.Topic?.FullName))
        {
            return topic.Topic.FullName;
        }

        var firstResponseText = topic.InfoChain
            .SelectMany(i => i.Info.Responses)
            .Select(r => r.Text)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));
        if (firstResponseText != null)
        {
            return firstResponseText.Length > 100 ? firstResponseText[..97] + "..." : firstResponseText;
        }

        if (!string.IsNullOrEmpty(topic.TopicName))
        {
            return topic.TopicName;
        }

        if (!string.IsNullOrEmpty(topic.Topic?.EditorId))
        {
            return topic.Topic.EditorId;
        }

        return null;
    }

    /// <summary>
    ///     Resolves the quest to display in the dialogue header. When a filter is active,
    ///     uses the first filtered INFO's quest rather than the topic tree grouping, because
    ///     shared topics (e.g., combat dialogue) may be grouped under a different quest than
    ///     the filtered INFOs.
    /// </summary>
    public static uint? ResolveHeaderQuest(
        TopicDialogueNode topic,
        List<InfoDialogueNode> filteredInfoChain,
        DialogueTreeResult dialogueTree,
        bool hasActiveFilter)
    {
        // When a filter is active, prefer the quest from filtered INFOs
        if (hasActiveFilter)
        {
            var questFromInfo = filteredInfoChain
                .Select(i => i.Info.QuestFormId)
                .FirstOrDefault(q => q is > 0);
            if (questFromInfo is > 0)
            {
                return questFromInfo.Value;
            }
        }

        // Fallback: find quest from the topic tree grouping
        var quest = dialogueTree.QuestTrees.Values
            .FirstOrDefault(q => q.Topics.Contains(topic));
        return quest?.QuestFormId;
    }
}
