using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Cell;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Output;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Reporting;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin;

/// <summary>
///     Builds the top-level DIAL GRUP with INFOs nested as type-7 "Topic Children" GRUPs
///     under each DIAL. Canonical layout:
///     <code>
///         Top GRUP "DIAL" (type=0, label="DIAL"):
///           DIAL record (FormID = X)
///           Topic Children GRUP (type=7, label=X):
///             INFO record (TPIC=X)
///             INFO record (TPIC=X)
///             ...
///           DIAL record (FormID = Y)
///           Topic Children GRUP (type=7, label=Y):
///             ...
///     </code>
///     New DIALs get fresh allocator-issued FormIDs; their child INFOs have TPIC patched
///     to that new ID so the runtime walks them as a coherent topic tree. INFOs pointing
///     at master DIALs are nested under reconstructed override DIAL anchors. INFOs with a dangling
///     or zero TPIC are dropped because the runtime null-derefs on dialog-tree walks.
///     Cross-record FormID references on each INFO (QSTI, ANAM, PNAM, NAME, TCLT, TCLF, TCFU,
///     CTDA.Reference) are validated against the master-FormIDs ∪ emitted-new-FormIDs set;
///     unresolvable references are dropped to prevent runtime null-derefs and the engine's
///     "fallback global broadcast" behavior (which manifested as every NPC playing the
///     crucified idle animation every few seconds).
/// </summary>
internal sealed record DialogSectionResult(
    byte[] DialogSection,
    byte[]? PlaceholderQustRecord,
    IReadOnlyDictionary<uint, uint> NewInfoSourceToAllocated,
    IReadOnlyList<EmittedDialogueAudioBinding> AudioBindings)
{
    public DialogSectionResult(byte[] DialogSection, byte[]? PlaceholderQustRecord)
        : this(DialogSection, PlaceholderQustRecord, new Dictionary<uint, uint>(), [])
    {
    }

    public DialogSectionResult(
        byte[] DialogSection,
        byte[]? PlaceholderQustRecord,
        IReadOnlyDictionary<uint, uint> NewInfoSourceToAllocated)
        : this(DialogSection, PlaceholderQustRecord, NewInfoSourceToAllocated, [])
    {
    }
}

internal static class DialogGrupBuilder
{
    public static DialogSectionResult BuildDialogSection(
        IReadOnlyList<DialogTopicRecord> topics,
        IReadOnlyList<DialogueRecord> infos,
        NewVsOverrideClassifier classifier,
        FormIdAllocator allocator,
        IEnumerable<uint> masterFormIds,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        ConversionPipelineStats stats,
        IConversionProgressSink sink,
        IReadOnlyDictionary<uint, uint>? remapTable = null,
        IEnumerable<uint>? additionalValidFormIds = null,
        IReadOnlyDictionary<uint, string>? voiceTypeEditorIdsByFormId = null,
        IReadOnlyDictionary<uint, uint>? npcVoiceTypeByNpcFormId = null,
        IReadOnlyDictionary<uint, string>? questEditorIdsByFormId = null)
    {
        var newTopics = topics.Where(t => !classifier.IsOverride(t.FormId)).ToList();
        var newInfos = infos.Where(i => !classifier.IsOverride(i.FormId)).ToList();
        if (newTopics.Count == 0 && newInfos.Count == 0)
        {
            return new DialogSectionResult([], null);
        }

        // Pass 1: allocate fresh FormIDs for every new DIAL so we can patch INFO.TPIC
        // before encoding (the encoder writes TPIC verbatim from the model field).
        var dialFormIdMap = new Dictionary<uint, uint>();
        foreach (var topic in newTopics)
        {
            dialFormIdMap[topic.FormId] = allocator.Allocate();
        }

        // Synthesize GREETING INFOs for new (NPC, dialogue-quest) pairs that have topic
        // DIALs but no GREETING entry of their own. Without this, the engine fires master
        // GREETING when the player initiates dialogue, finds nothing for the NPC, and the
        // dialogue menu opens with no topic-tree entry points — Goodbye is the only choice.
        // See GreetingEntrySynthesizer for rationale and the proof-by-example against
        // VDialogueArcadeGannon (55 master-GREETING INFOs link to his topic tree).
        // Must run AFTER dialFormIdMap is built so the synth INFO's TCLT entries point at
        // allocated DIAL FormIDs (which exist in validFormIds) — pointing at source FormIDs
        // would get filtered as dangling and the engine would surface no topic entry points.
        var synthesizedGreetings = GreetingEntrySynthesizer.Synthesize(
            newTopics, newInfos, dialFormIdMap);
        if (synthesizedGreetings.Count > 0)
        {
            newInfos.AddRange(synthesizedGreetings);
        }

        var masterFormIdSet = masterFormIds as HashSet<uint> ?? new HashSet<uint>(masterFormIds);

        // Group new INFOs by their EMITTED parent DIAL FormID. INFOs whose TPIC points at
        // a master DIAL are grouped under that master FormID and emitted after a reconstructed
        // DIAL anchor. INFOs with a dangling TPIC are dropped because the runtime null-derefs
        // on dialog-tree walks.
        var infosByEmittedDial = new Dictionary<uint, List<DialogueRecord>>();
        var infosByMasterDial = new Dictionary<uint, List<DialogueRecord>>();
        var droppedUnavailableMasterDialInfos = 0;
        var droppedOrphanInfos = 0;
        foreach (var info in newInfos)
        {
            if (!info.TopicFormId.HasValue || info.TopicFormId.Value == 0)
            {
                droppedOrphanInfos++;
                continue;
            }

            var topicId = info.TopicFormId.Value;
            if (dialFormIdMap.TryGetValue(topicId, out var newDialId))
            {
                if (!infosByEmittedDial.TryGetValue(newDialId, out var list))
                {
                    list = [];
                    infosByEmittedDial[newDialId] = list;
                }

                list.Add(info);
            }
            else if (masterFormIdSet.Contains(topicId))
            {
                if (masterRecordsByFormId.TryGetValue(topicId, out var masterTopicRecord)
                    && masterTopicRecord.Header.Signature == "DIAL")
                {
                    if (!infosByMasterDial.TryGetValue(topicId, out var list))
                    {
                        list = [];
                        infosByMasterDial[topicId] = list;
                    }

                    list.Add(info);
                }
                else
                {
                    droppedUnavailableMasterDialInfos++;
                }
            }
            else
            {
                droppedOrphanInfos++;
            }
        }

        // INFO-to-INFO references (PNAM and TCFU) need the destination INFO's emitted
        // FormID before any INFO is sanitized. Allocate surviving INFOs up front so sibling
        // links remap source runtime IDs to the actual plugin IDs instead of being dropped.
        var infoFormIdMap = PreallocateInfoFormIds(
            infosByEmittedDial.Values.SelectMany(static list => list)
                .Concat(infosByMasterDial.Values.SelectMany(static list => list)),
            allocator);

        // Build the valid-FormID set used for cross-record field validation. Includes
        // master FormIDs, every DIAL FormID we just allocated (so an INFO's NAME/TCLT/TCLF
        // can legitimately point at a sibling new DIAL), plus any caller-supplied
        // already-emitted new FormIDs (NPCs, QUSTs, REFRs, etc.) so CTDA conditions can
        // reference them without being dropped.
        var validFormIds = new HashSet<uint>(masterFormIdSet);
        foreach (var newDialId in dialFormIdMap.Values)
        {
            validFormIds.Add(newDialId);
        }
        foreach (var newInfoId in infoFormIdMap.Values)
        {
            validFormIds.Add(newInfoId);
        }
        if (additionalValidFormIds is not null)
        {
            foreach (var fid in additionalValidFormIds)
            {
                validFormIds.Add(fid);
            }
        }

        // INFO link subrecords (NAME/TCLT/TCLF/TCFU/PNAM) parsed from the dump reference
        // source FormIDs. The emitted plugin only contains allocator-issued DIAL/INFO
        // FormIDs, so those source references must flow through the same remap helper as
        // QUST/NPC/etc. references or the sanitizer drops the dialogue tree edges.
        var dialogRemapTable = MergeRemapTables(remapTable, dialFormIdMap);
        dialogRemapTable = MergeRemapTables(dialogRemapTable, infoFormIdMap);

        // Build a per-DIAL EDID lookup once. Engine voice-file paths are keyed on the
        // parent DIAL's EditorId, so we need EDID for every DIAL whose children we emit —
        // new DIALs (from the encoded topic record) and master DIAL anchors (from the
        // already-loaded master record dictionary).
        var dialEditorIdByFormId = new Dictionary<uint, string>();
        foreach (var topic in newTopics)
        {
            if (!string.IsNullOrEmpty(topic.EditorId)
                && dialFormIdMap.TryGetValue(topic.FormId, out var newDialId))
            {
                dialEditorIdByFormId[newDialId] = topic.EditorId;
            }
        }
        foreach (var masterDialId in infosByMasterDial.Keys)
        {
            if (!masterRecordsByFormId.TryGetValue(masterDialId, out var masterRec))
            {
                continue;
            }

            var edid = masterRec.EditorId;
            if (!string.IsNullOrEmpty(edid))
            {
                dialEditorIdByFormId[masterDialId] = edid;
            }
        }

        var synthesizedReturnLinks = ApplyRootReturnTopicLinks(
            infosByEmittedDial, infosByMasterDial, dialEditorIdByFormId);

        using var stream = new MemoryStream();
        var topLabel = "DIAL"u8.ToArray();
        var topPos = WriteGrupHeader(stream, topLabel, 0);

        var emittedDials = 0;
        var emittedMasterDialAnchors = 0;
        var emittedInfos = 0;
        var sanitizedFieldCount = 0;
        var droppedConditions = 0;
        var remappedCtdaParameters = 0;
        var droppedNoQstiInfos = 0;
        var infoSourceToAllocated = new Dictionary<uint, uint>();
        var audioBindings = new List<EmittedDialogueAudioBinding>();
        foreach (var topic in newTopics)
        {
            var newDialId = dialFormIdMap[topic.FormId];
            var patchedTopic = SanitizeDialReferences(topic, validFormIds, dialogRemapTable, ref sanitizedFieldCount);
            var dialEncoded = DialEncoder.EncodeNew(patchedTopic);
            if (dialEncoded.Subrecords.Count == 0)
            {
                stats.IncrementSkipped("DIAL");
                continue;
            }

            var dialBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
                "DIAL", newDialId, flags: 0u, dialEncoded.Subrecords);
            stream.Write(dialBytes);
            stats.IncrementEmitted("DIAL");
            stats.NewRecordsEmitted++;
            emittedDials++;

            if (!infosByEmittedDial.TryGetValue(newDialId, out var infosForDial) || infosForDial.Count == 0)
            {
                continue;
            }

            var childLabel = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(childLabel, newDialId);
            var childrenPos = WriteGrupHeader(stream, childLabel, 7);

            foreach (var info in infosForDial)
            {
                var newInfoId = ResolvePreallocatedInfoId(info, infoFormIdMap, allocator);
                var patched = SanitizeInfoReferences(info, newInfoId, newDialId, validFormIds,
                    dialogRemapTable, ref sanitizedFieldCount, ref droppedConditions,
                    ref remappedCtdaParameters);
                if (!patched.QuestFormId.HasValue || patched.QuestFormId.Value == 0)
                {
                    // Engine refuses topic-info inserts when QSTI is missing or zero — it logs
                    // "Unable to insert topic info ... quest (00000000)" and skips. Drop here
                    // so we don't pollute the master file with INFOs the engine won't accept.
                    droppedNoQstiInfos++;
                    stats.IncrementSkipped("INFO");
                    continue;
                }

                var infoEncoded = InfoEncoder.EncodeNew(patched, validFormIds, dialogRemapTable);
                if (infoEncoded.Subrecords.Count == 0)
                {
                    stats.IncrementSkipped("INFO");
                    continue;
                }

                var infoBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
                    "INFO", newInfoId, flags: 0u, infoEncoded.Subrecords);
                stream.Write(infoBytes);
                stats.IncrementEmitted("INFO");
                stats.NewRecordsEmitted++;
                emittedInfos++;
                if (info.FormId != 0 && info.FormId != newInfoId)
                {
                    infoSourceToAllocated[info.FormId] = newInfoId;
                }

                CollectAudioBindings(
                    audioBindings, patched, newInfoId, newDialId,
                    dialEditorIdByFormId, voiceTypeEditorIdsByFormId,
                    npcVoiceTypeByNpcFormId, masterRecordsByFormId,
                    questEditorIdsByFormId);
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, childrenPos);
        }

        var extendedAnchorQstiCount = 0;
        foreach (var (masterDialId, infosForDial) in infosByMasterDial.OrderBy(kv => kv.Key))
        {
            if (infosForDial.Count == 0 ||
                !masterRecordsByFormId.TryGetValue(masterDialId, out var masterDialRecord) ||
                masterDialRecord.Header.Signature != "DIAL")
            {
                droppedUnavailableMasterDialInfos += infosForDial.Count;
                continue;
            }

            // The engine looks at a DIAL's QSTI list to know which quests own INFOs under
            // that topic. When we attach new INFOs to a master TPIC (GREETING, HELLO, etc.)
            // those INFOs reference new quests not in master's QSTI list. Without extending
            // QSTI, the engine never checks the new quest's INFOs against the speaker, so
            // every new NPC's master-topic dialogue silently fails. Reconstruct the master
            // record with QSTI appended; everything else stays verbatim.
            var newQstis = CollectNewAnchorQstis(infosForDial, masterDialRecord, validFormIds, remapTable);
            extendedAnchorQstiCount += newQstis.Count;
            stream.Write(ReconstructDialWithExtraQstis(masterDialRecord, newQstis));
            stats.IncrementEmitted("DIAL");
            stats.OverridesEmitted++;
            emittedMasterDialAnchors++;

            var childLabel = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(childLabel, masterDialId);
            var childrenPos = WriteGrupHeader(stream, childLabel, 7);

            foreach (var info in infosForDial)
            {
                var newInfoId = ResolvePreallocatedInfoId(info, infoFormIdMap, allocator);
                var patched = SanitizeInfoReferences(info, newInfoId, masterDialId, validFormIds,
                    dialogRemapTable, ref sanitizedFieldCount, ref droppedConditions,
                    ref remappedCtdaParameters);
                if (!patched.QuestFormId.HasValue || patched.QuestFormId.Value == 0)
                {
                    droppedNoQstiInfos++;
                    stats.IncrementSkipped("INFO");
                    continue;
                }

                var infoEncoded = InfoEncoder.EncodeNew(patched, validFormIds, dialogRemapTable);
                if (infoEncoded.Subrecords.Count == 0)
                {
                    stats.IncrementSkipped("INFO");
                    continue;
                }

                var infoBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
                    "INFO", newInfoId, flags: 0u, infoEncoded.Subrecords);
                stream.Write(infoBytes);
                stats.IncrementEmitted("INFO");
                stats.NewRecordsEmitted++;
                emittedInfos++;
                if (info.FormId != 0 && info.FormId != newInfoId)
                {
                    infoSourceToAllocated[info.FormId] = newInfoId;
                }

                CollectAudioBindings(
                    audioBindings, patched, newInfoId, masterDialId,
                    dialEditorIdByFormId, voiceTypeEditorIdsByFormId,
                    npcVoiceTypeByNpcFormId, masterRecordsByFormId,
                    questEditorIdsByFormId);
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, childrenPos);
        }

        if (emittedDials == 0 && emittedMasterDialAnchors == 0)
        {
            // No DIALs survived — roll back the empty top-level GRUP header.
            stream.SetLength(topPos);
            sink.Info("Building dialog section",
                $"No DIAL records survived encoding. Dropped {droppedUnavailableMasterDialInfos:N0} INFO(s) " +
                $"with unavailable master-DIAL TPIC and {droppedOrphanInfos:N0} orphan INFO(s).",
                code: "dialog.empty");
            return new DialogSectionResult([], null);
        }

        RecordHeaderProcessor.FinalizeGrupSize(stream, topPos);

        sink.Info("Building dialog section",
            $"Emitted {emittedDials:N0} new DIAL + {emittedMasterDialAnchors:N0} master DIAL anchor(s) " +
            $"+ {emittedInfos:N0} new INFO record(s). " +
            $"Extended {extendedAnchorQstiCount:N0} master-DIAL QSTI binding(s) so the engine knows " +
            "new quests speak master topics. " +
            $"Dropped {droppedUnavailableMasterDialInfos:N0} INFO(s) with unavailable master-DIAL TPIC, " +
            $"{droppedOrphanInfos:N0} orphan INFO(s), and {droppedNoQstiInfos:N0} INFO(s) with no QSTI " +
            "(engine refuses topic-info inserts when QSTI is missing). " +
            $"Synthesized {synthesizedReturnLinks:N0} terminal INFO root-return topic link(s). " +
            $"Sanitized {sanitizedFieldCount:N0} unresolvable " +
            $"cross-record FormID reference(s); dropped {droppedConditions:N0} CTDA condition(s) " +
            $"with dangling Reference or Parameter FormIDs; remapped {remappedCtdaParameters:N0} " +
            "CTDA Parameter1/Parameter2 FormID(s) via the runtime→emitted alias table.",
            code: "dialog.emitted");

        return new DialogSectionResult(
            stream.ToArray(), null, infoSourceToAllocated, audioBindings);
    }

    private static int ApplyRootReturnTopicLinks(
        Dictionary<uint, List<DialogueRecord>> infosByEmittedDial,
        Dictionary<uint, List<DialogueRecord>> infosByMasterDial,
        IReadOnlyDictionary<uint, string> dialEditorIdByFormId)
    {
        var rootLinksBySpeaker = new Dictionary<(uint Quest, uint? Speaker), List<uint>>();
        var rootLinksByQuest = new Dictionary<uint, List<uint>>();

        foreach (var (dialId, infosForDial) in EnumerateInfoGroups(infosByEmittedDial, infosByMasterDial))
        {
            if (!dialEditorIdByFormId.TryGetValue(dialId, out var dialEditorId)
                || !string.Equals(dialEditorId, "GREETING", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var info in infosForDial)
            {
                if (info.QuestFormId is not { } questId || questId == 0 || info.LinkToTopics.Count == 0)
                {
                    continue;
                }

                AddRootLinks(rootLinksByQuest, questId, info.LinkToTopics);
                AddRootLinks(rootLinksBySpeaker, (questId, info.SpeakerFormId), info.LinkToTopics);
            }
        }

        if (rootLinksByQuest.Count == 0)
        {
            return 0;
        }

        var added = 0;
        foreach (var (dialId, infosForDial) in EnumerateInfoGroups(infosByEmittedDial, infosByMasterDial))
        {
            var isGreeting = dialEditorIdByFormId.TryGetValue(dialId, out var dialEditorId)
                             && string.Equals(dialEditorId, "GREETING", StringComparison.OrdinalIgnoreCase);
            var isGoodbyeTopic = dialEditorIdByFormId.TryGetValue(dialId, out dialEditorId)
                                 && string.Equals(dialEditorId, "GOODBYE", StringComparison.OrdinalIgnoreCase);

            for (var i = 0; i < infosForDial.Count; i++)
            {
                var info = infosForDial[i];
                if (isGreeting
                    || isGoodbyeTopic
                    || IsGoodbyeInfo(info)
                    || info.QuestFormId is not { } questId
                    || questId == 0
                    || info.Responses.Count == 0
                    || info.LinkToTopics.Count > 0
                    || info.FollowUpInfos.Count > 0
                    || info.AddTopics.Count > 0)
                {
                    continue;
                }

                if (!rootLinksBySpeaker.TryGetValue((questId, info.SpeakerFormId), out var rootLinks)
                    && !rootLinksByQuest.TryGetValue(questId, out rootLinks))
                {
                    continue;
                }

                var merged = new List<uint>(rootLinks.Count);
                var seen = new HashSet<uint>();
                foreach (var link in rootLinks)
                {
                    if (link != 0 && seen.Add(link))
                    {
                        merged.Add(link);
                    }
                }

                if (merged.Count == 0)
                {
                    continue;
                }

                added += merged.Count;
                infosForDial[i] = info with { LinkToTopics = merged };
            }
        }

        return added;
    }

    private static IEnumerable<(uint DialId, List<DialogueRecord> Infos)> EnumerateInfoGroups(
        Dictionary<uint, List<DialogueRecord>> infosByEmittedDial,
        Dictionary<uint, List<DialogueRecord>> infosByMasterDial)
    {
        foreach (var (dialId, infos) in infosByEmittedDial)
        {
            yield return (dialId, infos);
        }

        foreach (var (dialId, infos) in infosByMasterDial)
        {
            yield return (dialId, infos);
        }
    }

    private static bool IsGoodbyeInfo(DialogueRecord info)
    {
        const byte goodbyeFlag = 0x01;
        return (info.InfoFlags & goodbyeFlag) != 0;
    }

    private static void AddRootLinks<TKey>(
        Dictionary<TKey, List<uint>> target,
        TKey key,
        IReadOnlyList<uint> links)
        where TKey : notnull
    {
        if (!target.TryGetValue(key, out var existing))
        {
            existing = [];
            target[key] = existing;
        }

        foreach (var link in links)
        {
            if (link != 0 && !existing.Contains(link))
            {
                existing.Add(link);
            }
        }
    }

    private static Dictionary<uint, uint> PreallocateInfoFormIds(
        IEnumerable<DialogueRecord> infos,
        FormIdAllocator allocator)
    {
        var map = new Dictionary<uint, uint>();
        foreach (var info in infos)
        {
            if (info.FormId == 0
                || info.QuestFormId is null or 0
                || map.ContainsKey(info.FormId))
            {
                continue;
            }

            map[info.FormId] = allocator.Allocate();
        }

        return map;
    }

    private static uint ResolvePreallocatedInfoId(
        DialogueRecord info,
        IReadOnlyDictionary<uint, uint> infoFormIdMap,
        FormIdAllocator allocator)
    {
        return info.FormId != 0 && infoFormIdMap.TryGetValue(info.FormId, out var allocated)
            ? allocated
            : allocator.Allocate();
    }

    private static IReadOnlyDictionary<uint, uint> MergeRemapTables(
        IReadOnlyDictionary<uint, uint>? outerRemapTable,
        IReadOnlyDictionary<uint, uint> dialFormIdMap)
    {
        if (outerRemapTable is null || outerRemapTable.Count == 0)
        {
            return dialFormIdMap;
        }

        var merged = new Dictionary<uint, uint>(outerRemapTable);
        foreach (var (sourceDialId, allocatedDialId) in dialFormIdMap)
        {
            merged[sourceDialId] = allocatedDialId;
        }

        return merged;
    }

    /// <summary>
    ///     Append <see cref="EmittedDialogueAudioBinding" /> entries (one per response) for
    ///     the just-emitted INFO record. The triple <c>(VoiceTypeEditorId, ParentDialEditorId,
    ///     ResponseNumber)</c> matches what the FNV engine builds at runtime for the voice
    ///     file path; the asset packer uses these to bridge build-era FormID drift in the
    ///     dialogue-audio CSV.
    /// </summary>
    private static void CollectAudioBindings(
        List<EmittedDialogueAudioBinding> bindings,
        DialogueRecord patched,
        uint allocatedInfoId,
        uint parentDialFormId,
        Dictionary<uint, string> dialEditorIdByFormId,
        IReadOnlyDictionary<uint, string>? voiceTypeEditorIdsByFormId,
        IReadOnlyDictionary<uint, uint>? npcVoiceTypeByNpcFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, string>? questEditorIdsByFormId)
    {
        if (!dialEditorIdByFormId.TryGetValue(parentDialFormId, out var dialEdid)
            || string.IsNullOrEmpty(dialEdid))
        {
            return;
        }

        var voiceTypeEdid = ResolveSpeakerVoiceTypeEditorId(
            patched, voiceTypeEditorIdsByFormId, npcVoiceTypeByNpcFormId, masterRecordsByFormId);

        if (patched.Responses.Count == 0)
        {
            return;
        }

        // Resolve quest EDID via the INFO's QSTI. Prefer the unified DMP + master lookup;
        // fall back to scanning master records directly so master-quest INFOs still get a
        // QuestEditorId. Engine voice-filename construction prepends the quest EDID, so we
        // need this even when the quest itself is unchanged from master.
        string? questEdid = null;
        if (patched.QuestFormId is { } qfid && qfid != 0)
        {
            if (questEditorIdsByFormId is not null
                && questEditorIdsByFormId.TryGetValue(qfid, out var ed))
            {
                questEdid = ed;
            }
            else if (masterRecordsByFormId.TryGetValue(qfid, out var masterQuest)
                     && masterQuest.Header.Signature == "QUST"
                     && !string.IsNullOrEmpty(masterQuest.EditorId))
            {
                questEdid = masterQuest.EditorId;
            }
        }

        for (var i = 0; i < patched.Responses.Count; i++)
        {
            var resp = patched.Responses[i];
            var respNum = resp.ResponseNumber > 0 ? resp.ResponseNumber : (byte)(i + 1);
            bindings.Add(new EmittedDialogueAudioBinding
            {
                AllocatedInfoFormId = allocatedInfoId,
                ParentDialEditorId = dialEdid,
                VoiceTypeEditorId = voiceTypeEdid,
                ResponseNumber = respNum,
                QuestEditorId = questEdid,
                ResponseText = resp.Text
            });
        }
    }

    /// <summary>
    ///     Resolve speaker NPC FormID → NPC.VTCK FormID → VoiceType.EDID. Master NPCs come
    ///     from <paramref name="masterRecordsByFormId" />; new NPCs (emitted by NpcEncoder)
    ///     are surfaced through <paramref name="npcVoiceTypeByNpcFormId" /> so the resolver
    ///     can chain to the same VTYP EDID lookup. Returns null if any link breaks.
    /// </summary>
    private static string? ResolveSpeakerVoiceTypeEditorId(
        DialogueRecord patched,
        IReadOnlyDictionary<uint, string>? voiceTypeEditorIdsByFormId,
        IReadOnlyDictionary<uint, uint>? npcVoiceTypeByNpcFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
    {
        if (voiceTypeEditorIdsByFormId is null)
        {
            return null;
        }

        // Direct: INFO already carries a SpeakerVoiceTypeFormId (from runtime probes or
        // GetIsVoiceType conditions). Skip the NPC→VTCK chain entirely.
        if (patched.SpeakerVoiceTypeFormId is { } directVoiceTypeId
            && directVoiceTypeId != 0
            && voiceTypeEditorIdsByFormId.TryGetValue(directVoiceTypeId, out var directEdid)
            && !string.IsNullOrEmpty(directEdid))
        {
            return directEdid;
        }

        if (patched.SpeakerFormId is not { } speakerFid || speakerFid == 0)
        {
            return null;
        }

        // New NPCs: source-FormID-keyed lookup populated by PluginBuilder from the encoded
        // NPC records.
        if (npcVoiceTypeByNpcFormId is not null
            && npcVoiceTypeByNpcFormId.TryGetValue(speakerFid, out var newVtFid)
            && newVtFid != 0
            && voiceTypeEditorIdsByFormId.TryGetValue(newVtFid, out var newEdid)
            && !string.IsNullOrEmpty(newEdid))
        {
            return newEdid;
        }

        // Master NPCs: extract VTCK from the master record's subrecords.
        if (!masterRecordsByFormId.TryGetValue(speakerFid, out var masterNpc)
            || masterNpc.Header.Signature != "NPC_")
        {
            return null;
        }

        var vtck = masterNpc.Subrecords.FirstOrDefault(s => s.Signature == "VTCK");
        if (vtck is null || vtck.Data.Length < 4)
        {
            return null;
        }

        var vtFid = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(vtck.Data);
        if (vtFid == 0
            || !voiceTypeEditorIdsByFormId.TryGetValue(vtFid, out var masterEdid)
            || string.IsNullOrEmpty(masterEdid))
        {
            return null;
        }

        return masterEdid;
    }

    /// <summary>
    ///     Remap QSTI / TNAM on a new DIAL through the source→allocated alias table so the
    ///     emitted topic references the FormIDs the engine will actually load. Source-FormID
    ///     QSTI (e.g. the proto's QUST FormID) silently dangles in the output and the engine
    ///     filters topics whose owning quest can't be resolved, leaving the player with only
    ///     GOODBYE on every NPC tied to a new quest.
    /// </summary>
    /// <summary>
    ///     Determine which new quest QSTI FormIDs need to be appended to a master DIAL
    ///     anchor so the engine's "which quests speak this topic" lookup includes the new
    ///     quests we're attaching INFOs to. Walks every INFO targeting the anchor, resolves
    ///     each <c>QuestFormId</c> through the alias table, and returns the set that isn't
    ///     already present in the master record's existing QSTI list.
    /// </summary>
    private static HashSet<uint> CollectNewAnchorQstis(
        IReadOnlyList<DialogueRecord> infosForDial,
        ParsedMainRecord masterDialRecord,
        HashSet<uint> validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        var existing = new HashSet<uint>();
        foreach (var sub in masterDialRecord.Subrecords)
        {
            if (sub.Signature == "QSTI" && sub.Data.Length >= 4)
            {
                existing.Add(System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(sub.Data));
            }
        }

        var toAdd = new HashSet<uint>();
        foreach (var info in infosForDial)
        {
            if (!info.QuestFormId.HasValue || info.QuestFormId.Value == 0)
            {
                continue;
            }

            var resolved = FormIdReferenceResolver.Resolve(info.QuestFormId.Value, validFormIds, remapTable);
            if (resolved is { } rid && rid != 0 && !existing.Contains(rid))
            {
                toAdd.Add(rid);
            }
        }

        return toAdd;
    }

    /// <summary>
    ///     Reconstruct a master DIAL record's bytes with extra QSTI subrecords appended.
    ///     The DIAL's existing subrecord order is preserved verbatim; new QSTIs go at the
    ///     end of the contiguous QSTI run (canonical fopdoc DIAL order is QSTI* before
    ///     INFC/INFX/SCDA blocks, so end-of-existing-QSTI matches canonical order even when
    ///     master has trailing subrecords).
    /// </summary>
    private static byte[] ReconstructDialWithExtraQstis(
        ParsedMainRecord masterDialRecord,
        HashSet<uint> extraQstis)
    {
        if (extraQstis.Count == 0)
        {
            return CellGrupBuilder.ReconstructRecordBytes(masterDialRecord);
        }

        using var subStream = new MemoryStream();
        using (var subWriter = new BinaryWriter(subStream, System.Text.Encoding.Latin1, true))
        {
            // Find the last existing QSTI position; insert new QSTIs immediately after.
            // If no QSTI exists, append at the end of the subrecord stream.
            var lastQstiIndex = -1;
            for (var i = masterDialRecord.Subrecords.Count - 1; i >= 0; i--)
            {
                if (masterDialRecord.Subrecords[i].Signature == "QSTI")
                {
                    lastQstiIndex = i;
                    break;
                }
            }

            var insertAfter = lastQstiIndex < 0 ? masterDialRecord.Subrecords.Count - 1 : lastQstiIndex;

            for (var i = 0; i < masterDialRecord.Subrecords.Count; i++)
            {
                var sub = masterDialRecord.Subrecords[i];
                SubrecordEncoder.WriteSubrecord(subWriter, sub.Signature, sub.Data);
                if (i == insertAfter)
                {
                    foreach (var qsti in extraQstis)
                    {
                        SubrecordEncoder.WriteFormIdSubrecord(subWriter, "QSTI", qsti);
                    }
                }
            }
        }

        var subBytes = subStream.ToArray();

        using var stream = new MemoryStream();
        var header = masterDialRecord.Header with
        {
            DataSize = (uint)subBytes.Length,
            Flags = masterDialRecord.Header.Flags & ~0x00040000u // clear compressed flag
        };
        RecordHeaderProcessor.WriteRecordHeader(stream, header);
        stream.Write(subBytes);
        return stream.ToArray();
    }

    private static DialogTopicRecord SanitizeDialReferences(
        DialogTopicRecord topic,
        HashSet<uint> validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int sanitizedFieldCount)
    {
        var quest = topic.QuestFormId is { } qid && qid != 0
            ? FormIdReferenceResolver.Resolve(qid, validFormIds, remapTable)
            : topic.QuestFormId;
        if (quest != topic.QuestFormId)
        {
            sanitizedFieldCount++;
        }

        var speaker = topic.SpeakerFormId is { } sid && sid != 0
            ? FormIdReferenceResolver.Resolve(sid, validFormIds, remapTable)
            : topic.SpeakerFormId;
        if (speaker != topic.SpeakerFormId)
        {
            sanitizedFieldCount++;
        }

        return topic with { QuestFormId = quest, SpeakerFormId = speaker };
    }

    /// <summary>
    ///     Drop FormID fields on a new INFO whose targets don't exist in either master or our
    ///     newly-emitted set. Patches FormId + TopicFormId to the allocator-issued values.
    ///     Delegates CTDA condition filtering (Reference dangle + Parameter1/Parameter2
    ///     dangle for function-aware FormID params) to <see cref="ConditionSanitizer.Filter" />.
    /// </summary>
    private static DialogueRecord SanitizeInfoReferences(
        DialogueRecord info,
        uint newInfoId,
        uint newDialId,
        HashSet<uint> validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        ref int sanitizedFieldCount,
        ref int droppedConditions,
        ref int remappedCtdaParameters)
    {
        // FormIdReferenceResolver tries the runtime→emitted alias table FIRST, then falls
        // back to the validity check. Returns null when the FormID dangles AND no remap
        // exists — callers null out the optional field so it isn't emitted.
        static uint? Resolve(uint? id, HashSet<uint> valid, IReadOnlyDictionary<uint, uint>? remap, ref int counter)
        {
            if (!id.HasValue || id.Value == 0)
            {
                return id;
            }

            var resolved = FormIdReferenceResolver.Resolve(id.Value, valid, remap);
            if (resolved is null)
            {
                counter++;
                return null;
            }

            return resolved;
        }

        var quest = Resolve(info.QuestFormId, validFormIds, remapTable, ref sanitizedFieldCount);
        var speaker = Resolve(info.SpeakerFormId, validFormIds, remapTable, ref sanitizedFieldCount);
        var prevInfo = Resolve(info.PreviousInfo, validFormIds, remapTable, ref sanitizedFieldCount);

        var addTopics = FilterValid(info.AddTopics, validFormIds, remapTable, ref sanitizedFieldCount);
        var linkTo = FilterValid(info.LinkToTopics, validFormIds, remapTable, ref sanitizedFieldCount);
        var linkFrom = FilterValid(info.LinkFromTopics, validFormIds, remapTable, ref sanitizedFieldCount);
        var followUps = FilterValid(info.FollowUpInfos, validFormIds, remapTable, ref sanitizedFieldCount);

        var conditions = ConditionSanitizer.Filter(
            info.Conditions, validFormIds, remapTable,
            ref remappedCtdaParameters, ref droppedConditions);

        return info with
        {
            FormId = newInfoId,
            TopicFormId = newDialId,
            QuestFormId = quest,
            SpeakerFormId = speaker,
            PreviousInfo = prevInfo,
            AddTopics = addTopics,
            LinkToTopics = linkTo,
            LinkFromTopics = linkFrom,
            FollowUpInfos = followUps,
            Conditions = conditions
        };
    }

    private static List<uint> FilterValid(
        List<uint> ids,
        HashSet<uint> valid,
        IReadOnlyDictionary<uint, uint>? remap,
        ref int counter)
    {
        var kept = new List<uint>(ids.Count);
        foreach (var id in ids)
        {
            if (id == 0)
            {
                kept.Add(id);
                continue;
            }

            var resolved = FormIdReferenceResolver.Resolve(id, valid, remap);
            if (resolved.HasValue)
            {
                kept.Add(resolved.Value);
            }
            else
            {
                counter++;
            }
        }

        return kept;
    }

    private static long WriteGrupHeader(Stream stream, byte[] label, int groupType)
    {
        var header = new GroupHeader
        {
            GroupSize = 0,
            Label = label,
            GroupType = groupType,
            Stamp = 0,
            Unknown = 0
        };
        return RecordHeaderProcessor.WriteGrupHeader(stream, header);
    }
}
