using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Field-reading helpers for weapon runtime structs (base/combat/critical fields,
///     mod slots, VATS attack data, modded model variants). Used by
///     <see cref="RuntimeItemReader" />.
/// </summary>
internal sealed class RuntimeItemFieldHelpers
{
    private readonly RuntimeMemoryContext _context;
    private readonly RuntimeItemLayouts _layouts;

    internal RuntimeItemFieldHelpers(RuntimeMemoryContext context, RuntimeItemLayouts layouts)
    {
        _context = context;
        _layouts = layouts;
    }

    #region Weapon Helper Methods

    internal (int Value, int Health, float Weight, short Damage, byte ClipSize)
        ReadWeaponBaseClassFields(byte[] buffer)
    {
        var value = RuntimeMemoryContext.ReadInt32BE(buffer, _layouts.WeapValueOffset);
        var health = RuntimeMemoryContext.ReadInt32BE(buffer, _layouts.WeapHealthOffset);
        var weight = BinaryUtils.ReadFloatBE(buffer, _layouts.WeapWeightOffset);
        var damage = (short)BinaryUtils.ReadUInt16BE(buffer, _layouts.WeapDamageOffset);
        var clipSize = buffer[_layouts.WeapClipRoundsOffset];

        if (value < 0 || value > 1000000)
        {
            Logger.Instance.Debug("[WEAP] Clamped value={0} to 0 (range 0-1000000)", value);
            value = 0;
        }

        if (health < 0 || health > 100000)
        {
            Logger.Instance.Debug("[WEAP] Clamped health={0} to 0 (range 0-100000)", health);
            health = 0;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(weight) || weight < 0 || weight > 500)
        {
            Logger.Instance.Debug("[WEAP] Clamped weight={0} to 0 (range 0-500)", weight);
            weight = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            Logger.Instance.Debug("[WEAP] Clamped damage={0} to 0 (range 0-10000)", damage);
            damage = 0;
        }

        return (value, health, weight, damage, clipSize);
    }

    internal (WeaponType WeaponType, uint AnimationType, float Speed, float Reach,
        float MinSpread, float Spread, float MinRange, float MaxRange,
        byte VatsChance, float ActionPoints, float ShotsPerSec,
        byte NumProjectiles, byte AmmoPerShot, uint Skill, uint StrengthRequirement)
        ReadWeaponCombatFields(byte[] buffer)
    {
        // animationType is stored as uint8 at the first byte of a 4-byte aligned field
        var animTypeByte = buffer[_layouts.WeapAnimTypeOffset];
        var animationType = Enum.IsDefined(typeof(WeaponType), animTypeByte) ? animTypeByte : 0u;

        var speed = BinaryUtils.ReadFloatBE(buffer, _layouts.WeapSpeedOffset);
        var reach = BinaryUtils.ReadFloatBE(buffer, _layouts.WeapReachOffset);

        if (!RuntimeMemoryContext.IsNormalFloat(speed) || speed < 0 || speed > 100)
        {
            speed = 1.0f;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(reach) || reach < 0 || reach > 1000)
        {
            reach = 0;
        }

        var weaponType = Enum.IsDefined(typeof(WeaponType), animTypeByte)
            ? (WeaponType)animTypeByte
            : WeaponType.HandToHandMelee;

        var dataStart = _layouts.WeapDataStart;
        var minSpread =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamMinSpreadRelOffset, 0,
                1000);
        var spread = RuntimeMemoryContext.ReadValidatedFloat(buffer,
            dataStart + RuntimeItemLayouts.DnamSpreadRelOffset, 0, 1000);
        var minRange =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamMinRangeRelOffset, 0,
                100000);
        var maxRange =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamMaxRangeRelOffset, 0,
                100000);
        var actionPoints =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamActionPointsRelOffset,
                0, 1000);
        var shotsPerSec =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, dataStart + RuntimeItemLayouts.DnamShotsPerSecRelOffset,
                0, 1000);

        var vatsChance = buffer[dataStart + RuntimeItemLayouts.DnamVatsChanceRelOffset];
        if (vatsChance > 100)
        {
            vatsChance = 0;
        }

        var numProjectiles = buffer[dataStart + RuntimeItemLayouts.DnamNumProjectilesRelOffset];
        var ammoPerShot = buffer[dataStart + RuntimeItemLayouts.DnamAmmoPerShotRelOffset];

        var skill = BinaryUtils.ReadUInt32BE(buffer, dataStart + RuntimeItemLayouts.DnamSkillRelOffset);
        if (skill > 76)
        {
            skill = 0;
        }

        var strengthRequirement =
            BinaryUtils.ReadUInt32BE(buffer, dataStart + RuntimeItemLayouts.DnamStrengthRequirementRelOffset);
        if (strengthRequirement > 10)
        {
            strengthRequirement = 0;
        }

        return (weaponType, animationType, speed, reach, minSpread, spread,
            minRange, maxRange, vatsChance, actionPoints, shotsPerSec,
            numProjectiles, ammoPerShot, skill, strengthRequirement);
    }

    internal (short Damage, float Chance, uint? EffectFormId) ReadWeaponCriticalFields(byte[] buffer)
    {
        // Apply RuntimeWeaponCritProbe's discovered shift on top of the reference
        // OBJ_WEAP_CRITICAL position so builds whose criticalData block sits at a
        // non-default offset still read correctly. Shift is 0 when no probe ran or
        // confidence was low (no behavior change vs pre-probe baseline).
        var damageOffset = _layouts.WeapCritDamageOffset;
        var chanceOffset = _layouts.WeapCritChanceOffset;
        var effectOffset = _layouts.WeapCritEffectPtrOffset;

        var damage = damageOffset + 2 <= buffer.Length
            ? (short)BinaryUtils.ReadUInt16BE(buffer, damageOffset)
            : (short)0;
        var chance = chanceOffset + 4 <= buffer.Length
            ? BinaryUtils.ReadFloatBE(buffer, chanceOffset)
            : 0f;

        if (!RuntimeMemoryContext.IsNormalFloat(chance) || chance < 0 || chance > 100)
        {
            chance = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            damage = 0;
        }

        // Follow SpellItem* pointer at OBJ_WEAP_CRITICAL+12 to get critical effect FormID
        var effectFormId = effectOffset + 4 <= buffer.Length
            ? _context.FollowPointerToFormId(buffer, effectOffset)
            : null;

        return (damage, chance, effectFormId);
    }

    /// <summary>
    ///     Read modded weapon model variants — base model + 7 combinations.
    ///     Each combination has a 1st-person STAT pointer (PDB 608+i*4) and a 3rd-person
    ///     TESModelTextureSwap struct (PDB 640+i*32, cModel BSStringT at +4 within).
    ///     Returns the base + variants that have non-null model paths or non-zero STAT pointers.
    /// </summary>
    internal List<WeaponModelVariant> ReadWeaponModelVariants(byte[] buffer, long structFileOffset)
    {
        var result = new List<WeaponModelVariant>();

        var combinations = new[]
        {
            WeaponModCombination.None,
            WeaponModCombination.Mod1,
            WeaponModCombination.Mod2,
            WeaponModCombination.Mod3,
            WeaponModCombination.Mod12,
            WeaponModCombination.Mod23,
            WeaponModCombination.Mod13,
            WeaponModCombination.Mod123
        };

        var firstPersonBase = _layouts.Weap1stPersonObjectOffset;
        var worldModelBase = _layouts.WeapWorldModelMod1Offset;

        for (var i = 0; i < 8; i++)
        {
            // 1st-person STAT pointer (4 bytes per slot)
            uint? firstPersonFormId = null;
            var fpOffset = firstPersonBase + i * 4;
            if (fpOffset >= 0 && fpOffset + 4 <= buffer.Length)
            {
                firstPersonFormId = _context.FollowPointerToFormId(buffer, fpOffset);
            }

            // 3rd-person model: TESModelTextureSwap at 32-byte stride.
            // The base (None) variant uses the existing WeapModelPathOffset; only Mod1..Mod123
            // are in the WorldModelMod array. Skip i=0 and read i-1 for the array index.
            string? thirdPersonPath = null;
            if (i >= 1)
            {
                // TESModelTextureSwap struct at worldModelBase + (i-1)*32
                // cModel BSStringT inside the struct is at +4
                var modelStructOffset = worldModelBase + (i - 1) * 32;
                var bsStringOffset = modelStructOffset + 4;
                if (bsStringOffset >= 0 && bsStringOffset + 8 <= buffer.Length)
                {
                    thirdPersonPath = _context.ReadBsStringT(structFileOffset, bsStringOffset);
                }
            }

            // Skip empty variants (no model AND no 1st-person object)
            if (string.IsNullOrEmpty(thirdPersonPath) && firstPersonFormId is null or 0)
            {
                continue;
            }

            result.Add(new WeaponModelVariant
            {
                Combination = combinations[i],
                ThirdPersonModelPath = thirdPersonPath,
                FirstPersonObjectFormId = firstPersonFormId
            });
        }

        return result;
    }

    /// <summary>
    ///     Read OBJ_WEAP_VATS_SPECIAL (20 bytes) from the runtime weapon struct.
    ///     PDB layout (verified):
    ///     +0  pVATSSpecialEffect (TESForm*, 4)
    ///     +4  fVATSSpecialAP (float)
    ///     +8  fVATSSpecialMultiplier (float)
    ///     +12 fVATSSkillRequired (float)
    ///     +16 bSilent (1B), +17 bModRequired (1B), +18 cFlags (1B), +19 padding
    /// </summary>
    internal VatsAttackData? ReadWeaponVatsAttack(byte[] buffer)
    {
        var vatsOffset = _layouts.WeapVatsDataOffset;
        if (vatsOffset < 0 || vatsOffset + 20 > buffer.Length)
        {
            return null;
        }

        var effectFormId = _context.FollowPointerToFormId(buffer, vatsOffset);
        var ap = BinaryUtils.ReadFloatBE(buffer, vatsOffset + 4);
        var damMult = BinaryUtils.ReadFloatBE(buffer, vatsOffset + 8);
        var skillReq = BinaryUtils.ReadFloatBE(buffer, vatsOffset + 12);
        var silent = buffer[vatsOffset + 16];
        var modRequired = buffer[vatsOffset + 17];
        var extraFlags = buffer[vatsOffset + 18];

        // Skip if everything is zero (no VATS attack configured)
        if (effectFormId is null or 0 && Math.Abs(ap) <= 0f && Math.Abs(damMult) <= 0f && Math.Abs(skillReq) <= 0f
            && silent == 0 && modRequired == 0 && extraFlags == 0)
        {
            return null;
        }

        return new VatsAttackData
        {
            EffectFormId = effectFormId ?? 0,
            ActionPointCost = RuntimeMemoryContext.IsNormalFloat(ap) ? ap : 0,
            DamageMultiplier = RuntimeMemoryContext.IsNormalFloat(damMult) ? damMult : 0,
            SkillRequired = RuntimeMemoryContext.IsNormalFloat(skillReq) ? skillReq : 0,
            IsSilent = silent != 0,
            RequiresMod = modRequired != 0,
            ExtraFlags = extraFlags
        };
    }

    /// <summary>
    ///     Read Phase 3 OBJ_WEAP fields (rumble, animation overrides, semi-auto delays, etc.)
    ///     from the runtime DNAM data block at offset WeapDataStart.
    /// </summary>
    internal (float DamageToWeaponMult, uint Resistance, float IronSightUseMult, float AmmoRegenRate,
        float KillImpulse, float KillImpulseDistance, float SemiAutoMin, float SemiAutoMax,
        float AnimShotsPerSec, float AnimReloadTime, float AnimJamTime, byte PowerAttackOverride,
        sbyte ModReloadAnim, sbyte ModFireAnim, float CookTimer, float RumbleLeft, float RumbleRight,
        float RumbleDuration, uint RumblePattern, float RumbleWavelength) ReadWeaponPhase3Fields(byte[] buffer)
    {
        var d = _layouts.WeapDataStart;

        static float Rd(byte[] b, int off)
        {
            return BinaryUtils.ReadFloatBE(b, off);
        }

        static uint Ru(byte[] b, int off)
        {
            return BinaryUtils.ReadUInt32BE(b, off);
        }

        return (
            DamageToWeaponMult: Rd(buffer, d + RuntimeItemLayouts.DnamDamageToWeaponMultRelOffset),
            Resistance: Ru(buffer, d + RuntimeItemLayouts.DnamResistanceRelOffset),
            IronSightUseMult: Rd(buffer, d + RuntimeItemLayouts.DnamIronSightUseMultRelOffset),
            AmmoRegenRate: Rd(buffer, d + RuntimeItemLayouts.DnamAmmoRegenRateRelOffset),
            KillImpulse: Rd(buffer, d + RuntimeItemLayouts.DnamKillImpulseRelOffset),
            KillImpulseDistance: Rd(buffer, d + RuntimeItemLayouts.DnamKillImpulseDistanceRelOffset),
            SemiAutoMin: Rd(buffer, d + RuntimeItemLayouts.DnamSemiAutoDelayMinRelOffset),
            SemiAutoMax: Rd(buffer, d + RuntimeItemLayouts.DnamSemiAutoDelayMaxRelOffset),
            AnimShotsPerSec: Rd(buffer, d + RuntimeItemLayouts.DnamAnimShotsPerSecondRelOffset),
            AnimReloadTime: Rd(buffer, d + RuntimeItemLayouts.DnamAnimReloadTimeRelOffset),
            AnimJamTime: Rd(buffer, d + RuntimeItemLayouts.DnamAnimJamTimeRelOffset),
            PowerAttackOverride: buffer[d + RuntimeItemLayouts.DnamPowerAttackOverrideRelOffset],
            ModReloadAnim: (sbyte)buffer[d + RuntimeItemLayouts.DnamModReloadClipAnimationRelOffset],
            ModFireAnim: (sbyte)buffer[d + RuntimeItemLayouts.DnamModFireAnimationRelOffset],
            CookTimer: Rd(buffer, d + RuntimeItemLayouts.DnamCookTimerRelOffset),
            RumbleLeft: Rd(buffer, d + RuntimeItemLayouts.DnamRumbleLeftMotorRelOffset),
            RumbleRight: Rd(buffer, d + RuntimeItemLayouts.DnamRumbleRightMotorRelOffset),
            RumbleDuration: Rd(buffer, d + RuntimeItemLayouts.DnamRumbleDurationRelOffset),
            RumblePattern: Ru(buffer, d + RuntimeItemLayouts.DnamRumblePatternRelOffset),
            RumbleWavelength: Rd(buffer, d + RuntimeItemLayouts.DnamRumbleWavelengthRelOffset)
        );
    }

    internal List<WeaponModSlot> ReadWeaponModSlots(byte[] buffer)
    {
        var result = new List<WeaponModSlot>();
        var dataStart = _layouts.WeapDataStart;

        var actions = new[]
        {
            BinaryUtils.ReadUInt32BE(buffer, dataStart + RuntimeItemLayouts.DnamModActionOneRelOffset),
            BinaryUtils.ReadUInt32BE(buffer, dataStart + RuntimeItemLayouts.DnamModActionTwoRelOffset),
            BinaryUtils.ReadUInt32BE(buffer, dataStart + RuntimeItemLayouts.DnamModActionThreeRelOffset)
        };
        var values = new[]
        {
            BinaryUtils.ReadFloatBE(buffer, dataStart + RuntimeItemLayouts.DnamModValueOneRelOffset),
            BinaryUtils.ReadFloatBE(buffer, dataStart + RuntimeItemLayouts.DnamModValueTwoRelOffset),
            BinaryUtils.ReadFloatBE(buffer, dataStart + RuntimeItemLayouts.DnamModValueThreeRelOffset)
        };
        var values2 = new[]
        {
            BinaryUtils.ReadFloatBE(buffer, dataStart + RuntimeItemLayouts.DnamModValueTwoOneRelOffset),
            BinaryUtils.ReadFloatBE(buffer, dataStart + RuntimeItemLayouts.DnamModValueTwoTwoRelOffset),
            BinaryUtils.ReadFloatBE(buffer, dataStart + RuntimeItemLayouts.DnamModValueTwoThreeRelOffset)
        };
        var modPtrs = new[]
        {
            _context.FollowPointerToFormId(buffer, _layouts.WeapModObjectOneOffset),
            _context.FollowPointerToFormId(buffer, _layouts.WeapModObjectTwoOffset),
            _context.FollowPointerToFormId(buffer, _layouts.WeapModObjectThreeOffset)
        };

        for (var i = 0; i < 3; i++)
        {
            if (actions[i] == 0)
            {
                continue;
            }

            var val = RuntimeMemoryContext.IsNormalFloat(values[i]) ? values[i] : 0f;
            var val2 = RuntimeMemoryContext.IsNormalFloat(values2[i]) ? values2[i] : 0f;

            result.Add(new WeaponModSlot
            {
                SlotIndex = i + 1,
                Action = Enum.IsDefined(typeof(WeaponModAction), actions[i])
                    ? (WeaponModAction)actions[i]
                    : WeaponModAction.None,
                Value = val,
                ValueTwo = val2,
                ModFormId = modPtrs[i]
            });
        }

        return result;
    }

    #endregion

    // Container Helper Methods removed: the previous ReadContainerContents +
    // ReadContainerObject pair on this class was dead code (no callers — the only
    // CONT consumer is RuntimeContainerReader, which has its own equivalent private
    // implementation). Deleting them also unblocked removing the CONT region of
    // RuntimeItemLayouts.
}
