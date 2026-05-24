using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Magic;

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
    // Schema field names diverge from model field names in several places — most notably
    // "Archtype" (schema spelling) vs Archetype (model). Extractor map keys must match the
    // schema verbatim; SchemaModelSerializer looks them up by exact string.
    private static readonly Dictionary<string, Func<BaseEffectRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Flags"] = m => m.Flags,
        ["BaseCost"] = m => m.BaseCost,
        ["AssocItem"] = m => m.AssociatedItem,
        ["MagicSchool"] = m => m.MagicSchool,
        ["ResistanceValue"] = m => m.ResistValue,
        // "Unknown" (uint16) and padding not in model → zero-fill.
        ["Light"] = m => m.LightFormId ?? 0u,
        ["ProjectileSpeed"] = m => m.ProjectileSpeed ?? 0f,
        ["EffectShader"] = m => m.EffectShaderFormId ?? 0u,
        ["EnchantEffect"] = m => m.EnchantEffectFormId ?? 0u,
        ["CastingSound"] = m => m.CastingSoundFormId ?? 0u,
        ["BoltSound"] = m => m.BoltSoundFormId ?? 0u,
        ["HitSound"] = m => m.HitSoundFormId ?? 0u,
        ["AreaSound"] = m => m.AreaSoundFormId ?? 0u,
        ["ConstantEffectEnchantmentFactor"] = m => m.CEEnchantFactor ?? 0f,
        ["ConstantEffectBarterFactor"] = m => m.CEBarterFactor ?? 0f,
        ["Archtype"] = m => (int)m.Archetype, // schema typo preserved
        ["ActorValue"] = m => m.ActorValue,
    };

    public string RecordType => "MGEF";
    public Type ModelType => typeof(BaseEffectRecord);

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

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "MGEF", 72, mgef, DataExtractors));

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }
}
