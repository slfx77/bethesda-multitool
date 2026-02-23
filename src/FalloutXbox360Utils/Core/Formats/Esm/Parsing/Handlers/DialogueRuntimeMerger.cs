using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Merges runtime DMP data into dialogue and topic records. Handles runtime hash table
///     probing, TESTopicInfo struct reading, and TESTopic quest-info list walking.
/// </summary>
internal sealed class DialogueRuntimeMerger(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    /// <summary>
    ///     Detect DIAL FormType from RuntimeEditorIds by cross-referencing known ESM DIAL FormIDs,
    ///     then merge runtime TESTopic struct data (type, flags, priority) into topic records.
    /// </summary>
    internal void MergeRuntimeDialogTopicData(List<DialogTopicRecord> topics)
    {
        // Build set of known DIAL FormIDs from ESM scan
        var knownDialFormIds = new HashSet<uint>(topics.Select(t => t.FormId));

        // Detect DIAL FormType by finding RuntimeEditorId entries matching known DIAL FormIDs
        byte? dialFormType = null;
        var formTypeCounts = new Dictionary<byte, int>();
        foreach (var formType in _context.ScanResult.RuntimeEditorIds
                     .Where(entry => knownDialFormIds.Contains(entry.FormId))
                     .Select(entry => entry.FormType))
        {
            formTypeCounts.TryGetValue(formType, out var count);
            formTypeCounts[formType] = count + 1;
        }

        if (formTypeCounts.Count > 0)
        {
            var best = formTypeCounts.MaxBy(kv => kv.Value);
            if (best.Value >= 2)
            {
                dialFormType = best.Key;
            }
        }

        if (!dialFormType.HasValue)
        {
            // Fallback: try FormType 0x45 (empirically verified as DIAL+INFO shared FormType).
            // Validate by attempting ReadRuntimeDialogTopic on a few candidate entries.
            const byte candidateFormType = 0x45;
            var validCount = 0;
            var testedCount = 0;
            foreach (var entry in _context.ScanResult.RuntimeEditorIds)
            {
                if (entry.FormType != candidateFormType || !entry.TesFormOffset.HasValue)
                {
                    continue;
                }

                if (++testedCount > 20)
                {
                    break;
                }

                var probe = _context.RuntimeReader!.ReadRuntimeDialogTopic(entry);
                if (probe != null)
                {
                    validCount++;
                }
            }

            if (validCount >= 3)
            {
                dialFormType = candidateFormType;
                Logger.Instance.Debug(
                    $"  [Semantic] DIAL FormType fallback: 0x{candidateFormType:X2} " +
                    $"({validCount}/{testedCount} passed ReadRuntimeDialogTopic validation)");
            }
        }

        if (!dialFormType.HasValue)
        {
            Logger.Instance.Debug("  [Semantic] Could not detect DIAL FormType - no runtime topic data");
            return;
        }

        Logger.Instance.Debug($"  [Semantic] Detected DIAL FormType: 0x{dialFormType.Value:X2} " +
                              $"({formTypeCounts.GetValueOrDefault(dialFormType.Value)} matches " +
                              $"from {knownDialFormIds.Count} known DIALs)");

        // Build FormID -> topic index for merging
        var topicByFormId = new Dictionary<uint, int>();
        for (var i = 0; i < topics.Count; i++)
        {
            topicByFormId.TryAdd(topics[i].FormId, i);
        }

        var mergedCount = 0;
        var newCount = 0;

        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != dialFormType.Value || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            var runtimeTopic = _context.RuntimeReader!.ReadRuntimeDialogTopic(entry);
            if (runtimeTopic == null)
            {
                continue;
            }

            if (topicByFormId.TryGetValue(entry.FormId, out var idx))
            {
                // Merge runtime data into existing ESM topic
                var existing = topics[idx];
                topics[idx] = existing with
                {
                    EditorId = existing.EditorId ?? entry.EditorId,
                    FullName = existing.FullName ?? runtimeTopic.FullName,
                    TopicType = runtimeTopic.TopicType,
                    Flags = runtimeTopic.Flags,
                    ResponseCount = (int)runtimeTopic.TopicCount,
                    Priority = runtimeTopic.Priority,
                    DummyPrompt = runtimeTopic.DummyPrompt
                };
                mergedCount++;
            }
            else
            {
                // New topic from runtime only
                topics.Add(new DialogTopicRecord
                {
                    FormId = entry.FormId,
                    EditorId = entry.EditorId,
                    FullName = runtimeTopic.FullName ?? entry.DisplayName,
                    TopicType = runtimeTopic.TopicType,
                    Flags = runtimeTopic.Flags,
                    ResponseCount = (int)runtimeTopic.TopicCount,
                    Priority = runtimeTopic.Priority,
                    DummyPrompt = runtimeTopic.DummyPrompt,
                    Offset = entry.TesFormOffset.Value,
                    IsBigEndian = true
                });
                newCount++;
            }
        }

        Logger.Instance.Debug(
            $"  [Semantic] Runtime DIAL merge: {mergedCount} merged, {newCount} new " +
            $"(total: {topics.Count})");
    }

    /// <summary>
    ///     Enrich dialogue records with runtime TESTopicInfo data from the hash table.
    ///     Matches dialogue FormIDs against RuntimeEditorIds to find corresponding entries,
    ///     then reads the TESTopicInfo struct to get speaker, quest, flags, difficulty, and prompt.
    ///     Only enriches existing records - new entries are created by MergeRuntimeDialogueTopicLinks.
    /// </summary>
    internal void MergeRuntimeDialogueData(List<DialogueRecord> dialogues)
    {
        // Build FormID -> runtime entry lookup from hash table
        var runtimeByFormId = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
        {
            if (entry.TesFormOffset.HasValue)
            {
                runtimeByFormId.TryAdd(entry.FormId, entry);
            }
        }

        var mergedCount = 0;

        for (var i = 0; i < dialogues.Count; i++)
        {
            var dialogue = dialogues[i];

            if (!runtimeByFormId.TryGetValue(dialogue.FormId, out var entry))
            {
                continue;
            }

            var runtimeInfo = _context.RuntimeReader!.ReadRuntimeDialogueInfo(entry);
            if (runtimeInfo == null)
            {
                continue;
            }

            dialogues[i] = dialogue with
            {
                EditorId = dialogue.EditorId ?? entry.EditorId ?? runtimeInfo.FormEditorId,
                PromptText = runtimeInfo.PromptText ?? dialogue.PromptText,
                InfoIndex = runtimeInfo.InfoIndex,
                InfoFlags = runtimeInfo.InfoFlags,
                InfoFlagsExt = runtimeInfo.InfoFlagsExt,
                Difficulty = runtimeInfo.Difficulty > 0 ? runtimeInfo.Difficulty : dialogue.Difficulty,
                SpeakerFormId = runtimeInfo.SpeakerFormId ?? dialogue.SpeakerFormId,
                QuestFormId = runtimeInfo.QuestFormId ?? dialogue.QuestFormId,
                SaidOnce = runtimeInfo.SaidOnce,
                AddTopics = runtimeInfo.AddTopicFormIds.Count > 0 && dialogue.AddTopics.Count == 0
                    ? runtimeInfo.AddTopicFormIds
                    : dialogue.AddTopics
            };
            mergedCount++;
        }

        Logger.Instance.Debug(
            $"  [Semantic] Runtime INFO enrich: {mergedCount}/{dialogues.Count} enriched " +
            $"(hashEntries={runtimeByFormId.Count})");
    }

    /// <summary>
    ///     Walk TESTopic.m_listQuestInfo for each runtime DIAL entry to build
    ///     Topic -> Quest and Topic -> [INFO] mappings. Sets TopicFormId and QuestFormId
    ///     on all linked dialogue records.
    /// </summary>
    internal void MergeRuntimeDialogueTopicLinks(
        List<DialogueRecord> dialogues,
        List<DialogTopicRecord> topics)
    {
        var dialFormType = DetectDialFormType(topics);
        if (!dialFormType.HasValue)
        {
            return;
        }

        var dialogueByFormId = BuildDialogueFormIdIndex(dialogues);
        var stats = new TopicLinkStats();

        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
        {
            if (entry.FormType != dialFormType.Value || !entry.TesFormOffset.HasValue)
            {
                continue;
            }

            var questLinks = _context.RuntimeReader!.WalkTopicQuestInfoList(entry);
            if (questLinks.Count == 0)
            {
                continue;
            }

            stats.TopicsWalked++;
            stats.TotalInfosFound += questLinks.Sum(l => l.InfoEntries.Count);

            ProcessTopicQuestLinks(dialogues, dialogueByFormId, entry.FormId, questLinks, stats);
        }

        Logger.Instance.Debug(
            $"  [Semantic] Topic->Quest walk: {stats.TopicsWalked} topics, " +
            $"{stats.TotalInfosFound} INFO ptrs, {stats.TotalInfosLinked} existing linked, " +
            $"{stats.NewInfoCount} new INFOs created " +
            $"(+{stats.TopicLinked} TopicFormId, +{stats.QuestLinked} QuestFormId)");
    }

    /// <summary>
    ///     Detect the FormType byte used for DIAL records in the runtime editor ID table.
    /// </summary>
    private byte? DetectDialFormType(List<DialogTopicRecord> topics)
    {
        var knownDialFormIds = new HashSet<uint>(topics.Select(t => t.FormId));
        var formTypeCounts = new Dictionary<byte, int>();

        foreach (var formType in _context.ScanResult.RuntimeEditorIds
                     .Where(entry => knownDialFormIds.Contains(entry.FormId))
                     .Select(entry => entry.FormType))
        {
            formTypeCounts.TryGetValue(formType, out var count);
            formTypeCounts[formType] = count + 1;
        }

        if (formTypeCounts.Count > 0)
        {
            var best = formTypeCounts.MaxBy(kv => kv.Value);
            if (best.Value >= 2)
            {
                return best.Key;
            }
        }

        // Fallback: use 0x45 (empirically verified shared DIAL+INFO FormType)
        const byte candidateFormType = 0x45;
        var hasEntries = _context.ScanResult.RuntimeEditorIds
            .Any(e => e.FormType == candidateFormType && e.TesFormOffset.HasValue);

        return hasEntries ? candidateFormType : null;
    }

    /// <summary>
    ///     Build a FormID -> list index lookup for dialogues.
    /// </summary>
    private static Dictionary<uint, int> BuildDialogueFormIdIndex(List<DialogueRecord> dialogues)
    {
        var index = new Dictionary<uint, int>();
        for (var i = 0; i < dialogues.Count; i++)
        {
            index.TryAdd(dialogues[i].FormId, i);
        }

        return index;
    }

    /// <summary>
    ///     Process quest links for a single topic, updating or creating dialogue records.
    /// </summary>
    private void ProcessTopicQuestLinks(
        List<DialogueRecord> dialogues,
        Dictionary<uint, int> dialogueByFormId,
        uint topicFormId,
        List<TopicQuestLink> questLinks,
        TopicLinkStats stats)
    {
        foreach (var link in questLinks)
        {
            foreach (var infoEntry in link.InfoEntries)
            {
                if (dialogueByFormId.TryGetValue(infoEntry.FormId, out var idx))
                {
                    UpdateExistingDialogue(dialogues, idx, topicFormId, link.QuestFormId, stats);
                }
                else
                {
                    TryCreateNewDialogue(dialogues, dialogueByFormId, infoEntry, topicFormId, link.QuestFormId, stats);
                }
            }
        }
    }

    /// <summary>
    ///     Update an existing dialogue record with topic and quest FormIds if not already set.
    /// </summary>
    private static void UpdateExistingDialogue(
        List<DialogueRecord> dialogues,
        int index,
        uint topicFormId,
        uint questFormId,
        TopicLinkStats stats)
    {
        var existing = dialogues[index];
        var updated = existing;

        if (!existing.TopicFormId.HasValue || existing.TopicFormId.Value == 0)
        {
            updated = updated with { TopicFormId = topicFormId };
            stats.TopicLinked++;
        }

        if (!existing.QuestFormId.HasValue || existing.QuestFormId.Value == 0)
        {
            updated = updated with { QuestFormId = questFormId };
            stats.QuestLinked++;
        }

        if (updated != existing)
        {
            dialogues[index] = updated;
            stats.TotalInfosLinked++;
        }
    }

    /// <summary>
    ///     Create a new dialogue record from a runtime TESTopicInfo pointer.
    /// </summary>
    private void TryCreateNewDialogue(
        List<DialogueRecord> dialogues,
        Dictionary<uint, int> dialogueByFormId,
        InfoPointerEntry infoEntry,
        uint topicFormId,
        uint questFormId,
        TopicLinkStats stats)
    {
        var runtimeInfo = _context.RuntimeReader!.ReadRuntimeDialogueInfoFromVA(infoEntry.VirtualAddress);
        if (runtimeInfo == null)
        {
            return;
        }

        var newDialogue = new DialogueRecord
        {
            FormId = infoEntry.FormId,
            EditorId = runtimeInfo.FormEditorId,
            TopicFormId = topicFormId,
            QuestFormId = questFormId,
            PromptText = runtimeInfo.PromptText,
            InfoIndex = runtimeInfo.InfoIndex,
            InfoFlags = runtimeInfo.InfoFlags,
            InfoFlagsExt = runtimeInfo.InfoFlagsExt,
            Difficulty = runtimeInfo.Difficulty,
            SpeakerFormId = runtimeInfo.SpeakerFormId,
            SaidOnce = runtimeInfo.SaidOnce,
            AddTopics = runtimeInfo.AddTopicFormIds,
            Offset = runtimeInfo.DumpOffset,
            IsBigEndian = true
        };

        dialogues.Add(newDialogue);
        dialogueByFormId.TryAdd(infoEntry.FormId, dialogues.Count - 1);
        stats.NewInfoCount++;
        stats.TopicLinked++;
        stats.QuestLinked++;
    }

    /// <summary>
    ///     Statistics for topic linking operations.
    /// </summary>
    private sealed class TopicLinkStats
    {
        public int NewInfoCount;
        public int QuestLinked;
        public int TopicLinked;
        public int TopicsWalked;
        public int TotalInfosFound;
        public int TotalInfosLinked;
    }
}
