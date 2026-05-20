using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
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
///     Cross-record FormID references on each INFO (QSTI, ANAM, PNAM, NAME, TCLT, TCLF,
///     CTDA.Reference) are validated against the master-FormIDs ∪ emitted-new-FormIDs set;
///     unresolvable references are dropped to prevent runtime null-derefs and the engine's
///     "fallback global broadcast" behavior (which manifested as every NPC playing the
///     crucified idle animation every few seconds).
/// </summary>
internal sealed record DialogSectionResult(byte[] DialogSection, byte[]? PlaceholderQustRecord);

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
        IEnumerable<uint>? additionalValidFormIds = null)
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
        if (additionalValidFormIds is not null)
        {
            foreach (var fid in additionalValidFormIds)
            {
                validFormIds.Add(fid);
            }
        }

        using var stream = new MemoryStream();
        var topLabel = "DIAL"u8.ToArray();
        var topPos = WriteGrupHeader(stream, topLabel, 0);

        var emittedDials = 0;
        var emittedMasterDialAnchors = 0;
        var emittedInfos = 0;
        var sanitizedFieldCount = 0;
        var droppedConditions = 0;
        var remappedCtdaParameters = 0;
        foreach (var topic in newTopics)
        {
            var newDialId = dialFormIdMap[topic.FormId];
            var dialEncoded = DialEncoder.EncodeNew(topic);
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
                var newInfoId = allocator.Allocate();
                var patched = SanitizeInfoReferences(info, newInfoId, newDialId, validFormIds,
                    remapTable, ref sanitizedFieldCount, ref droppedConditions,
                    ref remappedCtdaParameters);
                var infoEncoded = InfoEncoder.EncodeNew(patched);
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
            }

            RecordHeaderProcessor.FinalizeGrupSize(stream, childrenPos);
        }

        foreach (var (masterDialId, infosForDial) in infosByMasterDial.OrderBy(kv => kv.Key))
        {
            if (infosForDial.Count == 0 ||
                !masterRecordsByFormId.TryGetValue(masterDialId, out var masterDialRecord) ||
                masterDialRecord.Header.Signature != "DIAL")
            {
                droppedUnavailableMasterDialInfos += infosForDial.Count;
                continue;
            }

            stream.Write(CellGrupBuilder.ReconstructRecordBytes(masterDialRecord));
            stats.IncrementEmitted("DIAL");
            stats.OverridesEmitted++;
            emittedMasterDialAnchors++;

            var childLabel = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(childLabel, masterDialId);
            var childrenPos = WriteGrupHeader(stream, childLabel, 7);

            foreach (var info in infosForDial)
            {
                var newInfoId = allocator.Allocate();
                var patched = SanitizeInfoReferences(info, newInfoId, masterDialId, validFormIds,
                    remapTable, ref sanitizedFieldCount, ref droppedConditions,
                    ref remappedCtdaParameters);
                var infoEncoded = InfoEncoder.EncodeNew(patched);
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
            $"Dropped {droppedUnavailableMasterDialInfos:N0} INFO(s) with unavailable master-DIAL TPIC and " +
            $"{droppedOrphanInfos:N0} orphan INFO(s). Sanitized {sanitizedFieldCount:N0} unresolvable " +
            $"cross-record FormID reference(s); dropped {droppedConditions:N0} CTDA condition(s) " +
            $"with dangling Reference or Parameter FormIDs; remapped {remappedCtdaParameters:N0} " +
            "CTDA Parameter1/Parameter2 FormID(s) via the runtime→emitted alias table.",
            code: "dialog.emitted");

        return new DialogSectionResult(stream.ToArray(), null);
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
        static uint? KeepIfValid(uint? id, HashSet<uint> valid, ref int counter)
        {
            if (!id.HasValue || id.Value == 0)
            {
                return id;
            }

            if (valid.Contains(id.Value))
            {
                return id;
            }

            counter++;
            return null;
        }

        var quest = KeepIfValid(info.QuestFormId, validFormIds, ref sanitizedFieldCount);
        var speaker = KeepIfValid(info.SpeakerFormId, validFormIds, ref sanitizedFieldCount);
        var prevInfo = KeepIfValid(info.PreviousInfo, validFormIds, ref sanitizedFieldCount);

        var addTopics = FilterValid(info.AddTopics, validFormIds, ref sanitizedFieldCount);
        var linkTo = FilterValid(info.LinkToTopics, validFormIds, ref sanitizedFieldCount);
        var linkFrom = FilterValid(info.LinkFromTopics, validFormIds, ref sanitizedFieldCount);

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
            Conditions = conditions
        };
    }

    private static List<uint> FilterValid(List<uint> ids, HashSet<uint> valid, ref int counter)
    {
        var kept = new List<uint>(ids.Count);
        foreach (var id in ids)
        {
            if (id == 0 || valid.Contains(id))
            {
                kept.Add(id);
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
