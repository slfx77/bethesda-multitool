using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers;
using BethesdaMultitool.Core.Formats.Esm.Merge;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Cell;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Output;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Reference;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;
using BethesdaMultitool.Core.Formats.Esm.Reporting;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin;

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
///     semantic CTDA.Reference slots) are validated against the master-FormIDs ∪ emitted-new-FormIDs set;
///     unresolvable references are dropped to prevent runtime null-derefs and the engine's
///     "fallback global broadcast" behavior (which manifested as every NPC playing the
///     crucified idle animation every few seconds).
/// </summary>
internal sealed record DialogSectionResult(
    byte[] DialogSection,
    byte[]? PlaceholderQustRecord,
    IReadOnlyDictionary<uint, uint> NewInfoSourceToAllocated,
    IReadOnlyList<EmittedDialogueAudioBinding> AudioBindings,
    DialogueProducerLedger ProducerLedger)
{
    public DialogSectionResult(byte[] DialogSection, byte[]? PlaceholderQustRecord)
        : this(
            DialogSection,
            PlaceholderQustRecord,
            new Dictionary<uint, uint>(),
            [],
            DialogueProducerLedger.Empty)
    {
    }

    public DialogSectionResult(
        byte[] DialogSection,
        byte[]? PlaceholderQustRecord,
        IReadOnlyDictionary<uint, uint> NewInfoSourceToAllocated)
        : this(
            DialogSection,
            PlaceholderQustRecord,
            NewInfoSourceToAllocated,
            [],
            DialogueProducerLedger.Empty)
    {
    }
}

/// <summary>Builds the plugin's top-level DIAL GRUP (dialogue topics and their nested INFO responses).</summary>
internal static class DialogGrupBuilder
{
    /// <summary>
    ///     Built-in dialogue types (GREETING, HELLO, GOODBYE, combat/service barks, etc.)
    ///     occupy the reserved low FormID block. Ordinary quest topics begin at 0x1000 and
    ///     are reachable only through their authored dialogue tree, so the global system-
    ///     topic speaker gate must not suppress their prototype-only INFO children.
    /// </summary>
    private const uint FirstNonSystemDialFormId = 0x00001000;

    /// <summary>
    ///     Builds the top-level DIAL GRUP section: emits new topics with fresh FormIDs, nests
    ///     INFOs under their topic, and validates/drops dangling cross-record references.
    /// </summary>
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
        IReadOnlyDictionary<uint, string>? questEditorIdsByFormId = null,
        MasterDialogueIndex? masterDialogueIndex = null,
        IReadOnlySet<uint>? diagnosticKeepMasterFormIds = null,
        IReadOnlyDictionary<uint, ImmutableHashSet<string>>? diagnosticRetainMasterSubrecords = null,
        IReadOnlyDictionary<uint, uint>? preallocatedNewFormIds = null,
        IReadOnlyList<QuestVariableRecoveryMapping>? questVariableProducerMappings = null,
        IReadOnlySet<uint>? liveScriptVariableOwnerFormIds = null)
    {
        masterDialogueIndex ??= MasterDialogueIndex.BuildFromRecordOrder(masterRecordsByFormId.Values);
        var combinePlan = DialogueCombinePlanner.Build(
            topics, infos, classifier, masterDialogueIndex, masterFormIds,
            additionalValidFormIds, remapTable);
        ReportIdentityTelemetry(combinePlan.TopicIdentities, sink);
        var newTopics = combinePlan.NewTopics.ToList();
        var newInfos = combinePlan.NewInfos.ToList();
        var sharedInfoOverlays = combinePlan.SharedInfoOverlays
            .Where(overlay => diagnosticKeepMasterFormIds?.Contains(overlay.PrototypeInfo.FormId) != true)
            .Where(overlay => diagnosticKeepMasterFormIds?.Contains(overlay.MasterInfo.ParentDialFormId) != true)
            .ToList();
        if (diagnosticKeepMasterFormIds is { Count: > 0 })
        {
            var candidateOrphanTopics = newInfos
                .Where(info => diagnosticKeepMasterFormIds.Contains(info.FormId)
                               || (info.TopicFormId is { } topicId
                                   && diagnosticKeepMasterFormIds.Contains(topicId))
                               || (info.AudioSourceInfoFormId is { } sourceId
                                   && diagnosticKeepMasterFormIds.Contains(sourceId))
                               || (info.AudioSourceTopicFormId is { } sourceTopicId
                                   && diagnosticKeepMasterFormIds.Contains(sourceTopicId)))
                .Select(static info => info.TopicFormId.GetValueOrDefault())
                .Where(static topicId => topicId != 0)
                .ToHashSet();
            newInfos.RemoveAll(info =>
                diagnosticKeepMasterFormIds.Contains(info.FormId)
                || (info.TopicFormId is { } topicId && diagnosticKeepMasterFormIds.Contains(topicId))
                || (info.AudioSourceInfoFormId is { } sourceId
                    && diagnosticKeepMasterFormIds.Contains(sourceId))
                || (info.AudioSourceTopicFormId is { } sourceTopicId
                    && diagnosticKeepMasterFormIds.Contains(sourceTopicId)));
            newTopics.RemoveAll(topic =>
                candidateOrphanTopics.Contains(topic.FormId)
                && newInfos.All(info => info.TopicFormId != topic.FormId));
        }

        if (newTopics.Count == 0 && newInfos.Count == 0 && sharedInfoOverlays.Count == 0)
        {
            return new DialogSectionResult([], null);
        }

        // Proto topics whose FormIDs collide with retail records are excluded from emission
        // above (the retail-FormID-stable generation of a quest's dialogue tree). Remember
        // them per quest: they are the retail branches — INCLUDING the conversation exits
        // ("Never mind." / goodbye topics) — that synthesized choice links must reconnect
        // to. Without an exit link the root-return stamp closes an inescapable choice ring
        // (Beatrix Russell softlock; byte-verified: the DUMP has the exit topic, the
        // collision gate dropped it, and the ring was stamped on by us). Selection is
        // per-speaker: shared quests (VFreeformFreeside) collide topics of MANY NPCs, and
        // an exit whose retail INFO can't pass for this speaker is a hidden choice.
        var exitTopicResolver = ExitTopicResolver.Build(topics, classifier, masterRecordsByFormId);

        // Pass 1: allocate fresh FormIDs for every new DIAL so we can patch INFO.TPIC
        // before encoding (the encoder writes TPIC verbatim from the model field).
        var dialFormIdMap = new Dictionary<uint, uint>();
        foreach (var topic in newTopics)
        {
            dialFormIdMap[topic.FormId] = ResolvePreallocatedFormId(
                topic.FormId, preallocatedNewFormIds, allocator);
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
        // The survival predicate closes an existence-check hole: a (speaker, quest) pair
        // whose only greeting is a content-less runtime stub must NOT count as covered —
        // the stub dies at the master-topic gates below, and skipping the synth then leaves
        // the NPC with only Goodbye (Ulysses).
        var synthesizedGreetings = GreetingEntrySynthesizer.Synthesize(
            newTopics, newInfos, dialFormIdMap,
            greetingSurvivesGates: info =>
                HasSpeakerBindingCondition(info) && HasRenderableResponse(info),
            speakersWithRetailGreeting: combinePlan.RetailCoveredSpeakers);
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
        var recoveredRawParentInfos = 0;

        bool ResolvesToEmittedDial(uint id) => dialFormIdMap.ContainsKey(id);
        bool ResolvesToMasterDial(uint id) =>
            masterFormIdSet.Contains(id)
            && masterRecordsByFormId.TryGetValue(id, out var record)
            && record.Header.Signature == "DIAL";

        foreach (var info in newInfos)
        {
            var routed = info;
            var topicId = info.TopicFormId ?? 0u;

            // Raw type-7 ancestry recovery (USER POLICY 2026-08-03: recover, don't orphan).
            // The runtime TESTopic attribution and the raw file ancestry are retained as
            // separate evidence on DialogueRecord; until now only the runtime attribution was
            // consulted here, so an INFO whose runtime link was missing or dangling dropped
            // even when its raw Topic-Children GRUP parent was sitting right there (xex44:
            // orphans grew 334→878 on this exact lane). The model forbids guessing between
            // ambiguous raw parents — but a candidate list where exactly ONE entry resolves
            // to an emittable DIAL is not a guess.
            if (topicId == 0 || !(ResolvesToEmittedDial(topicId) || ResolvesToMasterDial(topicId)))
            {
                var resolvable = routed.RawParentTopicFormIds
                    .Where(raw => raw != 0 && raw != topicId
                                  && (ResolvesToEmittedDial(raw) || ResolvesToMasterDial(raw)))
                    .Distinct()
                    .ToList();

                if (resolvable.Count == 1)
                {
                    routed = routed with { TopicFormId = resolvable[0] };
                    topicId = resolvable[0];
                    recoveredRawParentInfos++;
                }
            }

            if (topicId == 0)
            {
                droppedOrphanInfos++;
                continue;
            }

            if (dialFormIdMap.TryGetValue(topicId, out var newDialId))
            {
                if (!infosByEmittedDial.TryGetValue(newDialId, out var list))
                {
                    list = [];
                    infosByEmittedDial[newDialId] = list;
                }

                list.Add(routed);
            }
            else if (ResolvesToMasterDial(topicId))
            {
                if (!infosByMasterDial.TryGetValue(topicId, out var list))
                {
                    list = [];
                    infosByMasterDial[topicId] = list;
                }

                list.Add(routed);
            }
            else if (masterFormIdSet.Contains(topicId))
            {
                droppedUnavailableMasterDialInfos++;
            }
            else
            {
                droppedOrphanInfos++;
            }
        }

        var overlaysByMasterDial = sharedInfoOverlays
            .GroupBy(static overlay => overlay.MasterInfo.ParentDialFormId)
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(overlay => overlay.MasterInfo.FileOrder).ToList());

        // INFO-to-INFO references (PNAM and TCFU) need the destination INFO's emitted
        // FormID before any INFO is sanitized. Allocate surviving INFOs up front so sibling
        // links remap source runtime IDs to the actual plugin IDs instead of being dropped.
        var infoFormIdMap = PreallocateInfoFormIds(
            infosByEmittedDial.Values.SelectMany(static list => list)
                .Concat(infosByMasterDial.Values.SelectMany(static list => list)),
            allocator,
            preallocatedNewFormIds);

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

        // A GetScriptVariable chain may be semantically valid in the captured dump yet
        // still point at a placed owner whose parent CELL is suppressed later. The caller
        // supplies only master + actually-written REFR/ACHR/ACRE identities after cell
        // emission. Remove the complete INFO here; dropping just CTDA would widen it.
        if (liveScriptVariableOwnerFormIds is not null)
        {
            var unavailableOwnerInfos = new Dictionary<DialogueRecord, (uint Source, uint Resolved)>(
                ReferenceEqualityComparer.Instance);
            foreach (var info in infosByEmittedDial.Values.SelectMany(static list => list)
                         .Concat(infosByMasterDial.Values.SelectMany(static list => list)))
            {
                foreach (var condition in info.Conditions)
                {
                    if (condition.FunctionIndex !=
                        QuestVariableConditionSanitizer.GetScriptVariableFunctionIndex)
                    {
                        continue;
                    }

                    var sourceOwner = condition.Parameter1;
                    var resolvedOwner = ResolveTerminalAlias(sourceOwner, remapTable);
                    if (!liveScriptVariableOwnerFormIds.Contains(resolvedOwner))
                    {
                        unavailableOwnerInfos.TryAdd(info, (sourceOwner, resolvedOwner));
                        break;
                    }
                }
            }

            if (unavailableOwnerInfos.Count > 0)
            {
                foreach (var infosForDial in infosByEmittedDial.Values)
                {
                    infosForDial.RemoveAll(unavailableOwnerInfos.ContainsKey);
                }

                foreach (var infosForDial in infosByMasterDial.Values)
                {
                    infosForDial.RemoveAll(unavailableOwnerInfos.ContainsKey);
                }

                foreach (var (info, owner) in unavailableOwnerInfos)
                {
                    if (info.FormId != 0
                        && infoFormIdMap.Remove(info.FormId, out var allocatedInfoFormId))
                    {
                        validFormIds.Remove(allocatedInfoFormId);
                    }

                    stats.IncrementSkipped("INFO");
                    stats.IncrementDropReason("script-variable.owner-not-emitted");
                    sink.Warn(
                        "Building dialog section",
                        $"Suppressed new INFO 0x{info.FormId:X8}: GetScriptVariable owner " +
                        $"0x{owner.Source:X8} resolved to 0x{owner.Resolved:X8}, but that " +
                        "REFR/ACHR/ACRE was not actually emitted. The whole INFO is atomic; " +
                        "no condition was dropped or widened.",
                        "INFO",
                        info.FormId,
                        "script-variable.owner-not-emitted",
                        new Dictionary<string, string?>(StringComparer.Ordinal)
                        {
                            ["script-variable-source-owner-form-id"] = $"0x{owner.Source:X8}",
                            ["script-variable-target-owner-form-id"] = $"0x{owner.Resolved:X8}",
                            ["owner-liveness-source"] = "master-or-actual-cell-emission",
                        });
                }

                dialogRemapTable = MergeRemapTables(remapTable, dialFormIdMap);
                dialogRemapTable = MergeRemapTables(dialogRemapTable, infoFormIdMap);
            }
        }

        // INFOs bypass the top-level PlanWriter because they are nested under DIAL topic
        // GRUPs. Apply the same atomic inline-script gate here before any INFO bytes are
        // emitted. Iterate to a fixed point: removing one unsafe INFO also removes its
        // allocated FormID, which can expose an SCRO dangle in another INFO's result script.
        while (true)
        {
            var unsafeInfos = new Dictionary<DialogueRecord, InlineScriptReferenceValidator.Issue>(
                ReferenceEqualityComparer.Instance);
            foreach (var info in infosByEmittedDial.Values.SelectMany(static list => list)
                         .Concat(infosByMasterDial.Values.SelectMany(static list => list)))
            {
                var issue = InlineScriptReferenceValidator.FindFirstIssue(
                    info, validFormIds, dialogRemapTable);
                if (issue is not null)
                {
                    unsafeInfos.TryAdd(info, issue);
                }
            }

            if (unsafeInfos.Count == 0)
            {
                break;
            }

            foreach (var infosForDial in infosByEmittedDial.Values)
            {
                infosForDial.RemoveAll(unsafeInfos.ContainsKey);
            }

            foreach (var infosForDial in infosByMasterDial.Values)
            {
                infosForDial.RemoveAll(unsafeInfos.ContainsKey);
            }

            foreach (var (info, issue) in unsafeInfos)
            {
                if (info.FormId != 0
                    && infoFormIdMap.Remove(info.FormId, out var allocatedInfoFormId))
                {
                    validFormIds.Remove(allocatedInfoFormId);
                }

                stats.IncrementSkipped("INFO");
                stats.IncrementDropReason("inline-script.unsafe-info");
                sink.Warn(
                    "Building dialog section",
                    $"Suppressed new INFO 0x{info.FormId:X8}: {issue.Message} " +
                    "Inline SCDA/SLSD/SCRO/SCRV is atomic; no slot was dropped or zero-filled.",
                    "INFO",
                    info.FormId,
                    "inline-script.suppress-unsafe-owner",
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["script-path"] = issue.ScriptPath,
                        ["reference-field"] = issue.FieldPath,
                        ["target-source-form-id"] = issue.SourceFormId.HasValue
                            ? $"0x{issue.SourceFormId.Value:X8}"
                            : null,
                        ["target-emitted-form-id"] = issue.ResolvedFormId.HasValue
                            ? $"0x{issue.ResolvedFormId.Value:X8}"
                            : null,
                        ["local-variable-id"] = issue.LocalVariableId?.ToString(
                            CultureInfo.InvariantCulture),
                    });
            }

            dialogRemapTable = MergeRemapTables(remapTable, dialFormIdMap);
            dialogRemapTable = MergeRemapTables(dialogRemapTable, infoFormIdMap);
        }

        // Shared INFO overlays only replace retail NAM1 text, but planner-all's unsafe-
        // override verdict is KeepMaster for the complete owner. Honor that here as well:
        // an unsafe captured inline bundle must not cause even a partial INFO override on
        // the separate dialogue path.
        foreach (var overlaysForDial in overlaysByMasterDial.Values)
        {
            for (var index = overlaysForDial.Count - 1; index >= 0; index--)
            {
                var overlay = overlaysForDial[index];
                var issue = InlineScriptReferenceValidator.FindFirstIssue(
                    overlay.PrototypeInfo, validFormIds, dialogRemapTable);
                if (issue is null)
                {
                    continue;
                }

                overlaysForDial.RemoveAt(index);
                stats.IncrementSkipped("INFO");
                stats.IncrementDropReason("inline-script.unsafe-info-override");
                sink.Warn(
                    "Building dialog section",
                    $"Retained master INFO 0x{overlay.MasterInfo.Record.Header.FormId:X8}: " +
                    $"{issue.Message} Inline SCDA/SLSD/SCRO/SCRV is atomic.",
                    "INFO",
                    overlay.MasterInfo.Record.Header.FormId,
                    "inline-script.suppress-unsafe-owner",
                    new Dictionary<string, string?>(StringComparer.Ordinal)
                    {
                        ["owner-disposition"] = "KeepMaster",
                        ["script-path"] = issue.ScriptPath,
                        ["reference-field"] = issue.FieldPath,
                        ["target-source-form-id"] = issue.SourceFormId.HasValue
                            ? $"0x{issue.SourceFormId.Value:X8}"
                            : null,
                        ["target-emitted-form-id"] = issue.ResolvedFormId.HasValue
                            ? $"0x{issue.ResolvedFormId.Value:X8}"
                            : null,
                        ["local-variable-id"] = issue.LocalVariableId?.ToString(
                            CultureInfo.InvariantCulture),
                    });
            }
        }

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
        foreach (var masterDialId in infosByMasterDial.Keys.Concat(overlaysByMasterDial.Keys).Distinct())
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

        var topLevelDialFormIds = newTopics
            .Where(static topic => topic.IsTopLevel)
            .Select(topic => dialFormIdMap[topic.FormId])
            .ToHashSet();
        var synthesizedReturnLinks = ApplyRootReturnTopicLinks(
            infosByEmittedDial, infosByMasterDial, dialEditorIdByFormId,
            topLevelDialFormIds, exitTopicResolver);

        using var stream = new MemoryStream();
        var topLabel = "DIAL"u8.ToArray();
        var topPos = WriteGrupHeader(stream, topLabel, 0);

        var emittedDials = 0;
        var emittedMasterDialAnchors = 0;
        var emittedInfos = 0;
        var emittedSharedInfoOverlays = 0;
        var overlaidResponseTexts = 0;
        var sanitizedFieldCount = 0;
        var droppedConditions = 0;
        var remappedCtdaParameters = 0;
        var droppedNoQstiInfos = 0;
        var droppedUnboundMasterTopicInfos = 0;
        var droppedEmptyStubMasterTopicInfos = 0;
        var infoSourceToAllocated = new Dictionary<uint, uint>();
        var audioBindings = new List<EmittedDialogueAudioBinding>();
        var producerLedger = new DialogueProducerLedgerBuilder(
            questVariableProducerMappings,
            remapTable);
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

            // PNAM chain state: per topic GRUP, per quest. Shipped-DLC convention
            // (byte-verified against every FNV DLC): each quest's INFOs form a linked list
            // anchored at PNAM=0, subsequent entries naming the previous plugin-local INFO.
            // The proto's captured PreviousInfo values are unreliable post-remap, so chains
            // are computed at emission time from actual emitted FormIDs.
            var lastEmittedInfoByQuest = new Dictionary<uint, uint>();

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

                patched = patched with
                {
                    PreviousInfo = lastEmittedInfoByQuest.TryGetValue(patched.QuestFormId.Value, out var prevInfo)
                        ? prevInfo
                        : 0u
                };

                var infoEncoded = InfoEncoder.EncodeNew(patched, validFormIds, dialogRemapTable);
                ReportInfoEncodingWarnings(infoEncoded, info.FormId, sink);
                if (infoEncoded.Subrecords.Count == 0)
                {
                    stats.IncrementSkipped("INFO");
                    continue;
                }

                var infoBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
                    "INFO", newInfoId, flags: 0u, infoEncoded.Subrecords);
                stream.Write(infoBytes);
                producerLedger.RecordEmittedInfo(
                    info,
                    patched,
                    validFormIds,
                    dialogRemapTable);
                stats.IncrementEmitted("INFO");
                stats.NewRecordsEmitted++;
                emittedInfos++;
                lastEmittedInfoByQuest[patched.QuestFormId.Value] = newInfoId;
                if (info.FormId != 0 && info.FormId != newInfoId)
                {
                    infoSourceToAllocated[info.FormId] = newInfoId;
                }

                CollectAudioBindings(
                    audioBindings, info, patched, newInfoId, newDialId,
                    dialEditorIdByFormId, voiceTypeEditorIdsByFormId,
                    npcVoiceTypeByNpcFormId, masterRecordsByFormId,
                    questEditorIdsByFormId);
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, childrenPos);
        }

        var extendedAnchorQstiCount = 0;
        var masterDialIds = infosByMasterDial.Keys
            .Concat(overlaysByMasterDial.Keys)
            .Distinct()
            .OrderBy(static formId => formId);
        foreach (var masterDialId in masterDialIds)
        {
            var infosForDial = infosByMasterDial.GetValueOrDefault(masterDialId) ?? [];
            var overlaysForDial = overlaysByMasterDial.GetValueOrDefault(masterDialId) ?? [];
            if ((infosForDial.Count == 0 && overlaysForDial.Count == 0) ||
                !masterRecordsByFormId.TryGetValue(masterDialId, out var masterDialRecord) ||
                masterDialRecord.Header.Signature != "DIAL")
            {
                droppedUnavailableMasterDialInfos += infosForDial.Count + overlaysForDial.Count;
                continue;
            }

            // The engine looks at a DIAL's QSTI list to know which quests own INFOs under
            // that topic. When we attach new INFOs to a master TPIC (GREETING, HELLO, etc.)
            // those INFOs reference new quests not in master's QSTI list. Without extending
            // QSTI, the engine never checks the new quest's INFOs against the speaker, so
            // every new NPC's master-topic dialogue silently fails. Reconstruct the master
            // record with QSTI appended; everything else stays verbatim.
            var retainMasterQstis = diagnosticRetainMasterSubrecords
                ?.GetValueOrDefault(masterDialId)
                ?.Contains("QSTI") == true;
            var newQstis = retainMasterQstis
                ? []
                : CollectNewAnchorQstis(infosForDial, masterDialRecord, validFormIds, remapTable);
            extendedAnchorQstiCount += newQstis.Count;
            stream.Write(ReconstructDialWithExtraQstis(masterDialRecord, newQstis));
            stats.IncrementEmitted("DIAL");
            stats.OverridesEmitted++;
            emittedMasterDialAnchors++;

            var childLabel = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(childLabel, masterDialId);
            var childrenPos = WriteGrupHeader(stream, childLabel, 7);

            // Shared INFOs are written in retail file order before plugin-new INFO chains.
            // Only NAM1 payloads are replaced; all scripts, conditions, response metadata,
            // links, unknown subrecords, and the retail FormID remain byte-for-byte master.
            foreach (var overlay in overlaysForDial)
            {
                var overlayResult = DialogueInfoOverlayWriter.Build(
                    overlay.MasterInfo.Record, overlay.PrototypeInfo,
                    diagnosticRetainMasterSubrecords?.GetValueOrDefault(
                        overlay.MasterInfo.Record.Header.FormId));
                stream.Write(overlayResult.RecordBytes);
                stats.IncrementEmitted("INFO");
                stats.OverridesEmitted++;
                emittedInfos++;
                emittedSharedInfoOverlays++;
                overlaidResponseTexts += overlayResult.ReplacedTextCount;

                var masterScope = DialogueCombinePlanner.ResolveMasterScope(
                    overlay.MasterInfo, overlay.PrototypeInfo);
                var bindingInfo = overlay.PrototypeInfo with
                {
                    FormId = overlay.MasterInfo.Record.Header.FormId,
                    TopicFormId = masterDialId,
                    QuestFormId = masterScope.QuestFormId,
                    SpeakerFormId = masterScope.SpeakerFormId,
                    Responses = overlayResult.EmittedResponses.ToList(),
                    AudioSourceInfoFormId = overlay.PrototypeInfo.FormId,
                    AudioSourceResponseNumbers = overlayResult.SourceResponseNumbers.ToList()
                };
                CollectAudioBindings(
                    audioBindings, bindingInfo, bindingInfo,
                    overlay.MasterInfo.Record.Header.FormId, masterDialId,
                    dialEditorIdByFormId, voiceTypeEditorIdsByFormId,
                    npcVoiceTypeByNpcFormId, masterRecordsByFormId,
                    questEditorIdsByFormId, isRetailInfoOverlay: true);
            }

            // Same per-quest PNAM chain construction as the new-DIAL loop above.
            var lastEmittedInfoByQuest = new Dictionary<uint, uint>();

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

                // INFOs attached to MASTER system topics (GREETING etc.) are evaluated for
                // EVERY speaker in the game. Proto INFOs whose conditions reference quest
                // variables that don't translate get those conditions REMOVED by the
                // engine at load ("Unable to find variableID..."), turning them into
                // near-unconditional greetings that hijack vanilla NPCs (Sunny greeting
                // with Gomorrah gambler lines; conversation menus breaking game-wide).
                // Require a hard speaker binding: a GetIsID/GetIsVoiceType condition or an
                // explicit speaker link. Unbound INFOs are dropped — proto content that
                // can't be safely scoped has no business inside a shared master topic.
                if (masterDialId < FirstNonSystemDialFormId
                    && !HasSpeakerBindingCondition(patched))
                {
                    droppedUnboundMasterTopicInfos++;
                    stats.IncrementSkipped("INFO");
                    stats.IncrementDropReason("info.master-topic-unbound");
                    continue;
                }

                // Even a correctly speaker-bound INFO must carry renderable content here.
                // Runtime conversation-menu reconstructions yield content-less stubs (empty
                // NAM1, zeroed TRDT, only TCLT choice links); chained into a shared master
                // topic they SHADOW the actor's retail greeting — the engine selects the
                // stub, renders nothing, and the conversation instantly closes (Victor /
                // Doc Mitchell / the Gomorrah greeter). Stubs stay
                // legitimate under NEW proto topics, which re-emit their own menu structure.
                // EXEMPT: GreetingEntrySynthesizer entries — their single empty response is
                // the designed "[silence], then the topic list" entry mechanism; dropping
                // them leaves proto NPCs with only Goodbye (Ulysses)
                // and severs synthesized topic entry points on retail NPCs.
                if (!HasRenderableResponse(patched))
                {
                    if (!patched.IsSynthesizedGreetingEntry)
                    {
                        droppedEmptyStubMasterTopicInfos++;
                        stats.IncrementSkipped("INFO");
                        stats.IncrementDropReason("info.master-topic-empty-stub");
                        continue;
                    }

                    stats.IncrementDropReason("info.master-topic-synth-entry-kept");
                }

                patched = patched with
                {
                    PreviousInfo = lastEmittedInfoByQuest.TryGetValue(patched.QuestFormId.Value, out var prevInfo)
                        ? prevInfo
                        : 0u
                };

                var infoEncoded = InfoEncoder.EncodeNew(patched, validFormIds, dialogRemapTable);
                ReportInfoEncodingWarnings(infoEncoded, info.FormId, sink);
                if (infoEncoded.Subrecords.Count == 0)
                {
                    stats.IncrementSkipped("INFO");
                    continue;
                }

                var infoBytes = PluginRecordByteBuilder.BuildNewRecordBytes(
                    "INFO", newInfoId, flags: 0u, infoEncoded.Subrecords);
                stream.Write(infoBytes);
                producerLedger.RecordEmittedInfo(
                    info,
                    patched,
                    validFormIds,
                    dialogRemapTable);
                stats.IncrementEmitted("INFO");
                stats.NewRecordsEmitted++;
                emittedInfos++;
                lastEmittedInfoByQuest[patched.QuestFormId.Value] = newInfoId;
                if (info.FormId != 0 && info.FormId != newInfoId)
                {
                    infoSourceToAllocated[info.FormId] = newInfoId;
                }

                CollectAudioBindings(
                    audioBindings, info, patched, newInfoId, masterDialId,
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
            $"+ {emittedInfos - emittedSharedInfoOverlays:N0} new INFO record(s) + " +
            $"{emittedSharedInfoOverlays:N0} retail INFO overlay(s) " +
            $"({overlaidResponseTexts:N0} NAM1 replacement(s); all other retail bytes preserved; " +
            $"{combinePlan.NoOpOverlaysSuppressed:N0} no-op overlay(s) with no text delta suppressed). " +
            $"Collapsed {combinePlan.DuplicateInfosCollapsed:N0} duplicate INFO capture(s), rehomed " +
            $"{combinePlan.CutInfosRehomed:N0} safe cut INFO(s), and suppressed " +
            $"{combinePlan.SystemInfosSuppressed:N0} unsafe/colliding system INFO(s). " +
            $"Extended {extendedAnchorQstiCount:N0} master-DIAL QSTI binding(s) so the engine knows " +
            "new quests speak master topics. " +
            $"Recovered {recoveredRawParentInfos:N0} INFO(s) via raw Topic-Children ancestry whose " +
            "runtime topic attribution was missing or dangling. " +
            $"Dropped {droppedUnavailableMasterDialInfos:N0} INFO(s) with unavailable master-DIAL TPIC, " +
            $"{droppedOrphanInfos:N0} orphan INFO(s), {droppedNoQstiInfos:N0} INFO(s) with no QSTI " +
            "(engine refuses topic-info inserts when QSTI is missing), and " +
            $"{droppedUnboundMasterTopicInfos:N0} master-topic INFO(s) without a hard speaker binding " +
            "(GetIsID/GetIsVoiceType/ANAM — unbound proto INFOs hijack vanilla NPC greetings once the " +
            "engine strips their unresolvable conditions), and " +
            $"{droppedEmptyStubMasterTopicInfos:N0} content-less master-topic stub INFO(s) " +
            "(empty menu reconstructions that shadow retail greetings). " +
            $"Synthesized {synthesizedReturnLinks:N0} terminal INFO root-return topic link(s). " +
            $"Sanitized {sanitizedFieldCount:N0} unresolvable " +
            $"cross-record FormID reference(s); dropped {droppedConditions:N0} CTDA condition(s) " +
            $"with dangling Reference or Parameter FormIDs; remapped {remappedCtdaParameters:N0} " +
            "CTDA Parameter1/Parameter2 FormID(s) via the runtime→emitted alias table.",
            code: "dialog.emitted");

        return new DialogSectionResult(
            stream.ToArray(),
            null,
            infoSourceToAllocated,
            audioBindings,
            producerLedger.Build());
    }

    /// <summary>
    ///     Per (quest, speaker), picks ONE master DIAL from the proto's retail-FormID-
    ///     colliding topics to serve as the synthesized choice chains' exit link. The pick
    ///     must have a retail INFO that PASSES for the speaker (GetIsID match or
    ///     unconditioned) or the engine hides the choice — quest-level picks fail on shared
    ///     quests like VFreeformFreeside where the lowest-FormID topic belongs to a
    ///     different NPC. Ranking: goodbye-flagged passing → terminating (link-less)
    ///     passing → any passing → quest-wide Min fallback; ties broken by Min FormID.
    /// </summary>
    private sealed class ExitTopicResolver
    {
        private readonly Dictionary<uint, List<uint>> _collidingByQuest;
        private readonly Dictionary<uint, MasterDialConditions> _conditionsByDial;
        private readonly Dictionary<(uint Quest, uint Speaker), uint> _memo = new();

        private ExitTopicResolver(
            Dictionary<uint, List<uint>> collidingByQuest,
            Dictionary<uint, MasterDialConditions> conditionsByDial)
        {
            _collidingByQuest = collidingByQuest;
            _conditionsByDial = conditionsByDial;
        }

        public static ExitTopicResolver Build(
            IReadOnlyList<DialogTopicRecord> topics,
            NewVsOverrideClassifier classifier,
            IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId)
        {
            var collidingByQuest = new Dictionary<uint, List<uint>>();
            var collidingDials = new HashSet<uint>();
            foreach (var topic in topics)
            {
                // Engine/system DIALs (GREETING 0xC8, HELLO 0xD2, GOODBYE 0xD4, …) live in
                // the sub-0x1000 FormID block and are captured by every dump — they collide
                // with master by definition and their huge INFO sets trivially pass every
                // rank, hijacking the exit pick (a TCLT back to GREETING is nonsense).
                if (topic.FormId < 0x00001000u
                    || !classifier.IsOverride(topic.FormId)
                    || !masterRecordsByFormId.TryGetValue(topic.FormId, out var masterDial)
                    || masterDial.Header.Signature != "DIAL")
                {
                    continue;
                }

                // Quest membership + type come from MASTER's own record — captured
                // topic→quest attribution is unreliable (a Hidden Valley Conversation topic
                // was attributed to Beatrix's Freeside quest). Choice links target
                // Topic-type DIALs only (DATA type byte 0).
                var dialData = masterDial.Subrecords.FirstOrDefault(s =>
                    s.Signature == "DATA" && s.Data.Length >= 1);
                if (dialData is null || dialData.Data[0] != 0)
                {
                    continue;
                }

                var addedToAnyQuest = false;
                foreach (var qsti in masterDial.Subrecords)
                {
                    if (qsti.Signature != "QSTI" || qsti.Data.Length < 4)
                    {
                        continue;
                    }

                    var quest = BinaryPrimitives.ReadUInt32LittleEndian(qsti.Data.AsSpan(0, 4));
                    if (quest == 0)
                    {
                        continue;
                    }

                    if (!collidingByQuest.TryGetValue(quest, out var list))
                    {
                        list = [];
                        collidingByQuest[quest] = list;
                    }

                    if (!list.Contains(topic.FormId))
                    {
                        list.Add(topic.FormId);
                        addedToAnyQuest = true;
                    }
                }

                if (addedToAnyQuest)
                {
                    collidingDials.Add(topic.FormId);
                }
            }

            var conditions = collidingDials.Count == 0
                ? new Dictionary<uint, MasterDialConditions>()
                : IndexMasterDialConditions(masterRecordsByFormId, collidingDials);
            return new ExitTopicResolver(collidingByQuest, conditions);
        }

        /// <summary>0 when the quest has no colliding master topics.</summary>
        public uint Resolve(uint quest, uint? speaker)
        {
            if (!_collidingByQuest.TryGetValue(quest, out var dials))
            {
                return 0;
            }

            var key = (quest, speaker ?? 0u);
            if (_memo.TryGetValue(key, out var memoized))
            {
                return memoized;
            }

            var exit = PickForSpeaker(dials, speaker ?? 0u);
            _memo[key] = exit;
            return exit;
        }

        private uint PickForSpeaker(List<uint> dials, uint speaker)
        {
            uint bestGoodbye = 0, bestTerminating = 0, bestPassing = 0;
            foreach (var dial in dials)
            {
                if (!_conditionsByDial.TryGetValue(dial, out var c))
                {
                    continue;
                }

                if (c.GoodbyePassesFor(speaker) && (bestGoodbye == 0 || dial < bestGoodbye))
                {
                    bestGoodbye = dial;
                }

                if (c.TerminatingPassesFor(speaker) && (bestTerminating == 0 || dial < bestTerminating))
                {
                    bestTerminating = dial;
                }

                if (c.AnyPassesFor(speaker) && (bestPassing == 0 || dial < bestPassing))
                {
                    bestPassing = dial;
                }
            }

            if (bestGoodbye != 0)
            {
                return bestGoodbye;
            }

            if (bestTerminating != 0)
            {
                return bestTerminating;
            }

            return bestPassing != 0 ? bestPassing : dials.Min();
        }

        /// <summary>
        ///     One offset-ordered walk over the master stream (INFO parentage is
        ///     GRUP-positional). Per colliding DIAL, aggregates which speakers its INFOs
        ///     pass for. INFO DATA: type(0), nextSpeaker(1), flags16(2-3) — Goodbye 0x0001.
        ///     CTDA: functionIndex at offset 8 (GetIsID = 72), param1 at offset 12.
        ///     "Terminating" = INFO with no TCLT links (playing it returns to the topic
        ///     list, where the engine Goodbye is available).
        /// </summary>
        private static Dictionary<uint, MasterDialConditions> IndexMasterDialConditions(
            IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
            HashSet<uint> collidingDials)
        {
            var index = new Dictionary<uint, MasterDialConditions>();
            uint currentDial = 0;
            foreach (var record in masterRecordsByFormId.Values
                         .Where(r => r.Header.Signature is "DIAL" or "INFO")
                         .OrderBy(r => r.Offset))
            {
                if (record.Header.Signature == "DIAL")
                {
                    currentDial = record.Header.FormId;
                    continue;
                }

                if (currentDial == 0 || !collidingDials.Contains(currentDial))
                {
                    continue;
                }

                var isGoodbye = false;
                var hasTclt = false;
                var getIsIdSpeakers = new HashSet<uint>();
                var conditionCount = 0;
                foreach (var sub in record.Subrecords)
                {
                    switch (sub.Signature)
                    {
                        case "DATA" when sub.Data.Length >= 4:
                            isGoodbye = (sub.Data[2] & 0x01) != 0;
                            break;
                        case "TCLT":
                            hasTclt = true;
                            break;
                        case "CTDA" when sub.Data.Length >= 16:
                            conditionCount++;
                            if (DialogueSpeakerBinding.IsPositiveSubjectGetIsId(sub.Data))
                            {
                                getIsIdSpeakers.Add(
                                    BinaryPrimitives.ReadUInt32LittleEndian(sub.Data.AsSpan(12, 4)));
                            }

                            break;
                    }
                }

                if (!index.TryGetValue(currentDial, out var conditions))
                {
                    conditions = new MasterDialConditions();
                    index[currentDial] = conditions;
                }

                if (getIsIdSpeakers.Count == 0)
                {
                    conditions.Record(
                        speaker: null,
                        unconditioned: conditionCount == 0,
                        goodbye: isGoodbye,
                        terminating: !hasTclt);
                }
                else
                {
                    foreach (var speaker in getIsIdSpeakers)
                    {
                        conditions.Record(
                            speaker,
                            unconditioned: false,
                            goodbye: isGoodbye,
                            terminating: !hasTclt);
                    }
                }
            }

            return index;
        }
    }

    /// <summary>Per-master-DIAL aggregate of which speakers its INFOs pass for.</summary>
    private sealed class MasterDialConditions
    {
        private readonly HashSet<uint> _goodbyeSpeakers = [];
        private readonly HashSet<uint> _terminatingSpeakers = [];
        private readonly HashSet<uint> _anySpeakers = [];
        private bool _goodbyeUnconditioned;
        private bool _terminatingUnconditioned;
        private bool _anyUnconditioned;

        public void Record(uint? speaker, bool unconditioned, bool goodbye, bool terminating)
        {
            Track(_anySpeakers, ref _anyUnconditioned, speaker, unconditioned);
            if (goodbye)
            {
                Track(_goodbyeSpeakers, ref _goodbyeUnconditioned, speaker, unconditioned);
            }

            if (terminating)
            {
                Track(_terminatingSpeakers, ref _terminatingUnconditioned, speaker, unconditioned);
            }
        }

        public bool GoodbyePassesFor(uint speaker) =>
            _goodbyeUnconditioned || _goodbyeSpeakers.Contains(speaker);

        public bool TerminatingPassesFor(uint speaker) =>
            _terminatingUnconditioned || _terminatingSpeakers.Contains(speaker);

        public bool AnyPassesFor(uint speaker) =>
            _anyUnconditioned || _anySpeakers.Contains(speaker);

        private static void Track(HashSet<uint> speakers, ref bool unconditioned, uint? speaker, bool isUnconditioned)
        {
            if (isUnconditioned)
            {
                unconditioned = true;
            }
            else if (speaker is > 0)
            {
                speakers.Add(speaker.Value);
            }
        }
    }

    private static int ApplyRootReturnTopicLinks(
        Dictionary<uint, List<DialogueRecord>> infosByEmittedDial,
        Dictionary<uint, List<DialogueRecord>> infosByMasterDial,
        Dictionary<uint, string> dialEditorIdByFormId,
        HashSet<uint> topLevelDialFormIds,
        ExitTopicResolver? exitTopicResolver = null)
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
            if (topLevelDialFormIds.Contains(dialId))
            {
                continue;
            }

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
                    || info.IsRehomedCutDialogue
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

                var merged = new List<uint>(rootLinks.Count + 1);
                var seen = new HashSet<uint>();
                foreach (var link in rootLinks)
                {
                    if (link != 0 && seen.Add(link))
                    {
                        merged.Add(link);
                    }
                }

                // Reconnect the quest's retail exit branch (a master DIAL the collision
                // gate excluded): its retail INFOs terminate, returning the player to the
                // topic list — otherwise the stamped links form an inescapable ring.
                var exitTopic = exitTopicResolver?.Resolve(questId, info.SpeakerFormId) ?? 0;
                if (exitTopic != 0 && seen.Add(exitTopic))
                {
                    merged.Add(exitTopic);
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
        FormIdAllocator allocator,
        IReadOnlyDictionary<uint, uint>? preallocatedNewFormIds)
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

            map[info.FormId] = ResolvePreallocatedFormId(
                info.FormId, preallocatedNewFormIds, allocator);
        }

        return map;
    }

    private static uint ResolvePreallocatedFormId(
        uint sourceFormId,
        IReadOnlyDictionary<uint, uint>? preallocatedNewFormIds,
        FormIdAllocator allocator)
    {
        return sourceFormId != 0
               && preallocatedNewFormIds is not null
               && preallocatedNewFormIds.TryGetValue(sourceFormId, out var preallocated)
               && preallocated != 0
            ? preallocated
            : allocator.Allocate();
    }

    private static uint ResolvePreallocatedInfoId(
        DialogueRecord info,
        Dictionary<uint, uint> infoFormIdMap,
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

        // Callers can contribute aliases in more than one phase (prototype -> master,
        // then source/preallocated -> final emitted). Collapse every chain here so later
        // INFO condition sanitation writes the same terminal identity used by the placed-
        // owner liveness gate. A one-hop table could retain the INFO against the terminal
        // owner and then drop or serialize its CTDA against an intermediate alias.
        foreach (var sourceFormId in merged.Keys.ToArray())
        {
            merged[sourceFormId] = ResolveTerminalAlias(sourceFormId, merged);
        }

        return merged;
    }

    private static uint ResolveTerminalAlias(
        uint sourceFormId,
        IReadOnlyDictionary<uint, uint>? remapTable)
    {
        if (sourceFormId == 0 || remapTable is null)
        {
            return sourceFormId;
        }

        var current = sourceFormId;
        var visited = new HashSet<uint>();
        while (visited.Add(current)
               && remapTable.TryGetValue(current, out var mapped)
               && mapped != current)
        {
            if (mapped == 0)
            {
                return 0;
            }

            current = mapped;
        }

        return current;
    }

    private static void ReportInfoEncodingWarnings(
        EncodedRecord encoded,
        uint sourceInfoFormId,
        IConversionProgressSink sink)
    {
        foreach (var warning in encoded.Warnings)
        {
            sink.Warn(
                "Building dialog section",
                warning,
                "INFO",
                sourceInfoFormId,
                "inline-script.encoder-rejected-owner");
        }
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
        DialogueRecord original,
        DialogueRecord patched,
        uint allocatedInfoId,
        uint parentDialFormId,
        Dictionary<uint, string> dialEditorIdByFormId,
        IReadOnlyDictionary<uint, string>? voiceTypeEditorIdsByFormId,
        IReadOnlyDictionary<uint, uint>? npcVoiceTypeByNpcFormId,
        IReadOnlyDictionary<uint, ParsedMainRecord> masterRecordsByFormId,
        IReadOnlyDictionary<uint, string>? questEditorIdsByFormId,
        bool isRetailInfoOverlay = false)
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

        // Resolve the quest EditorId via the INFO record's QSTI link. We first try the
        // unified DMP and master lookup, then fall back to scanning master records directly
        // so that master-quest INFOs still receive a QuestEditorId. The engine constructs
        // voice filenames by prepending the quest EditorId, so this attribution is needed
        // even when the quest itself is unchanged from master.
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
            var sourceRespNum = i < original.AudioSourceResponseNumbers.Count
                && original.AudioSourceResponseNumbers[i] != 0
                    ? original.AudioSourceResponseNumbers[i]
                    : respNum;
            var sourceInfoFormId = original.AudioSourceInfoFormId is > 0
                ? original.AudioSourceInfoFormId.Value
                : original.FormId;
            bindings.Add(new EmittedDialogueAudioBinding
            {
                AllocatedInfoFormId = allocatedInfoId,
                SourceInfoFormId = sourceInfoFormId,
                ParentDialEditorId = dialEdid,
                VoiceTypeEditorId = voiceTypeEdid,
                ResponseNumber = respNum,
                SourceResponseNumber = sourceRespNum,
                IsRetailInfoOverlay = isRetailInfoOverlay,
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

        // New NPCs: source-FormID-keyed lookup populated by PluginConversionPipeline from the encoded
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
            // Canonical FNV DIAL order is EDID, QSTI*, FULL, PNAM, TDUM?, DATA — the added
            // quest links must land in the QSTI block. Insert after the last existing QSTI;
            // when the master topic has none (GREETING etc. on some masters), insert right
            // after EDID. Appending at the end put QSTI after DATA, which FNVEdit flags as
            // out-of-order and the engine's sequential DIAL reader misparses (wrong-greeting
            // class).
            var insertAfter = -1;
            for (var i = masterDialRecord.Subrecords.Count - 1; i >= 0; i--)
            {
                if (masterDialRecord.Subrecords[i].Signature == "QSTI")
                {
                    insertAfter = i;
                    break;
                }
            }

            if (insertAfter < 0)
            {
                for (var i = 0; i < masterDialRecord.Subrecords.Count; i++)
                {
                    if (masterDialRecord.Subrecords[i].Signature == "EDID")
                    {
                        insertAfter = i;
                        break;
                    }
                }
            }

            if (insertAfter < 0)
            {
                foreach (var qsti in extraQstis)
                {
                    SubrecordEncoder.WriteFormIdSubrecord(subWriter, "QSTI", qsti);
                }
            }

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

    /// <summary>FNV condition function: GetQuestVariable (quest + script variable index).</summary>
    private const ushort GetQuestVariableFunctionIndex = 79;

    /// <summary>FNV condition function: GetVariable-style scripted lookup (variable index).</summary>
    private const ushort GetVariableFunctionIndex = 449;

    /// <summary>
    ///     True when the INFO is safe to attach under a shared MASTER topic (GREETING etc.),
    ///     which the engine evaluates for every actor in the game. Two requirements:
    ///     a hard per-actor binding — explicit speaker link (ANAM) or a GetIsID condition
    ///     naming a specific base actor (voice-type bindings are NOT sufficient: voice types
    ///     are shared across many vanilla NPCs) — and NO quest-variable conditions, because
    ///     our proto variable-index translation is unreliable and the engine strips
    ///     unresolvable conditions at load ("Unable to find variableID..."), widening the
    ///     INFO's audience to everyone sharing the remaining conditions.
    /// </summary>
    private static bool HasSpeakerBindingCondition(DialogueRecord info)
    {
        if (info.Conditions.Any(condition =>
                condition.FunctionIndex is GetQuestVariableFunctionIndex or GetVariableFunctionIndex))
        {
            return false;
        }

        return DialogueSpeakerBinding.GetExactSpeaker(info) is > 0;
    }

    /// <summary>
    ///     True when the INFO carries at least one renderable response — non-empty NAM1 text
    ///     or a TRDT voice sound. Content-less INFOs (runtime menu-structure stubs whose only
    ///     payload is TCLT choice links) must never attach to a shared MASTER topic: bound to
    ///     a retail NPC and chained ahead of the retail greetings, the engine selects the
    ///     stub, plays nothing, and instantly ends the conversation. The encoder's
    ///     "(NOT FOUND IN CRASH DUMP)" placeholder is NOT renderable — a placeholder-seeded
    ///     model must never shadow a retail greeting either.
    /// </summary>
    private static bool HasRenderableResponse(DialogueRecord info) =>
        info.Responses.Any(response =>
            (!string.IsNullOrWhiteSpace(response.Text)
             && !string.Equals(response.Text, DialogueTextBackfill.PlaceholderText, StringComparison.Ordinal))
            || response.SoundFormId is > 0);

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

    private const int IdentityEvidencePreviewLimit = 8;
    private const int IdentityConflictPreviewLimit = 4;
    private const int IdentityConflictTextLimit = 192;

    internal static void ReportIdentityTelemetry(
        IReadOnlyList<DialogueTopicIdentity> identities,
        IConversionProgressSink sink)
    {
        var exactTopicAnchors = identities.Count(static identity =>
            identity.Kind == DialogueTopicIdentityKind.MasterAnchor);
        var sharedChildTopicAnchors = identities.Count(static identity =>
            identity.Kind == DialogueTopicIdentityKind.SharedChildAnchor);
        var distinctPrototypeTopics = identities.Count(static identity =>
            identity.Kind == DialogueTopicIdentityKind.PrototypeDistinct);
        var ambiguousTopicIdentities = identities.Count(static identity =>
            identity.Kind == DialogueTopicIdentityKind.Ambiguous);
        var orphanRawParentClaims = identities.Count(static identity =>
            identity.IsOrphanRawParentClaim);
        var conflictedTopicIdentities = identities.Count(static identity =>
            identity.Conflicts.Count > 0);
        // A Decision event is intentionally visible in non-verbose corpus runs. This is
        // diagnostic-only, but an explicit zero is still required to distinguish an empty
        // dialogue capture from a converter that never ran the structural classifier.
        sink.Decision("Classifying dialog identity",
            $"Classified DIAL identity as {InvariantCount(exactTopicAnchors)} exact master anchor(s), " +
            $"{InvariantCount(sharedChildTopicAnchors)} shared-child structural anchor(s), " +
            $"{InvariantCount(distinctPrototypeTopics)} prototype-distinct, and " +
            $"{InvariantCount(ambiguousTopicIdentities)} ambiguous; classification is diagnostic-only. " +
            $"Provenance includes {InvariantCount(orphanRawParentClaims)} orphan raw-parent claim(s) and " +
            $"{InvariantCount(conflictedTopicIdentities)} conflicted identity record(s).",
            code: "dialogue.identity.summary");
        foreach (var identity in identities.OrderBy(static identity => identity.PrototypeDialFormId))
        {
            var master = identity.MasterDialFormId is { } masterDial
                ? $"0x{masterDial:X8}"
                : "none";
            var evidence = identity.EvidenceInfoFormIds
                .Where(static formId => formId != 0)
                .Distinct()
                .Order()
                .ToList();
            var conflicts = identity.Conflicts
                .Where(static conflict => !string.IsNullOrWhiteSpace(conflict))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToList();
            sink.Decision(
                "Classifying dialog identity",
                $"kind={identity.Kind}; master={master}; orphanRawParent={identity.IsOrphanRawParentClaim}; " +
                $"evidenceINFOCount={InvariantCount(evidence.Count)}; " +
                $"evidenceINFOPreview={FormatEvidencePreview(evidence)}; " +
                $"conflictCount={InvariantCount(conflicts.Count)}; " +
                $"conflictPreview={FormatConflictPreview(conflicts)}; reason={identity.Reason}",
                formType: "DIAL",
                formId: identity.PrototypeDialFormId,
                code: IdentityEventCode(identity.Kind));
        }
    }

    private static string FormatEvidencePreview(List<uint> evidence) => evidence.Count == 0
        ? "none"
        : string.Join(",", evidence
            .Take(IdentityEvidencePreviewLimit)
            .Select(static formId => $"0x{formId:X8}"));

    private static string FormatConflictPreview(List<string> conflicts) => conflicts.Count == 0
        ? "none"
        : string.Join(" | ", conflicts
            .Take(IdentityConflictPreviewLimit)
            .Select(static conflict => TruncateIdentityConflict(conflict)));

    private static string TruncateIdentityConflict(string conflict) =>
        conflict.Length <= IdentityConflictTextLimit
            ? conflict
            : conflict[..(IdentityConflictTextLimit - 3)] + "...";

    private static string InvariantCount(int value) =>
        value.ToString("N0", CultureInfo.InvariantCulture);

    private static string IdentityEventCode(DialogueTopicIdentityKind kind) => kind switch
    {
        DialogueTopicIdentityKind.MasterAnchor => "dialogue.identity.master-anchor",
        DialogueTopicIdentityKind.SharedChildAnchor => "dialogue.identity.shared-child-anchor",
        DialogueTopicIdentityKind.PrototypeDistinct => "dialogue.identity.prototype-distinct",
        DialogueTopicIdentityKind.Ambiguous => "dialogue.identity.ambiguous",
        _ => "dialogue.identity.unknown",
    };

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

