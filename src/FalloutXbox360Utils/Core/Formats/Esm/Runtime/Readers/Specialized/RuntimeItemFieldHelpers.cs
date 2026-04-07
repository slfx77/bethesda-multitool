using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Field-reading helpers for item runtime structs: weapon base/combat/critical fields
///     and container content traversal. Used by <see cref="RuntimeItemReader" />.
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
        byte NumProjectiles, byte AmmoPerShot, uint Skill) ReadWeaponCombatFields(byte[] buffer)
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

        return (weaponType, animationType, speed, reach, minSpread, spread,
            minRange, maxRange, vatsChance, actionPoints, shotsPerSec,
            numProjectiles, ammoPerShot, skill);
    }

    internal (short Damage, float Chance, uint? EffectFormId) ReadWeaponCriticalFields(byte[] buffer)
    {
        var damage = (short)BinaryUtils.ReadUInt16BE(buffer, _layouts.WeapCritDamageOffset);
        var chance = BinaryUtils.ReadFloatBE(buffer, _layouts.WeapCritChanceOffset);

        if (!RuntimeMemoryContext.IsNormalFloat(chance) || chance < 0 || chance > 100)
        {
            chance = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            damage = 0;
        }

        // Follow SpellItem* pointer at OBJ_WEAP_CRITICAL+12 to get critical effect FormID
        var effectFormId = _context.FollowPointerToFormId(buffer, _layouts.WeapCritEffectPtrOffset);

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
                    thirdPersonPath = _context.ReadBSStringT(structFileOffset, bsStringOffset);
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
        if (effectFormId is null or 0 && ap == 0 && damMult == 0 && skillReq == 0
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

        static float Rd(byte[] b, int off) => BinaryUtils.ReadFloatBE(b, off);
        static uint Ru(byte[] b, int off) => BinaryUtils.ReadUInt32BE(b, off);

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

    #region Container Helper Methods

    /// <summary>
    ///     Read container contents from TESContainer tList at +120/+124.
    ///     Reuses the same ContainerObject reading logic as NPC inventory.
    /// </summary>
    internal List<InventoryItem> ReadContainerContents(byte[] buffer)
    {
        var items = new List<InventoryItem>();

        // Read inline first node
        var firstDataPtr = BinaryUtils.ReadUInt32BE(buffer, _layouts.ContContentsDataOffset);
        var firstNextPtr = BinaryUtils.ReadUInt32BE(buffer, _layouts.ContContentsNextOffset);

        // Process inline first item
        var firstItem = ReadContainerObject(firstDataPtr);
        if (firstItem != null)
        {
            items.Add(firstItem);
        }

        // Follow chain of _Node (8 bytes each: data ptr + next ptr)
        var nextVA = firstNextPtr;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && items.Count < RuntimeMemoryContext.MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = _context.VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf);
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4);

            var item = ReadContainerObject(dataPtr);
            if (item != null)
            {
                items.Add(item);
            }

            nextVA = nextPtr;
        }

        return items;
    }

    /// <summary>
    ///     Follow a ContainerObject* pointer to read { count(int32 BE), pItem(TESForm*) }.
    ///     Returns an InventoryItem or null.
    /// </summary>
    private InventoryItem? ReadContainerObject(uint containerObjectVA)
    {
        if (containerObjectVA == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(containerObjectVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, 8);
        if (buf == null)
        {
            return null;
        }

        var count = RuntimeMemoryContext.ReadInt32BE(buf, 0);
        var pItem = BinaryUtils.ReadUInt32BE(buf, 4);

        // Validate count (reasonable range for inventory)
        if (count <= 0 || count > 100000)
        {
            Logger.Instance.Debug("[CONT] Rejected inventory item: count={0} (range 1-100000)", count);
            return null;
        }

        // Follow pItem to read the item's FormID
        var itemFormId = _context.FollowPointerVaToFormId(pItem);
        if (itemFormId == null)
        {
            return null;
        }

        return new InventoryItem(itemFormId.Value, count);
    }

    #endregion
}
