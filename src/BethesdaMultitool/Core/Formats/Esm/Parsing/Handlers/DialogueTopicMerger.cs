using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Handles dialogue-to-topic linking, speaker propagation, EditorID convention matching,
///     and GRUP-based INFO-to-DIAL linking.
/// </summary>
internal sealed class DialogueTopicMerger(RecordParserContext context) : RecordHandlerBase(context)
{
    /// <summary>
    ///     Link dialogue records to quests by matching EditorID naming conventions.
    ///     Fallout NV INFO EditorIDs follow patterns like "{QuestPrefix}Topic{NNN}"
    ///     or "{QuestPrefix}{Speaker}Topic{NNN}". This is a heuristic fallback for
    ///     records not linked by the precise m_listQuestInfo walking.
    /// </summary>
    internal static void LinkDialogueByEditorIdConvention(
        List<DialogueRecord> dialogues,
        List<QuestRecord> quests)
    {
        // Build quest EditorID -> FormID index from the parsed quests list.
        // Quests already have EditorIDs from ESM scan + runtime merge.
        var questPrefixes = new Dictionary<string, uint>(StringComparer.OrdinalIgnoreCase);
        foreach (var quest in quests)
        {
            if (!string.IsNullOrEmpty(quest.EditorId))
            {
                questPrefixes.TryAdd(quest.EditorId, quest.FormId);
            }
        }

        // Sort quest prefixes by length descending for longest-match-first
        var sortedPrefixes = questPrefixes
            .OrderByDescending(kv => kv.Key.Length)
            .ToList();

        var linked = 0;
        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            // Skip if already has QuestFormId
            if (dialogue.QuestFormId.HasValue && dialogue.QuestFormId.Value != 0)
            {
                continue;
            }

            // Skip if no EditorID to match
            if (string.IsNullOrEmpty(dialogue.EditorId))
            {
                continue;
            }

            // Find longest matching quest prefix
            foreach (var (prefix, questFormId) in sortedPrefixes)
            {
                if (dialogue.EditorId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && dialogue.EditorId.Length > prefix.Length)
                {
                    dialogues[i] = dialogue with { QuestFormId = questFormId };
                    linked++;
                    break;
                }
            }
        }

        if (linked > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] EditorID convention matching: {linked} dialogues linked to quests " +
                $"({sortedPrefixes.Count} quest prefixes)");
        }
    }

    /// <summary>
    ///     Propagate topic-level speaker (TNAM) to INFO records that lack a speaker.
    ///     In Fallout NV, the speaker NPC is stored on the DIAL record's TNAM subrecord,
    ///     not per-INFO. This pass fills in SpeakerFormId for INFOs under topics with TNAM.
    /// </summary>
    internal static void PropagateTopicSpeakers(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> dialogTopics)
    {
        // Build TopicFormId -> SpeakerFormId map from topics that have TNAM
        var topicSpeakers = new Dictionary<uint, uint>();
        foreach (var topic in dialogTopics)
        {
            if (topic.SpeakerFormId.HasValue && topic.SpeakerFormId.Value != 0)
            {
                topicSpeakers.TryAdd(topic.FormId, topic.SpeakerFormId.Value);
            }
        }

        if (topicSpeakers.Count == 0)
        {
            return;
        }

        var propagated = 0;
        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            // Skip if already has a speaker
            if (dialogue.SpeakerFormId.HasValue && dialogue.SpeakerFormId.Value != 0)
            {
                continue;
            }

            // Skip if no topic link
            if (!dialogue.TopicFormId.HasValue || dialogue.TopicFormId.Value == 0)
            {
                continue;
            }

            // Look up topic-level speaker
            if (topicSpeakers.TryGetValue(dialogue.TopicFormId.Value, out var speakerFormId))
            {
                dialogues[i] = dialogue with { SpeakerFormId = speakerFormId };
                propagated++;
            }
        }

        if (propagated > 0)
        {
            Logger.Instance.Debug($"  Propagated topic-level speaker (TNAM) to {propagated:N0} dialogue records");
        }
    }

    /// <summary>
    ///     Propagate speaker from attributed INFOs to unattributed siblings within the same topic.
    ///     If all attributed lines in a topic share the same speaker, unattributed lines inherit it.
    /// </summary>
    internal static void PropagateTopicSiblingSpeakers(List<DialogueRecord> dialogues)
    {
        // Group by (TopicFormId, QuestFormId) to prevent cross-quest contamination.
        // Shared topics like GREETING contain lines from many quests — grouping by
        // topic alone would propagate one quest's voice type to all other quests' lines.
        var byTopicQuest = new Dictionary<(uint TopicId, uint QuestId), List<int>>();
        for (var i = 0; i < dialogues.Count; i++)
        {
            if (dialogues[i].TopicFormId is > 0)
            {
                var key = (dialogues[i].TopicFormId!.Value, dialogues[i].QuestFormId ?? 0);
                if (!byTopicQuest.TryGetValue(key, out var list))
                {
                    list = [];
                    byTopicQuest[key] = list;
                }

                list.Add(i);
            }
        }

        var propagated = 0;
        foreach (var (_, indices) in byTopicQuest)
        {
            if (indices.Count < 2)
            {
                continue;
            }

            var attributedIndices = new List<int>();
            var unattributedIndices = new List<int>();
            foreach (var i in indices)
            {
                if (HasAnySpeaker(dialogues[i]))
                    attributedIndices.Add(i);
                else
                    unattributedIndices.Add(i);
            }

            if (attributedIndices.Count == 0 || unattributedIndices.Count == 0)
            {
                continue;
            }

            // Try NPC speaker, then voice type, then faction
            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerFormId, (d, v) => d with { SpeakerFormId = v }, out var count))
            {
                propagated += count;
                continue;
            }

            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerVoiceTypeFormId, (d, v) => d with { SpeakerVoiceTypeFormId = v }, out count))
            {
                propagated += count;
                continue;
            }

            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerFactionFormId, (d, v) => d with { SpeakerFactionFormId = v }, out count))
            {
                propagated += count;
            }
        }

        if (propagated > 0)
        {
            Logger.Instance.Debug($"  Propagated topic-sibling speaker to {propagated:N0} dialogue records");
        }
    }

    /// <summary>
    ///     Propagate speaker from attributed INFOs to unattributed lines within the same quest.
    ///     Uses a 60% threshold to avoid propagating in mixed-speaker quests.
    /// </summary>
    internal static void PropagateQuestSpeakers(List<DialogueRecord> dialogues)
    {
        var byQuest = new Dictionary<uint, List<int>>();
        for (var i = 0; i < dialogues.Count; i++)
        {
            if (dialogues[i].QuestFormId is > 0)
            {
                var questId = dialogues[i].QuestFormId!.Value;
                if (!byQuest.TryGetValue(questId, out var list))
                {
                    list = [];
                    byQuest[questId] = list;
                }

                list.Add(i);
            }
        }

        var propagated = 0;
        foreach (var (_, indices) in byQuest)
        {
            var unattributedIndices = indices.Where(i => !HasAnySpeaker(dialogues[i])).ToList();
            if (unattributedIndices.Count == 0)
            {
                continue;
            }

            var attributedIndices = indices.Where(i => HasAnySpeaker(dialogues[i])).ToList();
            if (attributedIndices.Count == 0)
            {
                continue;
            }

            // Higher threshold (60%) for quest-level to avoid bad propagation
            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerVoiceTypeFormId, (d, v) => d with { SpeakerVoiceTypeFormId = v },
                    out var count, 0.6))
            {
                propagated += count;
                continue;
            }

            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerFactionFormId, (d, v) => d with { SpeakerFactionFormId = v },
                    out count, 0.6))
            {
                propagated += count;
                continue;
            }

            if (TryPropagateDominant(dialogues, attributedIndices, unattributedIndices,
                    d => d.SpeakerFormId, (d, v) => d with { SpeakerFormId = v },
                    out count, 0.6))
            {
                propagated += count;
            }
        }

        if (propagated > 0)
        {
            Logger.Instance.Debug($"  Propagated quest-level speaker to {propagated:N0} dialogue records");
        }
    }

    /// <summary>
    ///     Preserve raw type-7 Topic Children ancestry alongside runtime topic attribution.
    ///     A unique raw parent fills a missing <see cref="DialogueRecord.TopicFormId" />, but
    ///     never overwrites a populated runtime value. Multiple raw parents remain explicit
    ///     ambiguity rather than being collapsed by dictionary or scan order.
    /// </summary>
    internal void LinkInfoToTopicsByGroupOrder(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics)
    {
        var topicToInfoMap = Context.ScanResult.TopicToInfoMap;
        if (topicToInfoMap.Count == 0)
        {
            Logger.Instance.Debug("  [Semantic] No TopicToInfoMap available -- skipping GRUP-based linking");
            return;
        }

        // Build DIAL FormID -> topic list index for quest propagation and response-count derivation.
        var topicByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < topics.Count; i++)
        {
            topicByFormId.TryAdd(topics[i].FormId, i);
        }

        var rawParentsByInfo = new Dictionary<uint, HashSet<uint>>();
        foreach (var (dialFormId, infoFormIds) in topicToInfoMap)
        {
            if (dialFormId == 0)
            {
                continue;
            }

            foreach (var infoFormId in infoFormIds)
            {
                if (infoFormId == 0)
                {
                    continue;
                }

                if (!rawParentsByInfo.TryGetValue(infoFormId, out var parents))
                {
                    parents = [];
                    rawParentsByInfo[infoFormId] = parents;
                }

                parents.Add(dialFormId);
            }
        }

        var rawOnlyLinked = 0;
        var rawRuntimeAgreements = 0;
        var rawRuntimeConflicts = 0;
        var ambiguousRawParents = 0;
        var questLinked = 0;

        foreach (var (dialFormId, infoFormIds) in topicToInfoMap)
        {
            if (topicByFormId.TryGetValue(dialFormId, out var topicIndex) &&
                topics[topicIndex].ResponseCount == 0 &&
                infoFormIds.Count > 0)
            {
                topics[topicIndex] = topics[topicIndex] with { ResponseCount = infoFormIds.Count };
            }
        }

        for (var index = 0; index < dialogues.Count; index++)
        {
            var dialogue = dialogues[index];
            if (!rawParentsByInfo.TryGetValue(dialogue.FormId, out var rawParentSet))
            {
                continue;
            }

            var rawParents = rawParentSet.Order().ToList();
            var updated = dialogue with { RawParentTopicFormIds = rawParents };
            if (rawParents.Count > 1)
            {
                ambiguousRawParents++;
                dialogues[index] = updated;
                continue;
            }

            var rawParent = rawParents[0];
            var resolvedFromRaw = false;
            if (dialogue.TopicFormId is null or 0)
            {
                updated = updated with { TopicFormId = rawParent };
                resolvedFromRaw = true;
                rawOnlyLinked++;
            }
            else if (dialogue.TopicFormId == rawParent)
            {
                rawRuntimeAgreements++;
            }
            else
            {
                rawRuntimeConflicts++;
            }

            // Preserve the pre-existing runtime model on agreement or disagreement. Quest
            // propagation remains part of the raw-only fallback, matching the legacy ESM
            // path without allowing raw evidence to mutate an already-attributed INFO.
            if (resolvedFromRaw
                && updated.QuestFormId is null or 0
                && updated.TopicFormId is { } resolvedParent
                && topicByFormId.TryGetValue(resolvedParent, out var parentTopicIndex)
                && topics[parentTopicIndex].QuestFormId is > 0)
            {
                updated = updated with { QuestFormId = topics[parentTopicIndex].QuestFormId };
                questLinked++;
            }

            dialogues[index] = updated;
        }

        Logger.Instance.Debug(
            $"  [Semantic] Raw type-7 parent provenance: {rawOnlyLinked} raw-only INFO link(s), " +
            $"{rawRuntimeAgreements} runtime/raw agreement(s), {rawRuntimeConflicts} conflict(s), " +
            $"{ambiguousRawParents} ambiguous raw parent set(s), {questLinked} quest propagation(s) " +
            $"(from {topicToInfoMap.Count} topics, {dialogues.Count} INFOs)");
    }

    private static bool HasAnySpeaker(DialogueRecord d)
    {
        return d.SpeakerFormId is > 0 || d.SpeakerFactionFormId is > 0 ||
               d.SpeakerRaceFormId is > 0 || d.SpeakerVoiceTypeFormId is > 0;
    }

    /// <summary>
    ///     Find the dominant (most common) value among attributed lines and propagate it to unattributed ones.
    ///     Returns true if propagation was performed.
    /// </summary>
    private static bool TryPropagateDominant(
        List<DialogueRecord> dialogues,
        List<int> attributedIndices,
        List<int> unattributedIndices,
        Func<DialogueRecord, uint?> selector,
        Func<DialogueRecord, uint, DialogueRecord> updater,
        out int propagatedCount,
        double minRatio = 0.5)
    {
        propagatedCount = 0;

        // Find the most common non-zero value
        var values = attributedIndices
            .Select(i => selector(dialogues[i]))
            .Where(v => v is > 0)
            .GroupBy(v => v!.Value)
            .Select(g => (Value: g.Key, Count: g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        if (values.Count == 0)
        {
            return false;
        }

        var dominant = values[0];
        var total = attributedIndices.Count(i => selector(dialogues[i]) is > 0);
        if (total == 0 || (double)dominant.Count / total < minRatio)
        {
            return false;
        }

        // Propagate to unattributed lines
        foreach (var idx in unattributedIndices)
        {
            dialogues[idx] = updater(dialogues[idx], dominant.Value);
            propagatedCount++;
        }

        return propagatedCount > 0;
    }
}
