using FalloutXbox360Utils.Core.Formats.Esm.Conversion.Schema;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="WaterRecord" /> (WATR) as PC-format subrecord bytes.
///     Water has a mix of typed fields (NNAM/ANAM/FNAM/SNAM/DATA) and schema-parsed dictionaries
///     (DNAM 196B for visual properties, GNAM 12B for related-water FormID triple).
///     fopdoc canonical order:
///         EDID, FULL?, NNAM?(noise texture), ANAM(1B opacity), FNAM(byte array flags),
///         SNAM?(sound FormID), DATA(2B damage), DNAM(196B visuals), GNAM(12B related).
/// </summary>
public sealed class WatrEncoder : IRecordEncoder
{
    public string RecordType => "WATR";
    public Type ModelType => typeof(WaterRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(WaterRecord watr)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(watr.EditorId))
        {
            warnings.Add($"New WATR 0x{watr.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", watr.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(watr.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", watr.FullName));
        }

        if (!string.IsNullOrEmpty(watr.NoiseTexture))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("NNAM", watr.NoiseTexture));
        }

        subs.Add(NewRecordSubrecords.EncodeByteSubrecord("ANAM", watr.Opacity));

        if (watr.WaterFlags is { Length: > 0 } flags)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("FNAM", flags));
        }

        if (watr.SoundFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("SNAM", watr.SoundFormId.Value));
        }

        var dataBytes = new byte[2];
        SubrecordEncoder.WriteUInt16(dataBytes, 0, watr.Damage);
        subs.Add(new EncodedSubrecord("DATA", dataBytes));

        if (watr.VisualProperties is not null)
        {
            var dnamSchema = SubrecordSchemaRegistry.GetSchema("DNAM", "WATR", 196);
            if (dnamSchema is not null)
            {
                subs.Add(new EncodedSubrecord("DNAM",
                    SchemaDictionarySerializer.Serialize(dnamSchema, watr.VisualProperties)));
            }
        }

        if (watr.RelatedWater is not null)
        {
            var gnamSchema = SubrecordSchemaRegistry.GetSchema("GNAM", "WATR", 12);
            if (gnamSchema is not null)
            {
                subs.Add(new EncodedSubrecord("GNAM",
                    SchemaDictionarySerializer.Serialize(gnamSchema, watr.RelatedWater)));
            }
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
