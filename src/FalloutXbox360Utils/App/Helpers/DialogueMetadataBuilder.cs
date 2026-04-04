using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;

namespace FalloutXbox360Utils;

/// <summary>
///     Pure data/logic helpers for building dialogue metadata structures.
///     No UI dependencies — all methods are static and operate on model types.
/// </summary>
internal static class DialogueMetadataBuilder
{
    /// <summary>
    ///     Builds a speaker-to-topics index from the dialogue tree, using NPCs with full names
    ///     as the preferred speaker attribution.
    /// </summary>
    public static Dictionary<uint, List<TopicDialogueNode>> BuildTopicsBySpeaker(
        DialogueTreeResult tree, HashSet<uint> npcsWithFullName)
    {
        var index = new Dictionary<uint, List<TopicDialogueNode>>();

        void IndexTopics(IEnumerable<TopicDialogueNode> topics)
        {
            foreach (var topic in topics)
            {
                var speakerId = ResolveTopicSpeaker(topic, npcsWithFullName);

                if (speakerId is > 0)
                {
                    if (!index.TryGetValue(speakerId.Value, out var list))
                    {
                        list = [];
                        index[speakerId.Value] = list;
                    }

                    list.Add(topic);
                }
            }
        }

        foreach (var quest in tree.QuestTrees.Values)
        {
            IndexTopics(quest.Topics);
        }

        IndexTopics(tree.OrphanTopics);

        return index;
    }

    /// <summary>
    ///     Resolves the speaker for a topic using consensus across all INFOs,
    ///     preferring NPCs with FullName (real named NPCs) over marker/template NPCs.
    /// </summary>
    public static uint? ResolveTopicSpeaker(TopicDialogueNode topic, HashSet<uint> npcsWithFullName)
    {
        // 1. Topic-level TNAM is highest authority
        if (topic.Topic?.SpeakerFormId is > 0)
        {
            return topic.Topic.SpeakerFormId;
        }

        // 2. Consensus across all INFOs, preferring NPCs with FullName
        var speakerCounts = new Dictionary<uint, int>();
        foreach (var speakerId in topic.InfoChain
                     .Select(i => i.Info.SpeakerFormId)
                     .Where(id => id is > 0))
        {
            speakerCounts.TryGetValue(speakerId!.Value, out var c);
            speakerCounts[speakerId.Value] = c + 1;
        }

        if (speakerCounts.Count == 0)
        {
            // Last resort: use voice type FormID from GetIsVoiceType conditions
            var voiceType = topic.InfoChain
                .Select(i => i.Info.SpeakerVoiceTypeFormId)
                .FirstOrDefault(id => id is > 0);
            return voiceType;
        }

        // Prefer speakers with FullName (real named NPCs) over marker/template NPCs
        var withName = speakerCounts
            .Where(kv => npcsWithFullName.Contains(kv.Key))
            .ToList();

        if (withName.Count > 0)
        {
            return withName.MaxBy(kv => kv.Value).Key;
        }

        // Fallback: most common overall
        return speakerCounts.MaxBy(kv => kv.Value).Key;
    }

    /// <summary>
    ///     Builds a FormID-to-TopicDialogueNode index from the dialogue tree,
    ///     covering both topic FormIDs and individual INFO FormIDs.
    /// </summary>
    public static Dictionary<uint, TopicDialogueNode> BuildDialogueFormIdIndex(DialogueTreeResult tree)
    {
        var index = new Dictionary<uint, TopicDialogueNode>();

        void IndexTopics(IEnumerable<TopicDialogueNode> topics)
        {
            foreach (var topic in topics)
            {
                // Index the topic itself
                index.TryAdd(topic.TopicFormId, topic);

                // Index each INFO by its FormID
                foreach (var info in topic.InfoChain)
                {
                    index.TryAdd(info.Info.FormId, topic);
                }
            }
        }

        foreach (var quest in tree.QuestTrees.Values)
        {
            IndexTopics(quest.Topics);
        }

        IndexTopics(tree.OrphanTopics);

        return index;
    }

    /// <summary>
    ///     Filters a list of topics by a search query, matching topic name, EditorId,
    ///     FullName, prompt text, and response text.
    /// </summary>
    public static List<TopicDialogueNode> FilterTopics(List<TopicDialogueNode> topics, string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return topics;
        }

        return topics.Where(t => TopicMatchesQuery(t, query)).ToList();
    }

    /// <summary>
    ///     Tests whether a topic matches a search query across multiple text fields.
    /// </summary>
    public static bool TopicMatchesQuery(TopicDialogueNode topic, string query)
    {
        // Match topic name / EditorId
        if (topic.TopicName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (topic.Topic?.EditorId?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        if (topic.Topic?.FullName?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }

        // Match response text or prompt text
        return topic.InfoChain.Any(entry =>
            entry.Info.PromptText?.Contains(query, StringComparison.OrdinalIgnoreCase) == true ||
            entry.Info.Responses.Any(r => r.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true));
    }

    /// <summary>
    ///     Checks whether two strings are similar enough that showing both would look duplicated.
    ///     Returns true if one starts with the other (case-insensitive).
    /// </summary>
    public static bool IsSimilarText(string a, string b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b))
        {
            return false;
        }

        return a.StartsWith(b, StringComparison.OrdinalIgnoreCase) ||
               b.StartsWith(a, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    ///     Detects [SUCCEEDED] or [FAILED] prefixes in prompt text (speech challenge outcomes).
    ///     If found, strips the prefix from the prompt text and returns the outcome label.
    /// </summary>
    public static string? DetectChallengeOutcome(ref string promptText)
    {
        if (promptText.StartsWith("[SUCCEEDED]", StringComparison.OrdinalIgnoreCase))
        {
            promptText = promptText["[SUCCEEDED]".Length..].TrimStart();
            return "SUCCEEDED";
        }

        if (promptText.StartsWith("[FAILED]", StringComparison.OrdinalIgnoreCase))
        {
            promptText = promptText["[FAILED]".Length..].TrimStart();
            return "FAILED";
        }

        return null;
    }

    /// <summary>
    ///     Returns the icon glyph for a dialogue topic type code.
    /// </summary>
    public static string GetTopicTypeIcon(byte topicType)
    {
        return topicType switch
        {
            0 => "\uE8BD", // Topic - chat
            1 => "\uE8BD", // Conversation - chat
            2 => "\uEC05", // Combat - swords
            3 => "\uE8BD", // Persuasion - chat
            4 => "\uE7B3", // Detection - eye
            5 => "\uE8F1", // Service - settings
            6 => "\uE8BD", // Miscellaneous - chat
            7 => "\uE767", // Radio - radio
            _ => "\uE8BD"
        };
    }
}
