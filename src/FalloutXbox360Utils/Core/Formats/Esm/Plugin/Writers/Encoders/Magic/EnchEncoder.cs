using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

/// <summary>
///     Encodes an <see cref="EnchantmentRecord" /> (ENCH) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, FULL?, ENIT(16B), (EFID + EFIT)*.
///     ENIT layout (16B): uint32 Type(0) + uint32 ChargeAmount(4) + uint32 EnchantCost(8) +
///     uint8 Flags(12) + pad(3).
///     EFID is the 4-byte base-effect FormID; EFIT is the 20-byte effect-item block.
/// </summary>
public sealed class EnchEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<EnchantmentRecord, object?>> EnitExtractors = new(StringComparer.Ordinal)
    {
        ["Type"] = m => m.EnchantType,
        ["ChargeAmount"] = m => m.ChargeAmount,
        ["EnchantCost"] = m => m.EnchantCost,
        ["Flags"] = m => m.Flags,
    };

    private static readonly Dictionary<string, Func<EnchantmentEffect, object?>> EfitExtractors = new(StringComparer.Ordinal)
    {
        ["Magnitude"] = m => m.Magnitude,
        ["Area"] = m => m.Area,
        ["Duration"] = m => m.Duration,
        ["Type"] = m => m.Type,
        ["ActorValue"] = m => m.ActorValue,
    };

    public string RecordType => "ENCH";
    public Type ModelType => typeof(EnchantmentRecord);

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

        subs.Add(SchemaModelSerializer.SerializeSubrecord("ENIT", "ENCH", 16, ench, EnitExtractors));

        foreach (var effect in ench.Effects)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EFID", effect.EffectFormId));
            subs.Add(new EncodedSubrecord("EFIT", BuildEfitSubrecord(effect)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Shared EFIT (20B) builder reused by SpelEncoder and AlchEncoder.
    /// </summary>
    internal static byte[] BuildEfitSubrecord(EnchantmentEffect effect)
    {
        return SchemaModelSerializer.Serialize("EFIT", "", 20, effect, EfitExtractors);
    }
}
