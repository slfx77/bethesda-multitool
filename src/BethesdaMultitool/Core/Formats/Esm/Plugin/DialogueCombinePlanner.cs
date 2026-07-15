using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

internal sealed record SharedDialogueInfoOverlay(
    DialogueRecord PrototypeInfo,
    MasterDialogueInfo MasterInfo);

internal sealed record DialogueCombinePlan(
    IReadOnlyList<DialogTopicRecord> NewTopics,
    IReadOnlyList<DialogueRecord> NewInfos,
    IReadOnlyList<SharedDialogueInfoOverlay> SharedInfoOverlays,
    IReadOnlySet<uint> RetailCoveredSpeakers,
    int DuplicateInfosCollapsed,
    int SystemInfosSuppressed,
    int CutInfosRehomed);

/// <summary>
///     Classifies prototype dialogue before FormID allocation. Shared INFOs become
///     byte-preserving retail overlays; safe prototype-only GREETING content is rehomed
///     onto scoped top-level topics; covered retail speakers never receive a synthesized
///     GREETING that can shadow their retail first-meet INFO.
/// </summary>
internal static class DialogueCombinePlanner
{
    private const uint GreetingDialFormId = 0x000000C8;
    private const uint FirstNonSystemFormId = 0x00001000;
    /// <summary>
    ///     Collapse duplicate carved INFO identities before CSV backfill. Doing this while
    ///     source provenance is still visible guarantees a direct non-placeholder DMP line
    ///     beats a CSV-filled placeholder from another duplicate capture.
    /// </summary>
    public static int DeduplicateInPlace(List<DialogueRecord> infos)
    {
        var deduplicated = DeduplicateInfos(infos, out var collapsed);
        if (collapsed == 0)
        {
            return 0;
        }

        infos.Clear();
        infos.AddRange(deduplicated);
        return collapsed;
    }

    public static DialogueCombinePlan Build(
        IReadOnlyList<DialogTopicRecord> topics,
        IReadOnlyList<DialogueRecord> infos,
        NewVsOverrideClassifier classifier,
        MasterDialogueIndex masterIndex,
        IEnumerable<uint> masterFormIds,
        IEnumerable<uint>? additionalValidFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var deduplicatedInfos = DeduplicateInfos(infos, out var duplicateInfosCollapsed);
        var newTopics = topics
            .Where(topic => !classifier.IsOverride(topic.FormId))
            .GroupBy(static topic => topic.FormId)
            .Select(static group => group.First())
            .ToList();
        var newInfos = new List<DialogueRecord>();
        var overlays = new List<SharedDialogueInfoOverlay>();
        var liveFormIds = new HashSet<uint>(masterFormIds);
        if (additionalValidFormIds is not null)
        {
            liveFormIds.UnionWith(additionalValidFormIds);
        }

        var occupiedFormIds = new HashSet<uint>(liveFormIds);
        occupiedFormIds.UnionWith(topics.Select(static topic => topic.FormId));
        occupiedFormIds.UnionWith(deduplicatedInfos.Select(static info => info.FormId));
        var nextSyntheticSourceId = 0xFFFFFFFEu;
        var editorIdCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var suppressed = 0;
        var rehomed = 0;

        foreach (var info in deduplicatedInfos)
        {
            if (classifier.IsOverride(info.FormId))
            {
                if (!masterIndex.TryGetInfo(info.FormId, out var masterInfo))
                {
                    // A same-FormID non-INFO collision is not safe dialogue content.
                    suppressed++;
                    continue;
                }

                overlays.Add(new SharedDialogueInfoOverlay(info, masterInfo));
                // Never append unmatched prototype slots to the retail INFO itself. A cut
                // slot may be rehomed only when it independently satisfies the strict cut-
                // topic gate below, including an explicit player-facing PromptText. This
                // retains intentional cut choices without ever exposing NAM1 NPC response
                // text as a global dialogue option (the Sunny v122 regression).
                var masterResponseNumbers = DialogueInfoOverlayWriter.GetMasterResponseNumbers(masterInfo.Record);
                var extraResponses = DialogueInfoOverlayWriter.IndexPrototypeResponses(info.Responses)
                    .Where(pair => !masterResponseNumbers.Contains(pair.Key))
                    .OrderBy(static pair => pair.Key)
                    .Select(static pair => pair.Value)
                    .ToList();
                var masterScope = ResolveMasterScope(masterInfo, info);
                if (extraResponses.Count > 0
                    && TryCreateRehomedCut(
                        info, extraResponses, masterScope.QuestFormId, masterScope.SpeakerFormId,
                        liveFormIds, remapTable, occupiedFormIds, editorIdCounts,
                        ref nextSyntheticSourceId, out var cutTopic, out var cutInfo))
                {
                    newTopics.Add(cutTopic);
                    newInfos.Add(cutInfo);
                    rehomed++;
                }

                continue;
            }

            var exactSpeaker = DialogueSpeakerBinding.GetExactSpeaker(info);
            var isCoveredSystemInfo = info.TopicFormId is { } parent
                                      && parent < FirstNonSystemFormId
                                      && exactSpeaker is { } speaker
                                      && masterIndex.HasExactGreetingCoverage(speaker);
            if (!isCoveredSystemInfo)
            {
                newInfos.Add(info);
                continue;
            }

            // GREETING is player-initiated content and may be recovered as a normal top-
            // level choice. HELLO/combat/service system barks have runtime semantics that
            // cannot be reproduced safely as menu topics, so covered-speaker copies stay
            // suppressed and retail remains authoritative.
            if (info.TopicFormId == GreetingDialFormId
                && TryCreateRehomedCut(
                    info, info.Responses, null, null,
                    liveFormIds, remapTable, occupiedFormIds, editorIdCounts,
                    ref nextSyntheticSourceId, out var greetingTopic, out var greetingInfo))
            {
                newTopics.Add(greetingTopic);
                newInfos.Add(greetingInfo);
                rehomed++;
            }

            suppressed++;
        }

        var retailCoveredSpeakers = deduplicatedInfos
            .Select(DialogueSpeakerBinding.GetExactSpeaker)
            .Where(speaker => speaker is { } value && masterIndex.HasExactGreetingCoverage(value))
            .Select(static speaker => speaker!.Value)
            .ToHashSet();
        RebuildScopedGreetingLinks(newTopics, newInfos);
        PromoteCoveredSpeakerRoots(newTopics, newInfos, retailCoveredSpeakers);

        return new DialogueCombinePlan(
            newTopics,
            newInfos,
            overlays.OrderBy(static overlay => overlay.MasterInfo.FileOrder).ToList(),
            retailCoveredSpeakers,
            duplicateInfosCollapsed,
            suppressed,
            rehomed);
    }

    /// <summary>
    ///     Runtime GREETING captures can contain a reconstructed menu snapshot rather than
    ///     authored flow: xex21 carried the union of many speakers' TCLT choices on each
    ///     prototype greeting, which exposed FortWaterBarrelWarning globally. Rebuild only
    ///     TCLT from exact-speaker/exact-quest player-topic roots. Every other INFO field is
    ///     retained, including responses, conditions, flags, and result-script blocks.
    /// </summary>
    private static void RebuildScopedGreetingLinks(
        IReadOnlyList<DialogTopicRecord> topics,
        List<DialogueRecord> infos)
    {
        // Captured greetings may already link a valid inferred root even when a different
        // topic in the pair carries the explicit TopLevel bit. Include both categories for
        // the sanitizer's allow-list; the synthesizer itself retains its established
        // explicit-root precedence.
        var rootsByPair = GreetingEntrySynthesizer.SelectScopedEntryTopics(
            topics, infos, includeAllGraphRoots: true);
        for (var index = 0; index < infos.Count; index++)
        {
            var greeting = infos[index];
            if (greeting.TopicFormId != GreetingDialFormId)
            {
                continue;
            }

            var speaker = DialogueSpeakerBinding.GetExactSpeaker(greeting).GetValueOrDefault();
            var quest = greeting.QuestFormId.GetValueOrDefault();
            IReadOnlyList<uint> links = [];
            if (speaker != 0
                && quest != 0
                && rootsByPair.TryGetValue((speaker, quest), out var roots))
            {
                var rootSet = roots.ToHashSet();
                var retainedRoots = greeting.LinkToTopics
                    .Where(rootSet.Contains)
                    .Distinct()
                    .ToList();
                // Prefer the authored/recovered ordering when it contains scoped roots.
                // If the runtime snapshot omitted every valid root, reconstruct them so a
                // prototype-only NPC is not left with a Goodbye-only menu.
                links = retainedRoots.Count > 0 ? retainedRoots : roots;
            }

            infos[index] = greeting with { LinkToTopics = [..links] };
        }
    }

    private static List<DialogueRecord> DeduplicateInfos(
        IReadOnlyList<DialogueRecord> infos,
        out int duplicatesCollapsed)
    {
        var result = new List<DialogueRecord>(infos.Count);
        var indexByFormId = new Dictionary<uint, int>();
        duplicatesCollapsed = 0;
        foreach (var info in infos)
        {
            if (info.FormId == 0 || !indexByFormId.TryGetValue(info.FormId, out var existingIndex))
            {
                if (info.FormId != 0)
                {
                    indexByFormId[info.FormId] = result.Count;
                }

                result.Add(info);
                continue;
            }

            result[existingIndex] = MergeDuplicate(result[existingIndex], info);
            duplicatesCollapsed++;
        }

        return result;
    }

    private static DialogueRecord MergeDuplicate(DialogueRecord first, DialogueRecord next)
    {
        var mergedResponses = DialogueInfoOverlayWriter.IndexPrototypeResponses(first.Responses);
        foreach (var (number, response) in DialogueInfoOverlayWriter.IndexPrototypeResponses(next.Responses))
        {
            if (!mergedResponses.TryGetValue(number, out var existing)
                || (!DialogueInfoOverlayWriter.IsMeaningfulText(existing.Text)
                    && DialogueInfoOverlayWriter.IsMeaningfulText(response.Text)))
            {
                mergedResponses[number] = response;
            }
        }

        return first with
        {
            TopicFormId = first.TopicFormId is > 0 ? first.TopicFormId : next.TopicFormId,
            QuestFormId = first.QuestFormId is > 0 ? first.QuestFormId : next.QuestFormId,
            SpeakerFormId = first.SpeakerFormId is > 0 ? first.SpeakerFormId : next.SpeakerFormId,
            SpeakerFactionFormId = first.SpeakerFactionFormId is > 0
                ? first.SpeakerFactionFormId
                : next.SpeakerFactionFormId,
            SpeakerRaceFormId = first.SpeakerRaceFormId is > 0
                ? first.SpeakerRaceFormId
                : next.SpeakerRaceFormId,
            SpeakerVoiceTypeFormId = first.SpeakerVoiceTypeFormId is > 0
                ? first.SpeakerVoiceTypeFormId
                : next.SpeakerVoiceTypeFormId,
            PromptText = !string.IsNullOrWhiteSpace(first.PromptText) ? first.PromptText : next.PromptText,
            Conditions = first.Conditions.Count > 0 ? first.Conditions : next.Conditions,
            ConditionFunctions = first.ConditionFunctions.Count > 0
                ? first.ConditionFunctions
                : next.ConditionFunctions,
            Responses = mergedResponses.OrderBy(static pair => pair.Key)
                .Select(static pair => pair.Value with { ResponseNumber = pair.Key })
                .ToList(),
            LinkToTopics = first.LinkToTopics.Count > 0 ? first.LinkToTopics : next.LinkToTopics,
            LinkFromTopics = first.LinkFromTopics.Count > 0 ? first.LinkFromTopics : next.LinkFromTopics,
            AddTopics = first.AddTopics.Count > 0 ? first.AddTopics : next.AddTopics,
            FollowUpInfos = first.FollowUpInfos.Count > 0 ? first.FollowUpInfos : next.FollowUpInfos,
            ResultScripts = first.ResultScripts.Count > 0 ? first.ResultScripts : next.ResultScripts,
            HasResultScript = first.HasResultScript || next.HasResultScript
        };
    }

    private static bool TryCreateRehomedCut(
        DialogueRecord source,
        IReadOnlyList<DialogueResponse> responses,
        uint? authoritativeQuestFormId,
        uint? authoritativeSpeakerFormId,
        IReadOnlySet<uint> liveFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        HashSet<uint> occupiedFormIds,
        Dictionary<string, int> editorIdCounts,
        ref uint nextSyntheticSourceId,
        out DialogTopicRecord topic,
        out DialogueRecord info)
    {
        topic = null!;
        info = null!;
        var sourceQuest = authoritativeQuestFormId ?? source.QuestFormId;
        var sourceSpeaker = authoritativeSpeakerFormId ?? DialogueSpeakerBinding.GetExactSpeaker(source);
        var visibleResponses = responses
            .Where(response => DialogueInfoOverlayWriter.IsMeaningfulText(response.Text))
            .ToList();
        // NAM1 is an NPC response, never a player-facing topic label. Rehoming is
        // safe only when the capture contains an explicit player prompt.
        var label = source.PromptText?.Trim();
        if (!TryResolveLiveFormId(sourceQuest, liveFormIds, remapTable, out var quest)
            || !TryResolveLiveFormId(sourceSpeaker, liveFormIds, remapTable, out var speaker)
            || string.IsNullOrWhiteSpace(label)
            || visibleResponses.Count == 0)
        {
            return false;
        }

        var topicSourceId = AllocateSyntheticSourceId(occupiedFormIds, ref nextSyntheticSourceId);
        var infoSourceId = AllocateSyntheticSourceId(occupiedFormIds, ref nextSyntheticSourceId);
        var editorIdBase = $"DmpCut_{source.FormId:X8}";
        var suffix = editorIdCounts.GetValueOrDefault(editorIdBase);
        editorIdCounts[editorIdBase] = suffix + 1;
        var editorId = suffix == 0 ? editorIdBase : $"{editorIdBase}_{suffix + 1}";

        topic = new DialogTopicRecord
        {
            FormId = topicSourceId,
            EditorId = editorId,
            FullName = label,
            QuestFormId = quest,
            TopicType = 0,
            Flags = 0x02,
            Priority = 50f,
            ResponseCount = 1
        };

        var denseResponses = new List<DialogueResponse>(visibleResponses.Count);
        var sourceResponseNumbers = new List<byte>(visibleResponses.Count);
        for (var index = 0; index < visibleResponses.Count; index++)
        {
            var response = visibleResponses[index];
            var sourceNumber = response.ResponseNumber > 0
                ? response.ResponseNumber
                : checked((byte)(index + 1));
            sourceResponseNumbers.Add(sourceNumber);
            denseResponses.Add(response with { ResponseNumber = checked((byte)(index + 1)) });
        }

        info = new DialogueRecord
        {
            FormId = infoSourceId,
            TopicFormId = topicSourceId,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            Conditions = [..source.Conditions],
            ConditionFunctions = [..source.ConditionFunctions],
            Responses = denseResponses,
            PreviousInfo = 0,
            InfoFlags = (byte)(source.InfoFlags & 0x10),
            InfoFlagsExt = (byte)(source.InfoFlagsExt & 0x01),
            IsRehomedCutDialogue = true,
            AudioSourceInfoFormId = source.FormId,
            AudioSourceTopicFormId = source.TopicFormId,
            AudioSourceResponseNumbers = sourceResponseNumbers
        };
        return true;
    }

    internal static (uint? QuestFormId, uint? SpeakerFormId) ResolveMasterScope(
        MasterDialogueInfo masterInfo,
        DialogueRecord prototypeInfo)
    {
        var quest = masterInfo.Record.Subrecords
            .Where(static subrecord => subrecord.Signature == "QSTI" && subrecord.Data.Length >= 4)
            .Select(static subrecord =>
                BinaryPrimitives.ReadUInt32LittleEndian(subrecord.Data.AsSpan(0, 4)))
            .FirstOrDefault(static formId => formId != 0);

        uint? speaker = null;
        if (masterInfo.ExactSpeakerFormIds.Count == 1)
        {
            speaker = masterInfo.ExactSpeakerFormIds.Single();
        }
        else if (masterInfo.ExactSpeakerFormIds.Count > 1
                 && DialogueSpeakerBinding.GetExactSpeaker(prototypeInfo) is { } prototypeSpeaker
                 && masterInfo.ExactSpeakerFormIds.Contains(prototypeSpeaker))
        {
            // A captured exact speaker may disambiguate a retail INFO that has several
            // hard GetIsID branches, but it may never invent scope absent from retail.
            speaker = prototypeSpeaker;
        }

        return (quest == 0 ? null : quest, speaker);
    }

    private static bool TryResolveLiveFormId(
        uint? sourceFormId,
        IReadOnlySet<uint> liveFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        out uint resolvedFormId)
    {
        resolvedFormId = sourceFormId.GetValueOrDefault();
        if (resolvedFormId == 0)
        {
            return false;
        }

        if (remapTable is not null
            && remapTable.TryGetValue(resolvedFormId, out var remapped)
            && liveFormIds.Contains(remapped))
        {
            resolvedFormId = remapped;
            return true;
        }

        return liveFormIds.Contains(resolvedFormId);
    }

    private static uint AllocateSyntheticSourceId(
        HashSet<uint> occupiedFormIds,
        ref uint nextSyntheticSourceId)
    {
        while (nextSyntheticSourceId is 0 or 0xFFFFFFFF || occupiedFormIds.Contains(nextSyntheticSourceId))
        {
            nextSyntheticSourceId--;
        }

        var allocated = nextSyntheticSourceId--;
        occupiedFormIds.Add(allocated);
        return allocated;
    }

    private static void PromoteCoveredSpeakerRoots(
        List<DialogTopicRecord> topics,
        IReadOnlyList<DialogueRecord> infos,
        IReadOnlySet<uint> retailCoveredSpeakers)
    {
        if (retailCoveredSpeakers.Count == 0 || topics.Count == 0)
        {
            return;
        }

        var topicById = topics.ToDictionary(static topic => topic.FormId);
        var candidatesByPair = new Dictionary<(uint Speaker, uint Quest), HashSet<uint>>();
        var incomingByPair = new Dictionary<(uint Speaker, uint Quest), HashSet<uint>>();
        var infosByTopic = new Dictionary<uint, List<DialogueRecord>>();
        foreach (var info in infos)
        {
            var speaker = DialogueSpeakerBinding.GetExactSpeaker(info).GetValueOrDefault();
            var quest = info.QuestFormId.GetValueOrDefault();
            var topicId = info.TopicFormId.GetValueOrDefault();
            if (!retailCoveredSpeakers.Contains(speaker)
                || quest == 0
                || !topicById.TryGetValue(topicId, out var topic)
                || topic.TopicType != 0
                || topic.QuestFormId != quest)
            {
                continue;
            }

            var pair = (speaker, quest);
            if (!candidatesByPair.TryGetValue(pair, out var candidates))
            {
                candidates = [];
                candidatesByPair[pair] = candidates;
            }

            candidates.Add(topicId);
            if (!incomingByPair.TryGetValue(pair, out var incoming))
            {
                incoming = [];
                incomingByPair[pair] = incoming;
            }

            foreach (var linked in info.LinkToTopics)
            {
                if (topicById.TryGetValue(linked, out var linkedTopic) && linkedTopic.QuestFormId == quest)
                {
                    incoming.Add(linked);
                }
            }

            foreach (var linkedFrom in info.LinkFromTopics)
            {
                if (topicById.TryGetValue(linkedFrom, out var linkedTopic) && linkedTopic.QuestFormId == quest)
                {
                    incoming.Add(topicId);
                }
            }

            if (!infosByTopic.TryGetValue(topicId, out var topicInfos))
            {
                topicInfos = [];
                infosByTopic[topicId] = topicInfos;
            }

            topicInfos.Add(info);
        }

        var promoted = new HashSet<uint>();
        foreach (var (pair, candidates) in candidatesByPair)
        {
            incomingByPair.TryGetValue(pair, out var incoming);
            var explicitRoots = candidates.Where(id => topicById[id].IsTopLevel).ToList();
            var roots = explicitRoots.Count > 0
                ? explicitRoots
                : candidates.Where(id => incoming is null || !incoming.Contains(id)).ToList();
            if (roots.Count == 0)
            {
                roots = [..candidates];
            }

            foreach (var rootId in roots)
            {
                var existing = topicById[rootId];
                var label = existing.FullName;
                if (string.IsNullOrWhiteSpace(label) && infosByTopic.TryGetValue(rootId, out var rootInfos))
                {
                    label = rootInfos.Select(static info => info.PromptText)
                        .FirstOrDefault(static text => !string.IsNullOrWhiteSpace(text));
                }

                if (string.IsNullOrWhiteSpace(label))
                {
                    continue;
                }

                topicById[rootId] = existing with { Flags = (byte)(existing.Flags | 0x02), FullName = label };
                promoted.Add(rootId);
            }
        }

        for (var index = 0; index < topics.Count; index++)
        {
            if (promoted.Contains(topics[index].FormId))
            {
                topics[index] = topicById[topics[index].FormId];
            }
        }
    }
}
