using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Synthesize a GREETING INFO record for each new (NPC, dialogue-quest) pair that has
///     custom topic DIALs but no INFO under master GREETING. Without this, the FNV engine
///     fires master GREETING when the player initiates dialogue but finds nothing to say
///     for the NPC, so it shows the dialogue menu with no topics — the only choice the
///     player sees is Goodbye, even when the NPC has dozens of conversation topics emitted.
///     <para>
///     This mirrors how vanilla NPCs (e.g. Arcade Gannon — 55 INFOs under master GREETING,
///     each TCLT-linked to topic-tree entry points) reach their topic trees. Proto-build
///     captures may not include any GREETING INFO for a given new NPC; the synth covers
///     that gap.
///     </para>
/// </summary>
internal static class GreetingEntrySynthesizer
{
    /// <summary>Master GREETING DIAL FormID.</summary>
    private const uint MasterGreetingDial = 0x000000C8;

    /// <summary>CTDA function index for GetIsID (verifies the actor evaluating equals a specific base).</summary>
    private const ushort GetIsIdFunctionIndex = 72;

    /// <summary>
    ///     Synthesize GREETING INFOs for every (speaker, quest) pair that has at least one
    ///     new topic DIAL but no existing INFO under master GREETING. Each synth INFO carries
///     a single <c>GetIsID(speaker) == 1.0</c> condition so only this NPC speaks it, and
    ///     TCLT entries linking to the inferred root DIALs for that speaker's quest tree so
    ///     the engine can surface those as topic-tree entry points after the greeting plays.
    /// </summary>
    /// <param name="newTopics">New DIAL records being emitted in this build.</param>
    /// <param name="newInfos">New INFO records being emitted in this build.</param>
    /// <param name="dialFormIdMap">
    ///     Source DIAL FormID → allocator-issued (emitted) FormID map. The TCLT entries on
    ///     the synth INFO must reference emitted DIAL FormIDs; source FormIDs aren't in the
    ///     valid-FormID set and would be filtered as dangling by
    ///     <see cref="DialogGrupBuilder" />'s sanitize pass.
    /// </param>
    /// <returns>Synthesized INFO records to append to <paramref name="newInfos" />.</returns>
    public static List<DialogueRecord> Synthesize(
        IReadOnlyList<DialogTopicRecord> newTopics,
        IReadOnlyList<DialogueRecord> newInfos,
        IReadOnlyDictionary<uint, uint> dialFormIdMap)
    {
        // Map source DIAL FormID → topic metadata and quest. The synth works in source
        // FormID space while it infers roots from LinkTo/LinkFrom, then maps the surviving
        // roots to emitted FormIDs at the end.
        var topicBySource = new Dictionary<uint, DialogTopicRecord>();
        var topicQuestBySource = new Dictionary<uint, uint>();
        foreach (var topic in newTopics)
        {
            if (!topic.QuestFormId.HasValue || topic.QuestFormId.Value == 0)
            {
                continue;
            }

            if (!dialFormIdMap.ContainsKey(topic.FormId))
            {
                continue;
            }

            topicBySource[topic.FormId] = topic;
            topicQuestBySource[topic.FormId] = topic.QuestFormId.Value;
        }

        // Discover (speaker, quest) pairs already covered by an existing GREETING INFO so
        // we don't duplicate. A speaker may have its own GREETING under a different quest,
        // so we treat the pair as the unique identity.
        var existingGreetingPairs = new HashSet<(uint Speaker, uint Quest)>();
        foreach (var info in newInfos)
        {
            if (info.TopicFormId != MasterGreetingDial) continue;
            if (!info.SpeakerFormId.HasValue || info.SpeakerFormId.Value == 0) continue;
            if (!info.QuestFormId.HasValue || info.QuestFormId.Value == 0) continue;
            existingGreetingPairs.Add((info.SpeakerFormId.Value, info.QuestFormId.Value));
        }

        // Discover every distinct (speaker, quest) pair the conversion produced — those are
        // the NPCs whose dialogue trees need an entry point. At the same time, collect the
        // topics that actually have INFOs for that speaker and mark topics that are reached
        // by a TCLT/TCLF edge. Synthetic GREETING must not link every topic in the quest:
        // doing so flattens internal branches into the first dialogue menu.
        var npcQuestPairs = new HashSet<(uint Speaker, uint Quest)>();
        var candidateTopicsByPair = new Dictionary<(uint Speaker, uint Quest), HashSet<uint>>();
        var incomingTopicsByPair = new Dictionary<(uint Speaker, uint Quest), HashSet<uint>>();
        foreach (var info in newInfos)
        {
            if (!info.SpeakerFormId.HasValue || info.SpeakerFormId.Value == 0) continue;
            if (!info.QuestFormId.HasValue || info.QuestFormId.Value == 0) continue;

            var pair = (Speaker: info.SpeakerFormId.Value, Quest: info.QuestFormId.Value);
            npcQuestPairs.Add(pair);

            if (info.TopicFormId is not { } sourceTopicId ||
                !topicQuestBySource.TryGetValue(sourceTopicId, out var topicQuestId) ||
                topicQuestId != pair.Quest)
            {
                continue;
            }

            if (!candidateTopicsByPair.TryGetValue(pair, out var candidates))
            {
                candidates = [];
                candidateTopicsByPair[pair] = candidates;
            }

            candidates.Add(sourceTopicId);

            if (!incomingTopicsByPair.TryGetValue(pair, out var incoming))
            {
                incoming = [];
                incomingTopicsByPair[pair] = incoming;
            }

            foreach (var linkedTopicId in info.LinkToTopics)
            {
                if (topicQuestBySource.TryGetValue(linkedTopicId, out var linkedQuestId) &&
                    linkedQuestId == pair.Quest)
                {
                    incoming.Add(linkedTopicId);
                }
            }

            // TCLF is the reverse edge: this INFO's parent topic is reachable from the
            // listed source topic(s).
            foreach (var sourceTopic in info.LinkFromTopics)
            {
                if (topicQuestBySource.TryGetValue(sourceTopic, out var sourceQuestId) &&
                    sourceQuestId == pair.Quest)
                {
                    incoming.Add(sourceTopicId);
                }
            }
        }

        var synthesized = new List<DialogueRecord>();
        foreach (var (speakerId, questId) in npcQuestPairs)
        {
            if (existingGreetingPairs.Contains((speakerId, questId)))
            {
                continue;
            }

            var linkedTopics = SelectEntryTopics(
                speakerId, questId, candidateTopicsByPair, incomingTopicsByPair,
                topicBySource, dialFormIdMap);
            if (linkedTopics.Count == 0)
            {
                continue;
            }

            synthesized.Add(BuildGreetingInfo(speakerId, questId, linkedTopics));
        }

        return synthesized;
    }

    private static List<uint> SelectEntryTopics(
        uint speakerId,
        uint questId,
        Dictionary<(uint Speaker, uint Quest), HashSet<uint>> candidateTopicsByPair,
        Dictionary<(uint Speaker, uint Quest), HashSet<uint>> incomingTopicsByPair,
        Dictionary<uint, DialogTopicRecord> topicBySource,
        IReadOnlyDictionary<uint, uint> dialFormIdMap)
    {
        var pair = (speakerId, questId);
        if (!candidateTopicsByPair.TryGetValue(pair, out var candidates) || candidates.Count == 0)
        {
            return [];
        }

        // Prefer explicit top-level topics when the runtime captured that bit. Some proto
        // captures lack it, so fall back to graph roots inferred from TCLT/TCLF.
        var roots = candidates
            .Where(sourceId => topicBySource.TryGetValue(sourceId, out var topic) && topic.IsTopLevel)
            .ToList();

        if (roots.Count == 0)
        {
            incomingTopicsByPair.TryGetValue(pair, out var incoming);
            roots = candidates
                .Where(sourceId => incoming is null || !incoming.Contains(sourceId))
                .ToList();
        }

        if (roots.Count == 0)
        {
            roots = [..candidates];
        }

        return roots
            .OrderBy(sourceId => topicBySource.TryGetValue(sourceId, out var topic)
                ? topic.Priority
                : 0f)
            .ThenBy(sourceId => topicBySource.TryGetValue(sourceId, out var topic)
                ? topic.EditorId
                : null,
                StringComparer.OrdinalIgnoreCase)
            .Select(sourceId => dialFormIdMap.TryGetValue(sourceId, out var emittedId) ? emittedId : 0)
            .Where(emittedId => emittedId != 0)
            .Distinct()
            .ToList();
    }

    private static DialogueRecord BuildGreetingInfo(
        uint speakerFormId,
        uint questFormId,
        IReadOnlyList<uint> linkedTopics)
    {
        return new DialogueRecord
        {
            // FormId = 0 — DialogGrupBuilder.allocator assigns a fresh ID during emission.
            FormId = 0,
            EditorId = null,
            TopicFormId = MasterGreetingDial,
            QuestFormId = questFormId,
            SpeakerFormId = speakerFormId,
            // Single empty-text response. The FNV engine plays the voice file if one exists
            // (named by topic EDID + INFO FormID), and falls back to silent if not. Empty
            // text is fine — the visible result is "[silence]" before the topic list opens.
            Responses =
            [
                new DialogueResponse
                {
                    Text = string.Empty,
                    ResponseNumber = 1,
                    EmotionType = 0,
                    EmotionValue = 0
                }
            ],
            // CTDA: GetIsID(speaker) == 1.0. Type=0 → "==" + run-on Subject (default).
            // The engine evaluates against the NPC the player is talking to, so the INFO
            // only fires for this specific NPC.
            Conditions =
            [
                new DialogueCondition
                {
                    Type = 0,
                    ComparisonValue = 1.0f,
                    FunctionIndex = GetIsIdFunctionIndex,
                    Parameter1 = speakerFormId,
                    Parameter2 = 0,
                    RunOn = 0,
                    Reference = 0
                }
            ],
            // TCLT — topic-tree entry points the player can ask about after the greeting.
            // Without these the engine surfaces no topics and the player sees only Goodbye.
            LinkToTopics = [..linkedTopics],
            LinkFromTopics = [],
            AddTopics = [],
            // InfoFlags = 0: no Goodbye flag set, conversation continues after this INFO.
            InfoFlags = 0,
            InfoFlagsExt = 0,
            Difficulty = 0
        };
    }
}
