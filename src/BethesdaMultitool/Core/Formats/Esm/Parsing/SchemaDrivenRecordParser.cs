using System.Buffers;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Dialogue;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Schema;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing;

/// <summary>
///     A game-agnostic record parser driven entirely by a generated <see cref="RecordDef" /> schema
///     (<see cref="RecordModel.EsmSchemas" />). It decodes <em>every</em> top-level record of a plugin into
///     a <see cref="GenericEsmRecord" /> carrying an ordered, labeled <see cref="DecodedNode" /> tree, so a
///     game whose layouts diverge from the hand-written FNV handlers (Oblivion today; Skyrim/FO4/FO76
///     next) is read correctly without a bespoke typed handler per type. Mirrors the role of
///     <see cref="Tes3.Tes3RecordParser" /> for the post-TES3 (6-byte subrecord) family.
/// </summary>
internal sealed class SchemaDrivenRecordParser(RecordParserContext context, IReadOnlyList<RecordDef> schema)
{
    private readonly RecordParserContext _context = context;
    private readonly Dictionary<string, RecordDef> _byType = BuildIndex(schema);

    // Typed dialogue, built game-aware from DIAL/INFO so the Dialogue tab works (the shared
    // DialogueTreeBuilder consumes these). Populated as a side effect while decoding records.
    private readonly List<DialogTopicRecord> _topics = [];
    private readonly List<DialogueRecord> _infos = [];

    // INFO FormID -> (parent topic FormID, ordering index within the topic), from the GRUP-based
    // TopicToInfoMap the analyzer already built (structural, game-agnostic).
    private Dictionary<uint, (uint Topic, ushort Index)> _infoLink = [];

    public RecordCollection ParseAll(IProgress<(int percent, string phase)>? progress = null)
    {
        _infoLink = BuildInfoLink(_context.ScanResult.TopicToInfoMap);

        var generic = new List<GenericEsmRecord>();
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var total = _context.ScanResult.MainRecords.Count;
            var done = 0;
            foreach (var record in _context.ScanResult.MainRecords)
            {
                if (!NonBrowsableChildTypes.Contains(record.RecordType))
                {
                    generic.Add(ParseRecord(record, buffer));
                }

                if (++done % 16384 == 0)
                {
                    progress?.Report((total == 0 ? 100 : (int)(done * 100L / total), "Decoding records..."));
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        progress?.Report((100, "Complete"));

        var dialogueTree = new DialogueTreeBuilder(_context).BuildDialogueTrees(_infos, _topics, []);

        return new RecordCollection
        {
            GenericRecords = generic,
            DialogTopics = _topics,
            Dialogues = _infos,
            DialogueTree = dialogueTree,
            FormIdToEditorId = _context.FormIdToEditorId,
            FormIdToDisplayName = _context.FormIdToFullName,
            TotalRecordsProcessed = generic.Count
            // UnparsedTypeCounts left empty: browsable records are decoded; placement children are skipped.
        };
    }

    /// <summary>Inverts the topic-&gt;infos GRUP map into info-&gt;(topic, order-index) for INFO linkage.</summary>
    private static Dictionary<uint, (uint Topic, ushort Index)> BuildInfoLink(
        Dictionary<uint, List<uint>> topicToInfo)
    {
        var link = new Dictionary<uint, (uint, ushort)>();
        foreach (var (topic, infos) in topicToInfo)
        {
            for (var i = 0; i < infos.Count; i++)
            {
                link[infos[i]] = (topic, (ushort)i);
            }
        }

        return link;
    }

    /// <summary>
    ///     Per-cell child / placement record types: extremely high volume (REFR alone is ~90% of an
    ///     Oblivion plugin — ~1.07M of 1.16M records) and not browsable base definitions. The Records tab
    ///     lists base records, not the millions of placed instances, so decoding every one into a field
    ///     tree is pure waste (the bulk of parse time and retained memory). Skipping them keeps the read
    ///     fast and the browser usable; placement/world structure is out of scope for the schema read path.
    /// </summary>
    private static readonly HashSet<string> NonBrowsableChildTypes = new(StringComparer.Ordinal)
    {
        "REFR", "ACHR", "ACRE", "PGRD", "PGRE", "PMIS", "LAND", "NAVM", "ROAD",
        "PARW", "PBAR", "PBEA", "PCON", "PFLA", "PHZD", "PWAT"
    };

    private GenericEsmRecord ParseRecord(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new GenericEsmRecord
            {
                FormId = record.FormId,
                RecordType = record.RecordType,
                EditorId = _context.GetEditorId(record.FormId),
                FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        ObjectBounds? bounds = null;
        var subrecords = new List<RawSubrecord>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        _context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "FULL":
                    fullName = _context.ReadFullName(subData);
                    break;
                case "MODL" when modelPath == null:
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
            }

            subrecords.Add(new RawSubrecord(sub.Signature, subData.ToArray()));
        }

        IReadOnlyList<DecodedNode>? tree = null;
        if (_byType.TryGetValue(record.RecordType, out var def))
        {
            tree = SchemaRecordDecoder.Decode(def, subrecords, record.IsBigEndian);
        }

        if (!string.IsNullOrEmpty(fullName))
        {
            _context.FormIdToFullName.TryAdd(record.FormId, fullName);
        }

        ExtractDialogue(record, editorId, subrecords);

        return new GenericEsmRecord
        {
            FormId = record.FormId,
            RecordType = record.RecordType,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            DecodedTree = tree,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Builds the typed dialogue model for DIAL/INFO records (game-aware) alongside their generic
    ///     decode, so the Dialogue tab has data. No-op for every other record type.
    /// </summary>
    private void ExtractDialogue(DetectedMainRecord record, string? editorId, IReadOnlyList<RawSubrecord> subrecords)
    {
        switch (record.RecordType)
        {
            case "DIAL":
                _topics.Add(OblivionDialogueExtractor.BuildTopic(record.FormId, editorId, subrecords));
                break;
            case "INFO":
                var link = _infoLink.TryGetValue(record.FormId, out var l) ? l : default;
                _infos.Add(OblivionDialogueExtractor.BuildInfo(
                    record.FormId, editorId, link.Topic == 0 ? null : link.Topic, link.Index, subrecords));
                break;
        }
    }

    private static Dictionary<string, RecordDef> BuildIndex(IReadOnlyList<RecordDef> schema)
    {
        var map = new Dictionary<string, RecordDef>(StringComparer.Ordinal);
        foreach (var record in schema)
        {
            map[record.Signature] = record;
        }

        return map;
    }
}
