using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Magic;
using BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

/// <summary>
///     Encodes an <see cref="EnchantmentRecord" /> (ENCH) as PC-format subrecord bytes.
///     FNV xEdit order: EDID, FULL?, ENIT(16B), (EFID + EFIT + CTDA*)*.
///     ENIT bytes 4..11 are labeled unused by FNV xEdit; the historical model fields retain
///     and re-emit those words rather than silently discarding captured bytes.
///     EFID is the 4-byte base-effect FormID; EFIT is the 20-byte effect-item block.
/// </summary>
public sealed class EnchEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<EnchantmentRecord, object?>> EnitExtractors =
        new(StringComparer.Ordinal)
        {
            ["Type"] = m => m.EnchantType,
            ["ChargeAmount"] = m => m.ChargeAmount,
            ["EnchantCost"] = m => m.EnchantCost,
            ["Flags"] = m => m.Flags
        };

    private static readonly Dictionary<string, Func<EnchantmentEffect, object?>> EfitExtractors =
        new(StringComparer.Ordinal)
        {
            ["Magnitude"] = m => m.Magnitude,
            ["Area"] = m => m.Area,
            ["Duration"] = m => m.Duration,
            ["Type"] = m => m.Type,
            ["ActorValue"] = m => m.ActorValue
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

        AppendEffectSubrecords(subs, ench.Effects);

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Shared EFIT (20B) builder reused by SpelEncoder and AlchEncoder.
    /// </summary>
    internal static byte[] BuildEfitSubrecord(EnchantmentEffect effect)
    {
        return SchemaModelSerializer.Serialize("EFIT", "", 20, effect, EfitExtractors);
    }

    /// <summary>
    ///     Appends FNV effect groups in physical order. FNV CTDAs are 28 bytes and have no
    ///     CIS1/CIS2 siblings or Parameter3 tail, so those modern-only fields are intentionally
    ///     outside this target writer contract.
    /// </summary>
    internal static void AppendEffectSubrecords(
        List<EncodedSubrecord> subrecords,
        IReadOnlyList<EnchantmentEffect> effects)
    {
        foreach (var effect in effects)
        {
            subrecords.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EFID", effect.EffectFormId));
            subrecords.Add(new EncodedSubrecord("EFIT", BuildEfitSubrecord(effect)));
            foreach (var condition in effect.Conditions)
            {
                subrecords.Add(new EncodedSubrecord("CTDA", InfoEncoder.BuildCtdaSubrecord(condition)));
            }
        }
    }
}
