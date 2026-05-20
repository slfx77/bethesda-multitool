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
    public string RecordType => "WEAP";
    public Type ModelType => typeof(WeaponRecord);

    public EncodedRecord Encode(object model)
    {
        var weap = (WeaponRecord)model;
        return new EncodedRecord
        {
            Subrecords = [new EncodedSubrecord("DATA", BuildDataSubrecord(weap))],
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

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(weap)));
        // DNAM embeds the projectile FormID at offset 36. When dangling, zero it (engine
        // logs "Could not find projectile" but the weapon still loads — just fires nothing).
        subs.Add(new EncodedSubrecord("DNAM",
            BuildDnamSubrecord(weap, ResolveOrZero(weap.ProjectileFormId, validFormIds, remapTable, warnings, weap.FormId, "DNAM projectile"))));

        // CRDT — critical-hit data (16 bytes). Master canonical order has CRDT directly
        // after DNAM and before VATS. Emit only when the model carries non-default values
        // to avoid bloating new WEAPs that don't need a custom crit.
        if (weap.CriticalDamage != 0 || weap.CriticalChance != 0f || weap.CriticalEffectFormId.HasValue)
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
            subs.Add(new EncodedSubrecord("VATS", BuildVatsSubrecord(vats)));
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
    }

    /// <summary>
    ///     Build the CRDT (Critical Hit Data) subrecord. FNV layout (16 bytes), confirmed
    ///     against master FalloutNV.esm Weap10mmPistol CRDT block at offset 0x85F420:
    ///     <list type="bullet">
    ///         <item>uint16 CritDamage (bytes 0-1)</item>
    ///         <item>uint16 Unused (bytes 2-3)</item>
    ///         <item>float CritPercentMult (bytes 4-7)</item>
    ///         <item>uint8 CritFlags (byte 8) — bit 0 = "On Death"</item>
    ///         <item>uint8[3] Unused (bytes 9-11)</item>
    ///         <item>FormID CritEffect (bytes 12-15)</item>
    ///     </list>
    /// </summary>
    private static byte[] BuildCrdtSubrecord(WeaponRecord weap, uint resolvedCriticalEffectFormId)
    {
        var data = new byte[16];
        SubrecordEncoder.WriteInt16(data, 0, weap.CriticalDamage);
        // bytes 2-3 unused (zero)
        SubrecordEncoder.WriteFloat(data, 4, weap.CriticalChance);
        // byte 8 = CritFlags ("On Death" not modeled, default 0)
        // bytes 9-11 unused (zero)
        SubrecordEncoder.WriteFormId(data, 12, resolvedCriticalEffectFormId);
        return data;
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

    private static byte[] BuildVatsSubrecord(VatsAttackData vats)
    {
        // VATS (20 bytes) per PDB OBJ_WEAP_VATS_SPECIAL:
        //   FormID Effect(0) + float AP(4) + float Multiplier(8) + float SkillRequired(12) +
        //   bool IsSilent(16) + bool RequiresMod(17) + uint8 Flags(18) + padding(19).
        var data = new byte[20];
        SubrecordEncoder.WriteFormId(data, 0, vats.EffectFormId);
        SubrecordEncoder.WriteFloat(data, 4, vats.ActionPointCost);
        SubrecordEncoder.WriteFloat(data, 8, vats.DamageMultiplier);
        SubrecordEncoder.WriteFloat(data, 12, vats.SkillRequired);
        data[16] = vats.IsSilent ? (byte)1 : (byte)0;
        data[17] = vats.RequiresMod ? (byte)1 : (byte)0;
        data[18] = vats.ExtraFlags;
        // byte 19 padding (zero)
        return data;
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

    private static byte[] BuildDataSubrecord(WeaponRecord weap)
    {
        var data = new byte[15];
        SubrecordEncoder.WriteInt32(data, 0, weap.Value);
        SubrecordEncoder.WriteInt32(data, 4, weap.Health);
        SubrecordEncoder.WriteFloat(data, 8, weap.Weight);
        SubrecordEncoder.WriteInt16(data, 12, weap.Damage);
        data[14] = weap.ClipSize;
        return data;
    }

    /// <summary>
    ///     Build the 204-byte DNAM payload per the WEAP DNAM schema. Field offsets match
    ///     <c>SubrecordItemSchemas.cs</c> — bytes for fields the model doesn't expose (e.g.,
    ///     ModActionOne/Two/Three FormIDs and their value pairs) are left as zero.
    /// </summary>
    private static byte[] BuildDnamSubrecord(WeaponRecord weap, uint resolvedProjectileFormId)
    {
        var dnam = new byte[204];

        // 0: int8 WeaponType
        dnam[0] = (byte)weap.WeaponType;
        // 1-3 padding (zero)
        // 4-7: float Speed
        SubrecordEncoder.WriteFloat(dnam, 4, weap.Speed);
        // 8-11: float Reach
        SubrecordEncoder.WriteFloat(dnam, 8, weap.Reach);
        // 12: uint8 Flags
        dnam[12] = weap.Flags;
        // 13: uint8 HandGripAnim
        dnam[13] = (byte)weap.HandGrip;
        // 14: uint8 AmmoPerShot
        dnam[14] = weap.AmmoPerShot;
        // 15: uint8 ReloadAnim
        dnam[15] = (byte)weap.ReloadAnim;
        // 16-19: float MinSpread
        SubrecordEncoder.WriteFloat(dnam, 16, weap.MinSpread);
        // 20-23: float Spread
        SubrecordEncoder.WriteFloat(dnam, 20, weap.Spread);
        // 24-27: float Drift
        SubrecordEncoder.WriteFloat(dnam, 24, weap.Drift);
        // 28-31: float IronFov
        SubrecordEncoder.WriteFloat(dnam, 28, weap.IronSightFov);
        // 32: uint8 ConditionLevel — not in model; zero
        // 33-35 padding
        // 36-39: FormIdLittleEndian Projectile (resolved by caller — zero if dangling).
        SubrecordEncoder.WriteFormId(dnam, 36, resolvedProjectileFormId);
        // 40: uint8 VatToHitChance
        dnam[40] = weap.VatsToHitChance;
        // 41: uint8 AttackAnim
        dnam[41] = (byte)weap.AttackAnim;
        // 42: uint8 NumProjectiles
        dnam[42] = weap.NumProjectiles;
        // 43: uint8 EmbeddedConditionValue — not in model; zero
        // 44-47: float MinRange
        SubrecordEncoder.WriteFloat(dnam, 44, weap.MinRange);
        // 48-51: float MaxRange
        SubrecordEncoder.WriteFloat(dnam, 48, weap.MaxRange);
        // 52-55: uint32 HitBehavior (enum OnHit cast to uint)
        SubrecordEncoder.WriteUInt32(dnam, 52, (uint)weap.OnHit);
        // 56-59: uint32 FlagsEx
        SubrecordEncoder.WriteUInt32(dnam, 56, weap.FlagsEx);
        // 60-63: float AttackMult
        SubrecordEncoder.WriteFloat(dnam, 60, weap.AttackMultiplier);
        // 64-67: float ShotsPerSec
        SubrecordEncoder.WriteFloat(dnam, 64, weap.ShotsPerSec);
        // 68-71: float ActionPoints
        SubrecordEncoder.WriteFloat(dnam, 68, weap.ActionPoints);
        // 72-75: float RumbleLeftMotor
        SubrecordEncoder.WriteFloat(dnam, 72, weap.RumbleLeftMotor);
        // 76-79: float RumbleRightMotor
        SubrecordEncoder.WriteFloat(dnam, 76, weap.RumbleRightMotor);
        // 80-83: float RumbleDuration
        SubrecordEncoder.WriteFloat(dnam, 80, weap.RumbleDuration);
        // 84-87: float DamageToWeaponMult
        SubrecordEncoder.WriteFloat(dnam, 84, weap.DamageToWeaponMult);
        // 88-91: float AnimShotsPerSecond
        SubrecordEncoder.WriteFloat(dnam, 88, weap.AnimShotsPerSecond);
        // 92-95: float AnimReloadTime
        SubrecordEncoder.WriteFloat(dnam, 92, weap.AnimReloadTime);
        // 96-99: float AnimJamTime
        SubrecordEncoder.WriteFloat(dnam, 96, weap.AnimJamTime);
        // 100-103: float AimArc
        SubrecordEncoder.WriteFloat(dnam, 100, weap.AimArc);
        // 104-107: uint32 Skill
        SubrecordEncoder.WriteUInt32(dnam, 104, weap.Skill);
        // 108-111: uint32 RumblePattern
        SubrecordEncoder.WriteUInt32(dnam, 108, weap.RumblePattern);
        // 112-115: float RumbleWavelength
        SubrecordEncoder.WriteFloat(dnam, 112, weap.RumbleWavelength);
        // 116-119: float LimbDamageMult
        SubrecordEncoder.WriteFloat(dnam, 116, weap.LimbDamageMult);
        // 120-123: uint32 Resistance
        SubrecordEncoder.WriteUInt32(dnam, 120, weap.Resistance);
        // 124-127: float IronSightUseMult
        SubrecordEncoder.WriteFloat(dnam, 124, weap.IronSightUseMult);
        // 128-131: float SemiAutoDelayMin
        SubrecordEncoder.WriteFloat(dnam, 128, weap.SemiAutoFireDelayMin);
        // 132-135: float SemiAutoDelayMax
        SubrecordEncoder.WriteFloat(dnam, 132, weap.SemiAutoFireDelayMax);
        // 136-139: float CookTimer
        SubrecordEncoder.WriteFloat(dnam, 136, weap.CookTimer);
        // 140-163: ModAction1/2/3 + values — not in model; zero
        // 164: uint8 PowerAttackOverrideAnim
        dnam[164] = weap.PowerAttackOverrideAnim;
        // 165-167 padding
        // 168-171: uint32 StrengthRequirement
        SubrecordEncoder.WriteUInt32(dnam, 168, weap.StrengthRequirement);
        // 172: int8 ModReloadClipAnimation
        dnam[172] = (byte)weap.ModReloadClipAnimation;
        // 173: int8 ModFireAnimation
        dnam[173] = (byte)weap.ModFireAnimation;
        // 174-175 padding
        // 176-179: float AmmoRegenRate
        SubrecordEncoder.WriteFloat(dnam, 176, weap.AmmoRegenRate);
        // 180-183: float KillImpulse
        SubrecordEncoder.WriteFloat(dnam, 180, weap.KillImpulse);
        // 184-195: ModActionValueTwo trio — not in model; zero
        // 196-199: float KillImpulseDistance
        SubrecordEncoder.WriteFloat(dnam, 196, weap.KillImpulseDistance);
        // 200-203: uint32 SkillRequirement
        SubrecordEncoder.WriteUInt32(dnam, 200, weap.SkillRequirement);

        return dnam;
    }
}
