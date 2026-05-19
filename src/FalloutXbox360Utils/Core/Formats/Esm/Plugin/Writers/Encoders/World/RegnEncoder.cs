using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.World;

/// <summary>
///     Encodes a <see cref="RegionRecord" /> (REGN) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, ICON?, RCLR(4B emittance RGBA), WNAM(4B worldspace FormID),
///     [RPLI?+RPLD?]*(boundary polygons), [RDAT + per-type data]*(region data tuples).
///     RDAT blocks + their typed payloads (RDOT/RDMP/RDGS/RDMD/RDSD/RDWT) and boundary
///     polygon subrecords (RPLI/RPLD) are captured by the parser as opaque-bytes payload
///     lists and emitted verbatim here — no per-type schema work, full round-trip.
///     Override path is a no-op; master ESM bytes retained verbatim.
/// </summary>
public sealed class RegnEncoder : IRecordEncoder
{
    public string RecordType => "REGN";
    public Type ModelType => typeof(RegionRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(RegionRecord regn)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(regn.EditorId))
        {
            warnings.Add($"New REGN 0x{regn.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", regn.EditorId ?? string.Empty));

        // RCLR: 4 bytes RGBA emittance color. Model has R/G/B; A defaults to 255.
        var rclr = new byte[4];
        rclr[0] = regn.EmittanceColorR;
        rclr[1] = regn.EmittanceColorG;
        rclr[2] = regn.EmittanceColorB;
        rclr[3] = 255;
        subs.Add(new EncodedSubrecord("RCLR", rclr));

        if (regn.WorldspaceFormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("WNAM", regn.WorldspaceFormId));
        }

        // RDAT region-data tuples. The parser captured each block's 8-byte RDAT header
        // (Type uint32 + Flags uint32) plus the typed payload subrecord(s) that followed
        // it. Emit RDAT + payload-by-signature, preserving stream order.
        foreach (var block in regn.DataBlocks)
        {
            var rdat = new byte[8];
            SubrecordEncoder.WriteUInt32(rdat, 0, block.Type);
            SubrecordEncoder.WriteUInt32(rdat, 4, block.Flags);
            subs.Add(new EncodedSubrecord("RDAT", rdat));

            foreach (var payload in block.Payload)
            {
                subs.Add(new EncodedSubrecord(payload.Signature, payload.Bytes));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
