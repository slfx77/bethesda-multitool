using System.Buffers;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
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

    public RecordCollection ParseAll(IProgress<(int percent, string phase)>? progress = null)
    {
        var generic = new List<GenericEsmRecord>(_context.ScanResult.MainRecords.Count);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            var total = _context.ScanResult.MainRecords.Count;
            var done = 0;
            foreach (var record in _context.ScanResult.MainRecords)
            {
                generic.Add(ParseRecord(record, buffer));

                if (++done % 8192 == 0)
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

        return new RecordCollection
        {
            GenericRecords = generic,
            FormIdToEditorId = _context.FormIdToEditorId,
            FormIdToDisplayName = _context.FormIdToFullName,
            TotalRecordsProcessed = _context.ScanResult.MainRecords.Count
            // UnparsedTypeCounts left empty: every record is decoded (schema tree or identity-only).
        };
    }

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
