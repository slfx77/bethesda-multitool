using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

/// <summary>
///     Encodes a <see cref="SpellRecord" /> (SPEL) as PC-format subrecord bytes.
///     fopdoc canonical order: EDID, FULL?, SPIT(16B), (EFID + EFIT)*.
///     SPIT layout (16B): uint32 Type(0) + uint32 Cost(4) + uint32 Level(8) +
///     uint8 Flags(12) + pad(3).
///     EFID/EFIT pairs reuse the shared <see cref="EnchEncoder.BuildEfitSubrecord" /> helper.
/// </summary>
public sealed class SpelEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<SpellRecord, object?>> SpitExtractors = new(StringComparer.Ordinal)
    {
        ["Type"] = m => (uint)m.Type,
        ["Cost"] = m => m.Cost,
        ["Level"] = m => m.Level,
        ["Flags"] = m => m.Flags,
    };

    public string RecordType => "SPEL";
    public Type ModelType => typeof(SpellRecord);

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

        subs.Add(SchemaModelSerializer.SerializeSubrecord("SPIT", "SPEL", 16, spel, SpitExtractors));

        foreach (var effect in spel.Effects)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EFID", effect.EffectFormId));
            subs.Add(new EncodedSubrecord("EFIT", EnchEncoder.BuildEfitSubrecord(effect)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
