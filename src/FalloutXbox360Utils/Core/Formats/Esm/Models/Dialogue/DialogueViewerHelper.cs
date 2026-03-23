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
        List<InfoDialogueNode> chain, uint? questFilter, uint? speakerFilter,
        bool strictQuestFilter = false)
    {
        var result = chain;

        if (questFilter != null)
        {
            var questFiltered = result
                .Where(i => i.Info.QuestFormId == questFilter.Value)
                .ToList();
            // When strict, always apply the filter even if it produces no results.
            // This prevents shared topics (GREETING, Hello) from showing thousands of
            // unrelated INFOs when viewed under a specific quest.
            if (questFiltered.Count > 0 || strictQuestFilter)
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
    ///     Linked topic's own text (FullName → DummyPrompt → INFO PromptText) → sourceInfo PromptText → fallbacks → "[Continue]".
    ///     The linked topic's own text takes priority over the source INFO's prompt to avoid
    ///     duplicate display when multiple topics share the same source INFO prompt text.
    /// </summary>
    public static string ResolvePromptText(InfoDialogueNode sourceInfo, TopicDialogueNode linkedTopic)
    {
        // Prefer the linked topic's own identifying text first — this distinguishes
        // topics that share the same source INFO (e.g., VCasaJimmySexYes vs VCasaJimmySexNo).
        var topicOwnText = ResolveTopicOwnText(linkedTopic);
        if (topicOwnText != null)
        {
            return topicOwnText;
        }

        // Fall back to the source INFO's prompt text
        if (!string.IsNullOrEmpty(sourceInfo.Info.PromptText))
        {
            return sourceInfo.Info.PromptText;
        }

        // Last resort: response text, TopicName, EditorId
        return ResolveTopicFallbackText(linkedTopic) ?? "[Continue]";
    }

    /// <summary>
    ///     Returns the topic's EditorID with FormID suffix for the "Show Editor ID" display mode.
    /// </summary>
    public static string ResolveEditorIdDisplay(TopicDialogueNode topic)
    {
        var editorId = topic.Topic?.EditorId;
        return !string.IsNullOrEmpty(editorId)
            ? $"{editorId} (0x{topic.TopicFormId:X8})"
            : $"0x{topic.TopicFormId:X8}";
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
    ///     Resolves the topic's own identifying text: FullName → DummyPrompt → INFO PromptText.
    ///     These are the topic's "proper" labels that distinguish it from sibling topics.
    /// </summary>
    private static string? ResolveTopicOwnText(TopicDialogueNode topic)
    {
        if (!string.IsNullOrEmpty(topic.Topic?.FullName))
        {
            return topic.Topic.FullName;
        }

        if (!string.IsNullOrEmpty(topic.Topic?.DummyPrompt))
        {
            return topic.Topic.DummyPrompt;
        }

        var firstWithPrompt = topic.InfoChain
            .FirstOrDefault(i => !string.IsNullOrEmpty(i.Info.PromptText));
        if (firstWithPrompt != null)
        {
            return firstWithPrompt.Info.PromptText!;
        }

        return null;
    }

    /// <summary>
    ///     Fallback text resolution: response text → TopicName → EditorId.
    ///     Used when neither the topic's own text nor the source INFO's prompt is available.
    /// </summary>
    private static string? ResolveTopicFallbackText(TopicDialogueNode topic)
    {
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
    ///     Full topic text resolution chain used by ResolveTopicDisplayText.
    ///     Priority: FullName → DummyPrompt → INFO PromptText → Response text → TopicName → EditorId.
    /// </summary>
    private static string? ResolveTopicText(TopicDialogueNode topic)
    {
        return ResolveTopicOwnText(topic) ?? ResolveTopicFallbackText(topic);
    }

    /// <summary>
    ///     Searches the entire dialogue tree for a GREETING topic that has at least one INFO
    ///     matching the given speaker. GREETING topics are shared across many NPCs, so they
    ///     may not be in a specific speaker's topic list from BuildTopicsBySpeaker.
    /// </summary>
    public static TopicDialogueNode? FindGreetingTopicForSpeaker(DialogueTreeResult tree, uint speakerFormId)
    {
        TopicDialogueNode? bestMatch = null;
        var bestInfoCount = 0;

        void Search(IEnumerable<TopicDialogueNode> topics)
        {
            foreach (var topic in topics)
            {
                if (topic.Topic?.EditorId?.Contains("GREETING", StringComparison.OrdinalIgnoreCase) != true)
                {
                    continue;
                }

                var matchingInfos = topic.InfoChain.Count(i => i.Info.SpeakerFormId == speakerFormId);
                if (matchingInfos > bestInfoCount)
                {
                    bestInfoCount = matchingInfos;
                    bestMatch = topic;
                }
            }
        }

        foreach (var quest in tree.QuestTrees.Values)
        {
            Search(quest.Topics);
        }

        Search(tree.OrphanTopics);
        return bestMatch;
    }

    /// <summary>
    ///     Finds the parent topic that links to the given topic, searching within a specific
    ///     quest if a filter is active, or across all quests otherwise.
    /// </summary>
    public static TopicDialogueNode? FindParentTopic(
        TopicDialogueNode currentTopic, DialogueTreeResult dialogueTree, uint? questFilter)
    {
        if (questFilter.HasValue &&
            dialogueTree.QuestTrees.TryGetValue(questFilter.Value, out var filteredQuest))
        {
            var result = FindParentTopicIn(currentTopic, filteredQuest.Topics);
            if (result != null)
            {
                return result;
            }
        }
        else
        {
            foreach (var quest in dialogueTree.QuestTrees.Values)
            {
                var result = FindParentTopicIn(currentTopic, quest.Topics);
                if (result != null)
                {
                    return result;
                }
            }
        }

        return FindParentTopicIn(currentTopic, dialogueTree.OrphanTopics);
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
