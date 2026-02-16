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
            foreach (var linked in infoNode.LinkedTopics)
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
                    info.LinkedTopics.Any(linked => linked.TopicFormId == currentTopic.TopicFormId)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    ///     Resolves the best prompt text for a player choice button, using a priority chain:
    ///     SourceInfo PromptText → LinkedTopic INFO PromptText → DummyPrompt → FullName
    ///     → Response text → TopicName → EditorId → "[Continue]"
    /// </summary>
    public static string ResolvePromptText(InfoDialogueNode sourceInfo, TopicDialogueNode linkedTopic)
    {
        // 1. Source INFO prompt text (the line that leads to this topic)
        if (!string.IsNullOrEmpty(sourceInfo.Info.PromptText))
        {
            return sourceInfo.Info.PromptText;
        }

        // 2. First INFO in linked topic with prompt text
        var firstWithPrompt = linkedTopic.InfoChain
            .FirstOrDefault(i => !string.IsNullOrEmpty(i.Info.PromptText));
        if (firstWithPrompt != null)
        {
            return firstWithPrompt.Info.PromptText!;
        }

        // 3. Topic-level dummy prompt
        if (!string.IsNullOrEmpty(linkedTopic.Topic?.DummyPrompt))
        {
            return linkedTopic.Topic.DummyPrompt;
        }

        // 4. Topic display name (FullName)
        if (!string.IsNullOrEmpty(linkedTopic.Topic?.FullName))
        {
            return linkedTopic.Topic.FullName;
        }

        // 5. First response text — more readable than EditorId for topics without FullName
        var firstResponseText = linkedTopic.InfoChain
            .SelectMany(i => i.Info.Responses)
            .Select(r => r.Text)
            .FirstOrDefault(t => !string.IsNullOrEmpty(t));
        if (firstResponseText != null)
        {
            return firstResponseText.Length > 100 ? firstResponseText[..97] + "..." : firstResponseText;
        }

        // 6. Topic name / EditorId fallback
        if (!string.IsNullOrEmpty(linkedTopic.TopicName))
        {
            return linkedTopic.TopicName;
        }

        if (!string.IsNullOrEmpty(linkedTopic.Topic?.EditorId))
        {
            return linkedTopic.Topic.EditorId;
        }

        return "[Continue]";
    }
}
