using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="EnchantmentRecord" /> (ENCH) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, FULL?, ENIT(16B), (EFID + EFIT)*.
///     ENIT layout (16B): uint32 Type(0) + uint32 ChargeAmount(4) + uint32 EnchantCost(8) +
///     uint8 Flags(12) + pad(3).
///     EFID is the 4-byte base-effect FormID; EFIT is the 20-byte effect-item block.
/// </summary>
public sealed class EnchEncoder : IRecordEncoder
{
    public string RecordType => "ENCH";
    public Type ModelType => typeof(EnchantmentRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(EnchantmentRecord ench)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(ench.EditorId))
        {
            warnings.Add($"New ENCH 0x{ench.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", ench.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(ench.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", ench.FullName));
        }

        subs.Add(new EncodedSubrecord("ENIT", BuildEnitSubrecord(ench)));

        foreach (var effect in ench.Effects)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EFID", effect.EffectFormId));
            subs.Add(new EncodedSubrecord("EFIT", BuildEfitSubrecord(effect)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildEnitSubrecord(EnchantmentRecord ench)
    {
        var data = new byte[16];
        SubrecordEncoder.WriteUInt32(data, 0, ench.EnchantType);
        SubrecordEncoder.WriteUInt32(data, 4, ench.ChargeAmount);
        SubrecordEncoder.WriteUInt32(data, 8, ench.EnchantCost);
        data[12] = ench.Flags;
        // bytes 13-15 padding
        return data;
    }

    /// <summary>
    ///     Shared EFIT (20B) builder reused by SpelEncoder.
    /// </summary>
    internal static byte[] BuildEfitSubrecord(EnchantmentEffect effect)
    {
        var data = new byte[20];
        SubrecordEncoder.WriteFloat(data, 0, effect.Magnitude);
        SubrecordEncoder.WriteUInt32(data, 4, effect.Area);
        SubrecordEncoder.WriteUInt32(data, 8, effect.Duration);
        SubrecordEncoder.WriteUInt32(data, 12, effect.Type);
        SubrecordEncoder.WriteInt32(data, 16, effect.ActorValue);
        return data;
    }
}
