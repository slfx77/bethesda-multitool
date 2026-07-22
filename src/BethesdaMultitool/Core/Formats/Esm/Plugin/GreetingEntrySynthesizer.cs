using BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

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
    /// <param name="greetingSurvivesGates">
    ///     Predicate matching the master-topic admission gates: a pair counts as "already has
    ///     a greeting" only when at least one of its greeting INFOs will actually EMIT. A
    ///     content-less runtime stub suppressing the synth and then dying at the gate leaves
    ///     the NPC with only Goodbye. Null = count every greeting (legacy behavior).
    /// </param>
    /// <returns>Synthesized INFO records to append to <paramref name="newInfos" />.</returns>
    public static List<DialogueRecord> Synthesize(
        IReadOnlyList<DialogTopicRecord> newTopics,
        IReadOnlyList<DialogueRecord> newInfos,
        IReadOnlyDictionary<uint, uint> dialFormIdMap,
        Func<DialogueRecord, bool>? greetingSurvivesGates = null,
        IReadOnlySet<uint>? speakersWithRetailGreeting = null)
    {
        // Infer entry points only from DIALs that will actually be emitted. The shared
        // selector also rejects Conversation/combat/service barks and scopes every edge to
        // one exact (speaker, quest) pair.
        var scopedRoots = SelectScopedEntryTopics(
            newTopics.Where(topic => dialFormIdMap.ContainsKey(topic.FormId)).ToList(),
            newInfos);

        // Discover (speaker, quest) pairs already covered by an existing GREETING INFO so
        // we don't duplicate. A speaker may have its own GREETING under a different quest,
        // so we treat the pair as the unique identity. Only greetings that will survive the
        // master-topic gates count — a doomed stub must not suppress the synth entry.
        var existingGreetingPairs = new HashSet<(uint Speaker, uint Quest)>();
        foreach (var info in newInfos)
        {
            if (info.TopicFormId != MasterGreetingDial) continue;
            if (!info.QuestFormId.HasValue || info.QuestFormId.Value == 0) continue;
            if (greetingSurvivesGates is not null && !greetingSurvivesGates(info)) continue;
            if (DialogueSpeakerBinding.GetExactSpeaker(info) is { } speaker)
            {
                existingGreetingPairs.Add((speaker, info.QuestFormId.Value));
            }
        }

        var synthesized = new List<DialogueRecord>();
        foreach (var (pair, sourceRootTopics) in scopedRoots
                     .OrderBy(static entry => entry.Key.Speaker)
                     .ThenBy(static entry => entry.Key.Quest))
        {
            var (speakerId, questId) = pair;
            // An exact retail GREETING for this speaker is the authoritative entry point.
            // Adding a silent synthetic INFO ahead of it shadows retail first-meet result
            // scripts (Sunny's tutorial walk never starts). Covered prototype topic trees
            // are promoted to normal top-level choices by DialogueCombinePlanner instead.
            if (speakersWithRetailGreeting?.Contains(speakerId) == true)
            {
                continue;
            }

            if (existingGreetingPairs.Contains((speakerId, questId)))
            {
                continue;
            }

            var linkedTopics = sourceRootTopics
                .Select(sourceId => dialFormIdMap.TryGetValue(sourceId, out var emittedId) ? emittedId : 0)
                .Where(static emittedId => emittedId != 0)
                .Distinct()
                .ToList();
            if (linkedTopics.Count == 0)
            {
                continue;
            }

            synthesized.Add(BuildGreetingInfo(speakerId, questId, linkedTopics));
        }

        return synthesized;
    }

    /// <summary>
    ///     Infer source-space player-topic roots for exact (speaker, quest) pairs. Only
    ///     Topic-type DIALs participate: Conversation and other system/bark topics must
    ///     never become player choices merely because runtime capture attached them to a
    ///     broad GREETING TCLT list.
    /// </summary>
    internal static IReadOnlyDictionary<(uint Speaker, uint Quest), IReadOnlyList<uint>>
        SelectScopedEntryTopics(
            IReadOnlyList<DialogTopicRecord> topics,
            IReadOnlyList<DialogueRecord> infos,
            bool includeAllGraphRoots = false)
    {
        var topicBySource = topics
            .Where(static topic => topic.FormId != 0
                                   && topic.TopicType == 0
                                   && topic.QuestFormId is > 0)
            .GroupBy(static topic => topic.FormId)
            .ToDictionary(static group => group.Key, static group => group.First());
        var candidatesByPair = new Dictionary<(uint Speaker, uint Quest), HashSet<uint>>();
        var scopedInfos = new List<(DialogueRecord Info, uint Speaker, uint Quest, uint Topic)>();
        foreach (var info in infos)
        {
            var speaker = DialogueSpeakerBinding.GetExactSpeaker(info).GetValueOrDefault();
            var quest = info.QuestFormId.GetValueOrDefault();
            var topicId = info.TopicFormId.GetValueOrDefault();
            if (speaker == 0
                || quest == 0
                || !topicBySource.TryGetValue(topicId, out var topic)
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
            scopedInfos.Add((info, speaker, quest, topicId));
        }

        var incomingByPair = candidatesByPair.Keys
            .ToDictionary(static pair => pair, static _ => new HashSet<uint>());
        foreach (var (info, speaker, quest, topicId) in scopedInfos)
        {
            var pair = (speaker, quest);
            var candidates = candidatesByPair[pair];
            var incoming = incomingByPair[pair];
            foreach (var linkedTopicId in info.LinkToTopics)
            {
                if (candidates.Contains(linkedTopicId))
                {
                    incoming.Add(linkedTopicId);
                }
            }

            // TCLF is the reverse edge: this INFO's parent topic is reachable from the
            // listed source topic. Require that source to be a candidate for this exact
            // pair; a same-quest edge owned by another speaker is not part of this graph.
            if (info.LinkFromTopics.Any(candidates.Contains))
            {
                incoming.Add(topicId);
            }
        }

        var result = new Dictionary<(uint Speaker, uint Quest), IReadOnlyList<uint>>();
        foreach (var (pair, candidates) in candidatesByPair)
        {
            var incoming = incomingByPair[pair];
            var explicitRoots = candidates
                .Where(sourceId => topicBySource[sourceId].IsTopLevel)
                .ToList();
            var inferredRoots = candidates.Where(sourceId => !incoming.Contains(sourceId)).ToList();
            List<uint> roots;
            if (explicitRoots.Count == 0)
            {
                roots = inferredRoots;
            }
            else if (includeAllGraphRoots)
            {
                roots = explicitRoots.Concat(inferredRoots).Distinct().ToList();
            }
            else
            {
                roots = explicitRoots;
            }

            // A completely cyclic capture has no zero-incoming vertex. Every member of a
            // root cycle is still scoped to this exact actor and quest; retain that legacy
            // fallback rather than strand prototype-only NPCs with a Goodbye-only menu.
            if (roots.Count == 0)
            {
                roots = [..candidates];
            }

            result[pair] = roots
                .OrderBy(sourceId => topicBySource[sourceId].Priority)
                .ThenBy(sourceId => topicBySource[sourceId].EditorId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(static sourceId => sourceId)
                .ToList();
        }

        return result;
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
            Difficulty = 0,
            // Exempts this entry from the master-topic empty-stub gate: the silent greeting
            // IS the designed topic-tree entry mechanism (see gate in DialogGrupBuilder).
            IsSynthesizedGreetingEntry = true
        };
    }
}
