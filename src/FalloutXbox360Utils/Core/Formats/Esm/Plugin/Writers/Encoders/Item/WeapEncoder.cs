using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Plugin.Reference;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders.Item;

/// <summary>
///     Encodes a <see cref="WeaponRecord" /> as PC-format WEAP subrecord bytes.
///     Emits DATA (15 bytes) only. DNAM (204 bytes), CRDT, ETYP, NAM0, etc. are retained
///     from the source ESM — those subrecords contain many fields the WeaponRecord model does
///     not enumerate, so reconstructing them risks corrupting unmapped bytes.
///     DATA layout: int32 Value(0) + int32 Health(4) + float Weight(8) + int16 Damage(12) + uint8 ClipSize(14).
/// </summary>
public sealed class WeapEncoder : IRecordEncoder
{
    private static readonly Dictionary<string, Func<WeaponRecord, object?>> DataExtractors = new(StringComparer.Ordinal)
    {
        ["Value"] = m => m.Value,
        ["Health"] = m => m.Health,
        ["Weight"] = m => m.Weight,
        ["Damage"] = m => m.Damage,
        ["ClipSize"] = m => m.ClipSize,
    };

    // CRDT helper mutates weap.CriticalEffectFormId via `with` so the static map sees the
    // resolved FormID. Schema field "CriticalChanceMult" maps to model.CriticalChance.
    private static readonly Dictionary<string, Func<WeaponRecord, object?>> CrdtExtractors = new(StringComparer.Ordinal)
    {
        ["CriticalDamage"] = m => (ushort)m.CriticalDamage,
        ["CriticalChanceMult"] = m => m.CriticalChance,
        // "EffectOnDeath" not in model → zero-fill.
        ["CriticalEffect"] = m => m.CriticalEffectFormId ?? 0u,
    };

    // DNAM payload is 204 bytes long. See the WEAP DNAM entry in SubrecordItemSchemas for
    // field layout. The schema and model use different names for many fields. The caller
    // mutates Projectile / ProjectileFormId on the record before serialization.
    private static readonly Dictionary<string, Func<WeaponRecord, object?>> DnamExtractors = new(StringComparer.Ordinal)
    {
        ["WeaponType"] = m => (sbyte)m.WeaponType,
        ["Speed"] = m => m.Speed,
        ["Reach"] = m => m.Reach,
        ["Flags"] = m => m.Flags,
        ["HandGripAnim"] = m => (byte)m.HandGrip,
        ["AmmoPerShot"] = m => m.AmmoPerShot,
        ["ReloadAnim"] = m => (byte)m.ReloadAnim,
        ["MinSpread"] = m => m.MinSpread,
        ["Spread"] = m => m.Spread,
        ["Drift"] = m => m.Drift,
        ["IronFov"] = m => m.IronSightFov,
        // ConditionLevel + EmbeddedConditionValue not in model → zero-fill.
        ["Projectile"] = m => m.ProjectileFormId ?? 0u,
        ["VatToHitChance"] = m => m.VatsToHitChance,
        ["AttackAnim"] = m => (byte)m.AttackAnim,
        ["NumProjectiles"] = m => m.NumProjectiles,
        ["MinRange"] = m => m.MinRange,
        ["MaxRange"] = m => m.MaxRange,
        ["HitBehavior"] = m => (uint)m.OnHit,
        ["FlagsEx"] = m => m.FlagsEx,
        ["AttackMult"] = m => m.AttackMultiplier,
        ["ShotsPerSec"] = m => m.ShotsPerSec,
        ["ActionPoints"] = m => m.ActionPoints,
        ["RumbleLeftMotor"] = m => m.RumbleLeftMotor,
        ["RumbleRightMotor"] = m => m.RumbleRightMotor,
        ["RumbleDuration"] = m => m.RumbleDuration,
        ["DamageToWeaponMult"] = m => m.DamageToWeaponMult,
        ["AnimShotsPerSecond"] = m => m.AnimShotsPerSecond,
        ["AnimReloadTime"] = m => m.AnimReloadTime,
        ["AnimJamTime"] = m => m.AnimJamTime,
        ["AimArc"] = m => m.AimArc,
        ["Skill"] = m => m.Skill,
        ["RumblePattern"] = m => m.RumblePattern,
        ["RumbleWavelength"] = m => m.RumbleWavelength,
        ["LimbDamageMult"] = m => m.LimbDamageMult,
        ["Resistance"] = m => m.Resistance,
        ["IronSightUseMult"] = m => m.IronSightUseMult,
        ["SemiAutoDelayMin"] = m => m.SemiAutoFireDelayMin,
        ["SemiAutoDelayMax"] = m => m.SemiAutoFireDelayMax,
        ["CookTimer"] = m => m.CookTimer,
        // ModActionOne/Two/Three + ModActionOneValue/Two/Three not in model → zero-fill.
        ["PowerAttackOverrideAnim"] = m => m.PowerAttackOverrideAnim,
        ["StrengthRequirement"] = m => m.StrengthRequirement,
        ["ModReloadClipAnimation"] = m => m.ModReloadClipAnimation,
        ["ModFireAnimation"] = m => m.ModFireAnimation,
        ["AmmoRegenRate"] = m => m.AmmoRegenRate,
        ["KillImpulse"] = m => m.KillImpulse,
        // ModActionOneValueTwo/Two/Three not in model → zero-fill.
        ["KillImpulseDistance"] = m => m.KillImpulseDistance,
        ["SkillRequirement"] = m => m.SkillRequirement,
    };

    private static readonly Dictionary<string, Func<VatsAttackData, object?>> VatsExtractors = new(StringComparer.Ordinal)
    {
        ["VatSpecialEffect"] = m => m.EffectFormId,
        ["VatSpecialAP"] = m => m.ActionPointCost,
        ["VatSpecialMultiplier"] = m => m.DamageMultiplier,
        ["VatSkillRequired"] = m => m.SkillRequired,
        ["Silent"] = m => m.IsSilent ? (byte)1 : (byte)0,
        ["ModRequired"] = m => m.RequiresMod ? (byte)1 : (byte)0,
        ["Flags"] = m => m.ExtraFlags,
    };

    public string RecordType => "WEAP";
    public Type ModelType => typeof(WeaponRecord);

    public EncodedRecord Encode(object model)
    {
        var weap = (WeaponRecord)model;
        return new EncodedRecord
        {
            Subrecords = [SchemaModelSerializer.SerializeSubrecord("DATA", "WEAP", 15, weap, DataExtractors)],
            Warnings = []
        };
    }

    /// <summary>
    ///     Encode a new WEAP record from scratch. FNV master canonical order (confirmed
    ///     against FalloutNV.esm):
    ///     EDID, OBND?, FULL?, MODL?, MODT?, ICON?, MICO?, NAM0?(ammo), REPL?, ETYP?,
    ///     DATA, DNAM, CRDT?, INAM?, [WNAM + WNM1-7/MWD1-7], VATS?. SCRI is deferred — the
    ///     WeaponRecord model lacks a Script field.
    /// </summary>
    /// <remarks>
    ///     DNAM (204 bytes) is emitted with all model-covered fields. Fields the model doesn't
    ///     expose (ModActionOne/Two/Three pairs, EmbeddedConditionValue) are zeroed.
    ///     Padding bytes per the schema are zeroed.
    /// </remarks>
    /// <summary>
    ///     Encode a new WEAP record. Optional FormID-bearing subrecords (NAM0 ammo,
    ///     REPL repair-list, INAM impact-data, CRDT critical-effect, DNAM-embedded projectile)
    ///     are validated against master ∪ emitted. Dangling FormIDs are remapped via the alias
    ///     table or zeroed (for embedded FormIDs in DNAM) / skipped (for standalone subrecords).
    ///     The engine logs "Could not find projectile/ammo" otherwise and the weapon spawns
    ///     nothing or uses the wrong ammo.
    /// </summary>
    internal static EncodedRecord EncodeNew(
        WeaponRecord weap,
        IReadOnlySet<uint>? validFormIds = null,
        IReadOnlyDictionary<uint, uint>? remapTable = null)
    {
        var subs = new List<EncodedSubrecord>();
        var warnings = new List<string>();

        if (string.IsNullOrEmpty(weap.EditorId))
        {
            warnings.Add($"New WEAP 0x{weap.FormId:X8} has no EditorId — emitting empty EDID.");
        }

        subs.Add(NewRecordSubrecords.EncodeStringSubrecord("EDID", weap.EditorId ?? string.Empty));

        // OBND not on WeaponRecord directly — defer. (Bounds is in some other model types.)

        if (!string.IsNullOrEmpty(weap.FullName))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("FULL", weap.FullName));
        }

        if (!string.IsNullOrEmpty(weap.ModelPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MODL", weap.ModelPath));
        }
        else
        {
            warnings.Add($"New WEAP 0x{weap.FormId:X8} has no model path — weapon won't render in-game.");
        }

        if (weap.TextureHashData is { Length: > 0 } modt)
        {
            subs.Add(NewRecordSubrecords.EncodeByteArraySubrecord("MODT", modt));
        }

        if (!string.IsNullOrEmpty(weap.InventoryIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("ICON", weap.InventoryIconPath));
        }

        if (!string.IsNullOrEmpty(weap.MessageIconPath))
        {
            subs.Add(NewRecordSubrecords.EncodeStringSubrecord("MICO", weap.MessageIconPath));
        }

        // FNV master canonical order (confirmed against FalloutNV.esm Weap10mmPistol):
        // NAM0 (ammo FormID) → REPL (repair list) → ETYP (equipment type). FNVEdit treats
        // ENAM as legacy F3/Oblivion and flags it as "unexpected" in FNV WEAP records.
        if (weap.AmmoFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(
                weap.AmmoFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("NAM0", resolved.Value));
            }
            else
            {
                warnings.Add(
                    $"New WEAP 0x{weap.FormId:X8} NAM0 ammo 0x{weap.AmmoFormId.Value:X8} dangles — subrecord skipped.");
            }
        }

        if (weap.RepairItemListFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(
                weap.RepairItemListFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("REPL", resolved.Value));
            }
        }

        // ETYP — 4-byte int32 (enum -1..13). Emit when not None.
        // Despite the schema registering ETYP as a FormID type for endian-swap purposes,
        // FNV's parser reads it as int32 — see WeaponRecordHandler.cs:313-320.
        if (weap.EquipmentType != EquipmentType.None)
        {
            subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("ETYP", (int)weap.EquipmentType));
        }

        subs.Add(SchemaModelSerializer.SerializeSubrecord("DATA", "WEAP", 15, weap, DataExtractors));
        // DNAM embeds the projectile FormID at offset 36. When dangling, zero it (engine
        // logs "Could not find projectile" but the weapon still loads — just fires nothing).
        subs.Add(new EncodedSubrecord("DNAM",
            BuildDnamSubrecord(weap, ResolveOrZero(weap.ProjectileFormId, validFormIds, remapTable, warnings, weap.FormId, "DNAM projectile"))));

        // CRDT — critical-hit data (16 bytes). Master canonical order has CRDT directly
        // after DNAM and before VATS. Emit only when the model carries non-default values
        // to avoid bloating new WEAPs that don't need a custom crit.
        if (weap.CriticalDamage != 0 || weap.CriticalChance is < 0f or > 0f || weap.CriticalEffectFormId.HasValue)
        {
            var resolvedCritEffect = weap.CriticalEffectFormId.HasValue
                ? ResolveOrZero(weap.CriticalEffectFormId, validFormIds, remapTable,
                    warnings, weap.FormId, "CRDT critical effect")
                : 0u;
            subs.Add(new EncodedSubrecord("CRDT", BuildCrdtSubrecord(weap, resolvedCritEffect)));
        }

        if (weap.ImpactDataSetFormId.HasValue)
        {
            var resolved = FormIdReferenceResolver.Resolve(
                weap.ImpactDataSetFormId.Value, validFormIds, remapTable);
            if (resolved.HasValue)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", resolved.Value));
            }
        }

        // Modded weapon variants — WNAM (base 1st-person STAT FormID) plus WNM1-WNM7 / MWD1-MWD7
        // for each mod combination. fopdoc canonical order: WNAM then alternating WNM*/MWD* per index.
        EmitModelVariants(subs, weap.ModelVariants);

        if (weap.VatsAttack is { } vats)
        {
            subs.Add(SchemaModelSerializer.SerializeSubrecord("VATS", "WEAP", 20, vats, VatsExtractors));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Build the CRDT (Critical Hit Data) subrecord, 16 bytes per the WEAP CRDT schema:
    ///     CriticalDamage(uint16) + pad(2) + CriticalChanceMult(float) + EffectOnDeath(uint8) +
    ///     pad(3) + CriticalEffect(FormID). The resolved FormID is patched onto the record via
    ///     `with { }` so the static schema extractor map picks it up.
    /// </summary>
    private static byte[] BuildCrdtSubrecord(WeaponRecord weap, uint resolvedCriticalEffectFormId)
    {
        var mutated = weap with { CriticalEffectFormId = resolvedCriticalEffectFormId };
        return SchemaModelSerializer.Serialize("CRDT", "", 16, mutated, CrdtExtractors);
    }

    /// <summary>
    ///     Resolve an optional FormID embedded inside a fixed-layout subrecord (DNAM, CRDT).
    ///     Unlike standalone subrecords (NAM0, REPL, INAM) we can't skip emission when a
    ///     field is dangling because the surrounding bytes are required — zero the FormID
    ///     instead. The engine accepts 0 as "no projectile / no critical effect".
    /// </summary>
    private static uint ResolveOrZero(
        uint? formId,
        IReadOnlySet<uint>? validFormIds,
        IReadOnlyDictionary<uint, uint>? remapTable,
        List<string> warnings,
        uint ownerFormId,
        string label)
    {
        if (!formId.HasValue || formId.Value == 0)
        {
            return 0u;
        }

        var resolved = FormIdReferenceResolver.Resolve(formId.Value, validFormIds, remapTable);
        if (resolved.HasValue)
        {
            return resolved.Value;
        }

        warnings.Add(
            $"New WEAP 0x{ownerFormId:X8} {label} 0x{formId.Value:X8} dangles — zeroed in subrecord bytes.");
        return 0u;
    }

    private static void EmitModelVariants(List<EncodedSubrecord> subs, List<WeaponModelVariant> variants)
    {
        if (variants.Count == 0)
        {
            return;
        }

        // WNAM = base (no mods) 1st-person STAT FormID. Only one variant should have None.
        var baseVariant = variants.FirstOrDefault(v => v.Combination == WeaponModCombination.None);
        if (baseVariant?.FirstPersonObjectFormId is { } baseFormId && baseFormId != 0)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("WNAM", baseFormId));
        }

        // Per-combination WNM*/MWD* — index 1-7 maps to combinations in the order the parser uses.
        foreach (var variant in variants)
        {
            var index = CombinationToIndex(variant.Combination);
            if (index <= 0)
            {
                continue;
            }

            if (variant.FirstPersonObjectFormId is { } fpFormId && fpFormId != 0)
            {
                subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord($"WNM{index}", fpFormId));
            }

            if (!string.IsNullOrEmpty(variant.ThirdPersonModelPath))
            {
                subs.Add(NewRecordSubrecords.EncodeStringSubrecord($"MWD{index}", variant.ThirdPersonModelPath));
            }
        }
    }

    private static int CombinationToIndex(WeaponModCombination combination)
    {
        return combination switch
        {
            WeaponModCombination.Mod1 => 1,
            WeaponModCombination.Mod2 => 2,
            WeaponModCombination.Mod3 => 3,
            WeaponModCombination.Mod12 => 4,
            WeaponModCombination.Mod13 => 5,
            WeaponModCombination.Mod23 => 6,
            WeaponModCombination.Mod123 => 7,
            _ => 0
        };
    }

    /// <summary>
    ///     Build the 204-byte DNAM payload per the WEAP DNAM schema. Mutates weap with the
    ///     resolved projectile FormID via `with { }` so the static extractor map sees the
    ///     resolved value at the schema's Projectile field; fields not in the model
    ///     (ConditionLevel / EmbeddedConditionValue / ModActionOne/Two/Three plus their value
    ///     pairs / ModActionOneValueTwo trio) zero-fill via SchemaDictionarySerializer.
    /// </summary>
    private static byte[] BuildDnamSubrecord(WeaponRecord weap, uint resolvedProjectileFormId)
    {
        var mutated = weap with { ProjectileFormId = resolvedProjectileFormId };
        return SchemaModelSerializer.Serialize("DNAM", "WEAP", 204, mutated, DnamExtractors);
    }
}
