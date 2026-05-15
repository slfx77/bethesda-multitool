using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="SpellRecord" /> (SPEL) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, FULL?, SPIT(16B), (EFID + EFIT)*.
///     SPIT layout (16B): uint32 Type(0) + uint32 Cost(4) + uint32 Level(8) +
///     uint8 Flags(12) + pad(3).
///     EFID/EFIT pairs reuse the shared <see cref="EnchEncoder.BuildEfitSubrecord" /> helper.
/// </summary>
public sealed class SpelEncoder : IRecordEncoder
{
    public string RecordType => "SPEL";
    public Type ModelType => typeof(SpellRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(SpellRecord spel)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(spel.EditorId))
        {
            warnings.Add($"New SPEL 0x{spel.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", spel.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(spel.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", spel.FullName));
        }

        subs.Add(new EncodedSubrecord("SPIT", BuildSpitSubrecord(spel)));

        foreach (var effect in spel.Effects)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EFID", effect.EffectFormId));
            subs.Add(new EncodedSubrecord("EFIT", EnchEncoder.BuildEfitSubrecord(effect)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildSpitSubrecord(SpellRecord spel)
    {
        var data = new byte[16];
        SubrecordEncoder.WriteUInt32(data, 0, (uint)spel.Type);
        SubrecordEncoder.WriteUInt32(data, 4, spel.Cost);
        SubrecordEncoder.WriteUInt32(data, 8, spel.Level);
        data[12] = spel.Flags;
        // bytes 13-15 padding
        return data;
    }
}
