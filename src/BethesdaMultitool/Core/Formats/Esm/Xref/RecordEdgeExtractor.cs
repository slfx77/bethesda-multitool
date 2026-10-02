using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Conversion.Schema;
using BethesdaMultitool.Core.Formats.Esm.RecordModel;
using BethesdaMultitool.Core.Formats.Esm.RecordModel.Decoding;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Xref;

internal sealed record RecordReferenceField(uint Target, string Subrecord, int Ordinal, int Occurrence,
    string Field, int FieldOffset, int PayloadOffset, string Kind, bool Certain, bool? OppositeEnableState = null);

internal static class RecordEdgeExtractor
{
    internal static IReadOnlyList<RecordReferenceField> Extract(string signature, byte[] payload, int size,
        bool bigEndian, BethesdaGame game, ushort? formVersion, ReferenceCoverage coverage)
    {
        var descriptors = EsmSubrecordUtils.IterateSubrecords(payload, size, bigEndian).ToList();
        var raw = descriptors.Select(s => new RawSubrecord(s.Signature,
            payload.AsSpan(s.DataOffset, s.DataLength).ToArray())).ToList();
        var observations = new List<SchemaFormIdObservation>();
        var schemaIndex = EsmSchemas.IndexForGame(game);
        if (schemaIndex != null && schemaIndex.TryGetValue(signature, out var schema))
        {
            var nodes = SchemaRecordDecoder.Decode(schema, raw, bigEndian, game: game,
                formVersion: formVersion, observeFormId: observations.Add);
            CollectRaw(nodes, signature, coverage);
        }
        else { coverage.UnmodeledRecordTypes.Add(signature); }

        var output = new List<RecordReferenceField>();
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var ordinal = 0; ordinal < raw.Count; ordinal++)
        {
            var sub = raw[ordinal];
            var occurrence = occurrences.GetValueOrDefault(sub.Signature);
            occurrences[sub.Signature] = occurrence + 1;
            var fields = observations.Where(o => o.SubrecordOrdinal == ordinal).ToList();

            // PLDT/PTDT use discriminated unions, not arbitrary FormIDs. The PLDT type is a byte
            // followed by padding, unlike PTDT's container-endian integer. Never use a fallback arm.
            if (signature == "PACK" && sub.Signature is "PLDT" or "PLD2" or "PTDT" or "PTD2")
            {
                fields.Clear();
                if (sub.Data.Length >= 8)
                {
                    var location = sub.Signature is "PLDT" or "PLD2";
                    var type = location ? sub.Data[0] : ReadUInt32(sub.Data, 0, bigEndian);
                    if (location ? type is 0 or 1 or 4 : type is 0 or 1)
                    {
                        var target = ReadUInt32(sub.Data, 4, bigEndian);
                        if (target != 0) { fields.Add(new(sub.Signature, ordinal, "Union", 4, target, SchemaFormIdOrigin.TypedField)); }
                    }
                }
            }
            else if (signature is "REFR" or "ACHR" or "ACRE" or "PGRE" or "PMIS" or "PBEA")
            {
                // The conversion schema carries explicit FormID fields in placed records. UInt32
                // fields are never reinterpreted as references merely because their value looks like one.
                foreach (var field in SubrecordSchemaReader.EnumerateFormIdFields(sub.Signature, sub.Data, signature, bigEndian))
                {
                    if (!fields.Any(f => f.FieldOffset == field.Offset && f.FormId == field.FormId))
                    { fields.Add(new(sub.Signature, ordinal, field.Field, field.Offset, field.FormId, SchemaFormIdOrigin.TypedField)); }
                }
            }

            // SCRO is an explicit FormID reference-table slot in both standalone and embedded scripts.
            // SCRV contains a local-variable index and intentionally does not enter this path.
            if (sub.Signature == "SCRO" && sub.Data.Length == 4 && !fields.Any(f => f.FieldOffset == 0))
            {
                var target = ReadUInt32(sub.Data, 0, bigEndian);
                if (target != 0) { fields.Add(new(sub.Signature, ordinal, "Script reference table", 0, target, SchemaFormIdOrigin.TypedField)); }
            }

            foreach (var field in fields.DistinctBy(f => (f.FieldOffset, f.FormId, f.Origin)))
            {
                output.Add(new(field.FormId, sub.Signature, ordinal, occurrence, field.Field, field.FieldOffset,
                    descriptors[ordinal].DataOffset + field.FieldOffset, Classify(signature, sub.Signature),
                    field.Origin == SchemaFormIdOrigin.TypedField,
                    sub.Signature == "XESP" && sub.Data.Length >= 8 ? (ReadUInt32(sub.Data, 4, bigEndian) & 1) != 0 : null));
            }
        }
        return output;
    }

    private static uint ReadUInt32(byte[] data, int offset, bool bigEndian) => bigEndian
        ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
        : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));

    private static string Classify(string owner, string subrecord) => subrecord switch
    {
        "PKID" => "ai-package", "XESP" => "enable-parent", "XLKR" => "linked-reference",
        "SCRO" => "script-reference", "SCRI" => "attached-script", "CTDA" => "condition",
        "NAME" when owner is "REFR" or "ACHR" or "ACRE" => "placement-base",
        "CNTO" => "inventory", "LVLO" => "leveled-list", "TCLT" => "dialogue-link",
        "TNAM" when owner == "TERM" => "terminal-submenu",
        "PLDT" or "PLD2" => "package-location", "PTDT" or "PTD2" => "package-target",
        "TPIC" => "dialogue-topic", "QSTI" => "quest", _ => "schema-formid"
    };

    private static void CollectRaw(IEnumerable<DecodedNode> nodes, string record, ReferenceCoverage coverage)
    {
        foreach (var node in nodes)
        {
            if (node.IsRaw) { coverage.RawSubrecords.Add($"{record}.{node.Signature ?? node.Label}"); }
            CollectRaw(node.Children, record, coverage);
        }
    }
}
