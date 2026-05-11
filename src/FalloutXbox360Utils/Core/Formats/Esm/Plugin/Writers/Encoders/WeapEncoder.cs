using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.Writers.Encoders;

/// <summary>
///     Encodes a <see cref="WeaponRecord" /> as PC-format WEAP subrecord bytes.
///     v1 emits DATA (15 bytes) only. DNAM (204 bytes), CRDT, ETYP, ENAM, etc. are retained
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
    ///     Encode a new WEAP record from scratch. fopdoc canonical order:
    ///     EDID, OBND?, FULL?, MODL?, ICON?, MICO?, DATA, DNAM. ETYP/ENAM/CRDT/SCRI are
    ///     deferred — model exposes them as enums/derived values rather than raw FormIDs.
    /// </summary>
    /// <remarks>
    ///     DNAM (204 bytes) is emitted with all model-covered fields. Fields the model doesn't
    ///     expose (ModActionOne/Two/Three pairs, EmbeddedConditionValue) are zeroed.
    ///     Padding bytes per the schema are zeroed.
    /// </remarks>
    internal static EncodedRecord EncodeNew(WeaponRecord weap)
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

        // ETYP — 4-byte int32 (enum -1..13). Emit when not None, after FULL/MODL.
        // Despite the schema registering ETYP as a FormID type for endian-swap purposes,
        // FNV's parser reads it as int32 — see WeaponRecordHandler.cs:313-320.
        if (weap.EquipmentType != Enums.EquipmentType.None)
        {
            subs.Add(NewRecordSubrecords.EncodeInt32Subrecord("ETYP", (int)weap.EquipmentType));
        }

        if (weap.AmmoFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("ENAM", weap.AmmoFormId.Value));
        }

        if (weap.RepairItemListFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("REPL", weap.RepairItemListFormId.Value));
        }

        subs.Add(new EncodedSubrecord("DATA", BuildDataSubrecord(weap)));
        subs.Add(new EncodedSubrecord("DNAM", BuildDnamSubrecord(weap)));

        if (weap.ImpactDataSetFormId.HasValue)
        {
            subs.Add(NewRecordSubrecords.EncodeFormIdSubrecord("INAM", weap.ImpactDataSetFormId.Value));
        }

        // Modded weapon variants — WNAM (base 1st-person STAT FormID) plus WNM1-WNM7 / MWD1-MWD7
        // for each mod combination. fopdoc canonical order: WNAM then alternating WNM*/MWD* per index.
        EmitModelVariants(subs, weap.ModelVariants);

        if (weap.VatsAttack is not null)
        {
            warnings.Add($"New WEAP 0x{weap.FormId:X8} has VATS attack data — VATS subrecord emission deferred to v6.");
        }

        return new EncodedRecord { Subrecords = subs, Warnings = warnings };
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

    private static int CombinationToIndex(WeaponModCombination combination) => combination switch
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
    private static byte[] BuildDnamSubrecord(WeaponRecord weap)
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
        // 36-39: FormIdLittleEndian Projectile
        SubrecordEncoder.WriteFormId(dnam, 36, weap.ProjectileFormId ?? 0);
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
