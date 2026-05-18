using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Merge;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;
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
///     at master DIALs are dropped — nesting them requires emitting an override DIAL anchor
///     record under the master ID, which this builder doesn't yet do. INFOs with a dangling
///     or zero TPIC are dropped because the runtime null-derefs on dialog-tree walks.
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
        ConversionPipelineStats stats,
        IConversionProgressSink sink)
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
        // a master DIAL or at nothing are dropped — emitting them under a master-DIAL child
        // GRUP requires an override DIAL anchor this builder doesn't yet emit, and a
        // dangling TPIC null-derefs the dialog tree walker.
        var infosByEmittedDial = new Dictionary<uint, List<DialogueRecord>>();
        var droppedMasterDialInfos = 0;
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
                droppedMasterDialInfos++;
            }
            else
            {
                droppedOrphanInfos++;
            }
        }

        using var stream = new MemoryStream();
        var topLabel = "DIAL"u8.ToArray();
        var topPos = WriteGrupHeader(stream, topLabel, 0);

        var emittedDials = 0;
        var emittedInfos = 0;
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
                // Patch TPIC to the emitted DIAL FormID so the runtime walks the topic tree correctly.
                var patched = info with { FormId = newInfoId, TopicFormId = newDialId };
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

        if (emittedDials == 0)
        {
            // No DIALs survived — roll back the empty top-level GRUP header.
            stream.SetLength(topPos);
            sink.Info("Building dialog section",
                $"No new DIAL records survived encoding. Dropped {droppedMasterDialInfos:N0} INFO(s) " +
                $"with master-DIAL TPIC and {droppedOrphanInfos:N0} orphan INFO(s).",
                code: "dialog.empty");
            return new DialogSectionResult([], null);
        }

        RecordHeaderProcessor.FinalizeGrupSize(stream, topPos);

        sink.Info("Building dialog section",
            $"Emitted {emittedDials:N0} new DIAL + {emittedInfos:N0} new INFO record(s). " +
            $"Dropped {droppedMasterDialInfos:N0} INFO(s) with master-DIAL TPIC and " +
            $"{droppedOrphanInfos:N0} orphan INFO(s).",
            code: "dialog.emitted");

        return new DialogSectionResult(stream.ToArray(), null);
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
