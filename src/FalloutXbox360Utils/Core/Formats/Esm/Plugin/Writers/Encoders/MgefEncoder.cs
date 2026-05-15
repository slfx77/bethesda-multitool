using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="BaseEffectRecord" /> (MGEF) as PC-format subrecord bytes.
///     Foundation for resolving effect names on enchantments and spells.
///     fopdoc canonical order: EDID, FULL?, DESC?, ICON?, MODL?, DATA(72B).
///     DATA layout (72 bytes per PDB EffectSettingData):
///     uint32 Flags(0) + float BaseCost(4) + FormID AssocItem(8) + int32 MagicSchool(12) +
///     int32 ResistValue(16) + uint16 Unknown(20) + pad(2) + FormID Light(24) +
///     float ProjectileSpeed(28) + FormID EffectShader(32) + FormID EnchantEffect(36) +
///     FormID CastingSound(40) + FormID BoltSound(44) + FormID HitSound(48) +
///     FormID AreaSound(52) + float CEEnchantFactor(56) + float CEBarterFactor(60) +
///     int32 Archetype(64) + int32 ActorValue(68).
/// </summary>
public sealed class MgefEncoder : IRecordEncoder
{
    public string RecordType => "MGEF";
    public Type ModelType => typeof(BaseEffectRecord);

    public EncodedRecord Encode(object model)
    {
        return new EncodedRecord { Subrecords = [], Warnings = [] };
    }

    internal static EncodedRecord EncodeNew(BaseEffectRecord mgef)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(mgef.EditorId))
        {
            warnings.Add($"New MGEF 0x{mgef.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", mgef.EditorId ?? string.Empty));

        if (!string.IsNullOrEmpty(mgef.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", mgef.FullName));
        }

        if (!string.IsNullOrEmpty(mgef.Description))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("DESC", mgef.Description));
        }

        if (!string.IsNullOrEmpty(mgef.Icon))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", mgef.Icon));
        }

        if (!string.IsNullOrEmpty(mgef.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", mgef.ModelPath));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(mgef)));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    private static byte[] BuildDataSubrecord(BaseEffectRecord mgef)
    {
        var data = new byte[72];
        SubrecordEncoder.WriteUInt32(data, 0, mgef.Flags);
        SubrecordEncoder.WriteFloat(data, 4, mgef.BaseCost);
        SubrecordEncoder.WriteFormId(data, 8, mgef.AssociatedItem);
        SubrecordEncoder.WriteInt32(data, 12, mgef.MagicSchool);
        SubrecordEncoder.WriteInt32(data, 16, mgef.ResistValue);
        // bytes 20-21: uint16 Unknown — not in model; leave zero
        // bytes 22-23: padding
        SubrecordEncoder.WriteFormId(data, 24, mgef.LightFormId ?? 0u);
        SubrecordEncoder.WriteFloat(data, 28, mgef.ProjectileSpeed ?? 0f);
        SubrecordEncoder.WriteFormId(data, 32, mgef.EffectShaderFormId ?? 0u);
        SubrecordEncoder.WriteFormId(data, 36, mgef.EnchantEffectFormId ?? 0u);
        SubrecordEncoder.WriteFormId(data, 40, mgef.CastingSoundFormId ?? 0u);
        SubrecordEncoder.WriteFormId(data, 44, mgef.BoltSoundFormId ?? 0u);
        SubrecordEncoder.WriteFormId(data, 48, mgef.HitSoundFormId ?? 0u);
        SubrecordEncoder.WriteFormId(data, 52, mgef.AreaSoundFormId ?? 0u);
        SubrecordEncoder.WriteFloat(data, 56, mgef.CEEnchantFactor ?? 0f);
        SubrecordEncoder.WriteFloat(data, 60, mgef.CEBarterFactor ?? 0f);
        SubrecordEncoder.WriteInt32(data, 64, (int)mgef.Archetype);
        SubrecordEncoder.WriteInt32(data, 68, mgef.ActorValue);
        return data;
    }
}
