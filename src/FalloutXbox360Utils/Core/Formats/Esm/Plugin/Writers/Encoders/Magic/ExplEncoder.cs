using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

/// <summary>
///     Encodes an <see cref="ExplosionRecord" /> (EXPL) as PC-format subrecord bytes.
///     New-record-only path: override emission is a no-op (master ESM bytes retained verbatim).
///     fopdoc canonical order: EDID, OBND?, FULL?, MODL?, EITM?, DATA.
///     DATA layout (52 bytes per PDB BGSExplosionData):
///     float Force(0) + float Damage(4) + float Radius(8) + FormID Light(12) +
///     FormID Sound1(16) + uint32 Flags(20) + float IsRadius(24) + FormID ImpactDataSet(28) +
///     FormID Sound2(32) + float RadiationLevel(36) + float RadiationDissipationTime(40) +
///     float RadiationRadius(44) + uint32 SoundLevel(48).
/// </summary>
public sealed class ExplEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<ExplosionRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Force"] = m => m.Force,
        ["Damage"] = m => m.Damage,
        ["Radius"] = m => m.Radius,
        ["Light"] = m => m.Light,
        ["Sound1"] = m => m.Sound1,
        ["Flags"] = m => m.Flags,
        ["IsRadius"] = m => m.IsRadius,
        ["ImpactDataSet"] = m => m.ImpactDataSet,
        ["Sound2"] = m => m.Sound2,
        ["RadiationLevel"] = m => m.RadiationLevel,
        ["RadiationDissipationTime"] = m => m.RadiationDissipationTime,
        ["RadiationRadius"] = m => m.RadiationRadius,
        ["SoundLevel"] = m => m.SoundLevel,
    };

    public string RecordType => "EXPL";
    public Type ModelType => typeof(ExplosionRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(ExplosionRecord expl)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(expl.EditorId))
        {
            warnings.Add($"New EXPL 0x{expl.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", expl.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(expl.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", expl.FullName));
        }

        if (!string.IsNullOrEmpty(expl.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", expl.ModelPath));
        }

        if (expl.Enchantment != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("EITM", expl.Enchantment));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "EXPL", 52, expl, DataExtractors));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
