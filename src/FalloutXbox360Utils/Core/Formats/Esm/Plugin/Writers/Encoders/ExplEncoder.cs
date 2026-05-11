using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes an <see cref="ExplosionRecord" /> (EXPL) as PC-format subrecord bytes.
///     New-record-only path: override emission is a no-op (master ESM bytes retained verbatim).
///     fopdoc canonical order: EDID, OBND?, FULL?, MODL?, EITM?, DATA.
///     DATA layout (52 bytes per PDB BGSExplosionData):
///         float Force(0) + float Damage(4) + float Radius(8) + FormID Light(12) +
///         FormID Sound1(16) + uint32 Flags(20) + float IsRadius(24) + FormID ImpactDataSet(28) +
///         FormID Sound2(32) + float RadiationLevel(36) + float RadiationDissipationTime(40) +
///         float RadiationRadius(44) + uint32 SoundLevel(48).
/// </summary>
public sealed class ExplEncoder : IRecordEncoder
{
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

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(expl)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(ExplosionRecord expl)
    {
        var data = new byte[52];
        SubrecordEncoder.WriteFloat(data, 0, expl.Force);
        SubrecordEncoder.WriteFloat(data, 4, expl.Damage);
        SubrecordEncoder.WriteFloat(data, 8, expl.Radius);
        SubrecordEncoder.WriteFormId(data, 12, expl.Light);
        SubrecordEncoder.WriteFormId(data, 16, expl.Sound1);
        SubrecordEncoder.WriteUInt32(data, 20, expl.Flags);
        SubrecordEncoder.WriteFloat(data, 24, expl.IsRadius);
        SubrecordEncoder.WriteFormId(data, 28, expl.ImpactDataSet);
        SubrecordEncoder.WriteFormId(data, 32, expl.Sound2);
        SubrecordEncoder.WriteFloat(data, 36, expl.RadiationLevel);
        SubrecordEncoder.WriteFloat(data, 40, expl.RadiationDissipationTime);
        SubrecordEncoder.WriteFloat(data, 44, expl.RadiationRadius);
        SubrecordEncoder.WriteUInt32(data, 48, expl.SoundLevel);
        return data;
    }
}
