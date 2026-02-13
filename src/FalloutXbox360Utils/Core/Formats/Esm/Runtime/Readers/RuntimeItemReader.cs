using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reads item-related runtime structs (weapons, armor, ammo, misc items, keys,
///     containers, consumables) from Xbox 360 memory dumps.
/// </summary>
internal sealed class RuntimeItemReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    // Build-specific offset shift: Proto Debug PDB + _s = actual dump offset.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    #region Struct Layouts (Proto Debug PDB base + _s)

    // TESObjectWEAP: PDB size 908, Debug dump 912, Release dump 924
    private int WeapStructSize => 908 + _s;
    private int WeapModelPathOffset => 64 + _s;
    private int WeapValueOffset => 136 + _s;
    private int WeapWeightOffset => 144 + _s;
    private int WeapHealthOffset => 152 + _s;
    private int WeapDamageOffset => 160 + _s;
    private int WeapAmmoPtrOffset => 168 + _s;
    private int WeapClipRoundsOffset => 176 + _s;
    private int WeapDataStart => 244 + _s;
    private int WeapAnimTypeOffset => 244 + _s;
    private int WeapSpeedOffset => 248 + _s;
    private int WeapReachOffset => 252 + _s;
    // DNAM relative offsets — fixed within the DNAM data block, not TESForm-derived
    private const int DnamMinSpreadRelOffset = 16;
    private const int DnamSpreadRelOffset = 20;
    private const int DnamProjectileRelOffset = 36;
    private const int DnamVatsChanceRelOffset = 40;
    private const int DnamMinRangeRelOffset = 44;
    private const int DnamMaxRangeRelOffset = 48;
    private const int DnamActionPointsRelOffset = 68;
    private const int DnamShotsPerSecRelOffset = 88;
    private int WeapCritDamageOffset => 440 + _s;
    private int WeapCritChanceOffset => 444 + _s;
    private int WeapPickupSoundOffset => 236 + _s;
    private int WeapPutdownSoundOffset => 240 + _s;
    private int WeapFireSound3DOffset => 532 + _s;
    private int WeapFireSoundDistOffset => 536 + _s;
    private int WeapFireSound2DOffset => 540 + _s;
    private int WeapDryFireSoundOffset => 548 + _s;
    private int WeapIdleSoundOffset => 556 + _s;
    private int WeapEquipSoundOffset => 560 + _s;
    private int WeapUnequipSoundOffset => 564 + _s;
    private int WeapImpactDataSetOffset => 568 + _s;

    // TESObjectARMO: PDB size 400, Debug dump 404, Release dump 416
    private int ArmoStructSize => 400 + _s;
    private int ArmoValueOffset => 92 + _s;
    private int ArmoWeightOffset => 100 + _s;
    private int ArmoHealthOffset => 108 + _s;
    private int ArmoRatingOffset => 376 + _s;

    // TESObjectAMMO: PDB size ~220, Debug dump ~224, Release dump 236
    private int AmmoStructSize => 220 + _s;
    private int AmmoValueOffset => 124 + _s;

    // TESObjectALCH: PDB size ~216, Debug dump ~220, Release dump 232
    private int AlchStructSize => 216 + _s;
    private int AlchWeightOffset => 152 + _s;
    private int AlchValueOffset => 184 + _s;

    // TESObjectMISC / TESKey: PDB size 172, Debug dump 176, Release dump 188
    private int MiscStructSize => 172 + _s;
    private int MiscValueOffset => 120 + _s;
    private int MiscWeightOffset => 128 + _s;

    // TESObjectCONT: PDB size 156, Debug dump 160, Release dump 172
    private int ContStructSize => 156 + _s;
    private int ContModelPathOffset => 64 + _s;
    private int ContScriptPtrOffset => 108 + _s; // TESScriptableForm::pFormScript (base+104, field+4)
    private int ContContentsDataOffset => 52 + _s;
    private int ContContentsNextOffset => 56 + _s;
    private int ContFlagsOffset => 124 + _s;

    #endregion

    /// <summary>
    ///     Read extended weapon data from a runtime TESObjectWEAP struct.
    ///     Returns a WeaponRecord with combat stats, or null if validation fails.
    /// </summary>
    public WeaponRecord? ReadRuntimeWeapon(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x28)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + WeapStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[WeapStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, WeapStructSize);
        }
        catch
        {
            return null;
        }

        // Validate: FormID at offset 12 should match
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read fields from base classes and weapon data struct
        var baseFields = ReadWeaponBaseClassFields(buffer);
        var combatFields = ReadWeaponCombatFields(buffer);
        var critFields = ReadWeaponCriticalFields(buffer);

        // Follow ammo pointer to get ammo FormID
        var ammoFormId = _context.FollowPointerToFormId(buffer, WeapAmmoPtrOffset);

        // Read model path via BSStringT at TESModel offset
        var modelPath = _context.ReadBSStringT(offset, WeapModelPathOffset);

        // Read sound pointers (TESSound* at various offsets)
        var pickupSound = _context.FollowPointerToFormId(buffer, WeapPickupSoundOffset);
        var putdownSound = _context.FollowPointerToFormId(buffer, WeapPutdownSoundOffset);
        var fireSound3D = _context.FollowPointerToFormId(buffer, WeapFireSound3DOffset);
        var fireSoundDist = _context.FollowPointerToFormId(buffer, WeapFireSoundDistOffset);
        var fireSound2D = _context.FollowPointerToFormId(buffer, WeapFireSound2DOffset);
        var dryFireSound = _context.FollowPointerToFormId(buffer, WeapDryFireSoundOffset);
        var idleSound = _context.FollowPointerToFormId(buffer, WeapIdleSoundOffset);
        var equipSound = _context.FollowPointerToFormId(buffer, WeapEquipSoundOffset);
        var unequipSound = _context.FollowPointerToFormId(buffer, WeapUnequipSoundOffset);
        var impactDataSet = _context.FollowPointerToFormId(buffer, WeapImpactDataSetOffset);

        return new WeaponRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = baseFields.Value,
            Health = baseFields.Health,
            Weight = baseFields.Weight,
            Damage = baseFields.Damage,
            ClipSize = baseFields.ClipSize,
            WeaponType = combatFields.WeaponType,
            AnimationType = combatFields.AnimationType,
            Speed = combatFields.Speed,
            Reach = combatFields.Reach,
            MinSpread = combatFields.MinSpread,
            Spread = combatFields.Spread,
            MinRange = combatFields.MinRange,
            MaxRange = combatFields.MaxRange,
            ActionPoints = combatFields.ActionPoints,
            ShotsPerSec = combatFields.ShotsPerSec,
            VatsToHitChance = combatFields.VatsChance,
            AmmoFormId = ammoFormId,
            ProjectileFormId = _context.FollowPointerToFormId(buffer, WeapDataStart + DnamProjectileRelOffset),
            CriticalDamage = critFields.Damage,
            CriticalChance = critFields.Chance,
            ModelPath = modelPath,
            PickupSoundFormId = pickupSound,
            PutdownSoundFormId = putdownSound,
            FireSound3DFormId = fireSound3D,
            FireSoundDistFormId = fireSoundDist,
            FireSound2DFormId = fireSound2D,
            DryFireSoundFormId = dryFireSound,
            IdleSoundFormId = idleSound,
            EquipSoundFormId = equipSound,
            UnequipSoundFormId = unequipSound,
            ImpactDataSetFormId = impactDataSet,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended armor data from a runtime TESObjectARMO struct.
    ///     Returns a ArmorRecord with Value/Weight/Health/AR, or null if validation fails.
    /// </summary>
    public ArmorRecord? ReadRuntimeArmor(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x18)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + ArmoStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[ArmoStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, ArmoStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, ArmoValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, ArmoWeightOffset, 0, 500);

        var health = RuntimeMemoryContext.ReadInt32BE(buffer, ArmoHealthOffset);
        if (health < 0 || health > 100000)
        {
            health = 0;
        }

        var armorRatingRaw = BinaryUtils.ReadUInt16BE(buffer, ArmoRatingOffset);
        var damageThreshold = armorRatingRaw / 100.0f;

        return new ArmorRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = value,
            Weight = weight,
            Health = health,
            DamageThreshold = damageThreshold,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended ammo data from a runtime TESObjectAMMO struct.
    ///     Returns a AmmoRecord with Value, or null if validation fails.
    /// </summary>
    public AmmoRecord? ReadRuntimeAmmo(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x29)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + AmmoStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[AmmoStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, AmmoStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, AmmoValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        // Read world model path via BSStringT at TESModel offset (+80)
        var modelPath = _context.ReadBSStringT(offset, WeapModelPathOffset);

        return new AmmoRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = (uint)value,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended misc item data from a runtime TESObjectMISC struct.
    ///     Returns a MiscItemRecord with Value/Weight, or null if validation fails.
    /// </summary>
    public MiscItemRecord? ReadRuntimeMiscItem(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x1F)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + MiscStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[MiscStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, MiscStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, MiscValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, MiscWeightOffset, 0, 500);

        // Read model path via BSStringT at TESModel offset (+80)
        var modelPath = _context.ReadBSStringT(offset, WeapModelPathOffset);

        return new MiscItemRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = value,
            Weight = weight,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended key data from a runtime TESKey struct.
    ///     TESKey inherits TESObjectMISC — same layout, same offsets.
    ///     Returns a KeyRecord with Value/Weight, or null if validation fails.
    /// </summary>
    public KeyRecord? ReadRuntimeKey(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x2E)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + MiscStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[MiscStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, MiscStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, MiscValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, MiscWeightOffset, 0, 500);

        // Read model path via BSStringT at TESModel offset (+80)
        var modelPath = _context.ReadBSStringT(offset, WeapModelPathOffset);

        return new KeyRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = value,
            Weight = weight,
            ModelPath = modelPath,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended container data from a runtime TESObjectCONT struct.
    ///     Returns a ContainerRecord with weight, contents, and flags.
    /// </summary>
    public ContainerRecord? ReadRuntimeContainer(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x1B)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + ContStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[ContStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, ContStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read flags
        var flags = buffer[ContFlagsOffset];

        // Read model path
        var modelPath = _context.ReadBSStringT(offset, ContModelPathOffset);

        // Read script pointer
        var scriptFormId = _context.FollowPointerToFormId(buffer, ContScriptPtrOffset);

        // Read container contents using same pattern as NPC inventory
        var contents = ReadContainerContents(buffer);

        return new ContainerRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Flags = flags,
            Contents = contents,
            ModelPath = modelPath,
            Script = scriptFormId,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended consumable data from a runtime TESObjectALCH struct.
    ///     Returns a ConsumableRecord with Value/Weight, or null if validation fails.
    /// </summary>
    public ConsumableRecord? ReadRuntimeConsumable(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x2F)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + AlchStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[AlchStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, AlchStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, AlchWeightOffset, 0, 500);

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, AlchValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        return new ConsumableRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = (uint)value,
            Weight = weight,
            Offset = offset,
            IsBigEndian = true
        };
    }

    #region Weapon Helper Methods

    private (int Value, int Health, float Weight, short Damage, byte ClipSize)
        ReadWeaponBaseClassFields(byte[] buffer)
    {
        var value = RuntimeMemoryContext.ReadInt32BE(buffer, WeapValueOffset);
        var health = RuntimeMemoryContext.ReadInt32BE(buffer, WeapHealthOffset);
        var weight = BinaryUtils.ReadFloatBE(buffer, WeapWeightOffset);
        var damage = (short)BinaryUtils.ReadUInt16BE(buffer, WeapDamageOffset);
        var clipSize = buffer[WeapClipRoundsOffset];

        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        if (health < 0 || health > 100000)
        {
            health = 0;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(weight) || weight < 0 || weight > 500)
        {
            weight = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            damage = 0;
        }

        return (value, health, weight, damage, clipSize);
    }

    private (Enums.WeaponType WeaponType, uint AnimationType, float Speed, float Reach,
        float MinSpread, float Spread, float MinRange, float MaxRange,
        byte VatsChance, float ActionPoints, float ShotsPerSec) ReadWeaponCombatFields(byte[] buffer)
    {
        // animationType is stored as uint8 at the first byte of a 4-byte aligned field
        var animTypeByte = buffer[WeapAnimTypeOffset];
        var animationType = animTypeByte <= 20 ? animTypeByte : 0u;

        var speed = BinaryUtils.ReadFloatBE(buffer, WeapSpeedOffset);
        var reach = BinaryUtils.ReadFloatBE(buffer, WeapReachOffset);

        if (!RuntimeMemoryContext.IsNormalFloat(speed) || speed < 0 || speed > 100)
        {
            speed = 1.0f;
        }

        if (!RuntimeMemoryContext.IsNormalFloat(reach) || reach < 0 || reach > 1000)
        {
            reach = 0;
        }

        // Animation type byte maps directly to WeaponType enum
        var weaponType = animTypeByte <= 11 ? (WeaponType)animTypeByte : 0;

        var minSpread = RuntimeMemoryContext.ReadValidatedFloat(buffer, WeapDataStart + DnamMinSpreadRelOffset, 0, 1000);
        var spread = RuntimeMemoryContext.ReadValidatedFloat(buffer, WeapDataStart + DnamSpreadRelOffset, 0, 1000);
        var minRange = RuntimeMemoryContext.ReadValidatedFloat(buffer, WeapDataStart + DnamMinRangeRelOffset, 0, 100000);
        var maxRange = RuntimeMemoryContext.ReadValidatedFloat(buffer, WeapDataStart + DnamMaxRangeRelOffset, 0, 100000);
        var actionPoints = RuntimeMemoryContext.ReadValidatedFloat(buffer, WeapDataStart + DnamActionPointsRelOffset, 0, 1000);
        var shotsPerSec = RuntimeMemoryContext.ReadValidatedFloat(buffer, WeapDataStart + DnamShotsPerSecRelOffset, 0, 1000);

        var vatsChance = buffer[WeapDataStart + DnamVatsChanceRelOffset];
        if (vatsChance > 100)
        {
            vatsChance = 0;
        }

        return (weaponType, animationType, speed, reach, minSpread, spread,
            minRange, maxRange, vatsChance, actionPoints, shotsPerSec);
    }

    private (short Damage, float Chance) ReadWeaponCriticalFields(byte[] buffer)
    {
        var damage = (short)BinaryUtils.ReadUInt16BE(buffer, WeapCritDamageOffset);
        var chance = BinaryUtils.ReadFloatBE(buffer, WeapCritChanceOffset);

        if (!RuntimeMemoryContext.IsNormalFloat(chance) || chance < 0 || chance > 100)
        {
            chance = 0;
        }

        if (damage < 0 || damage > 10000)
        {
            damage = 0;
        }

        return (damage, chance);
    }

    #endregion

    #region Container Helper Methods

    /// <summary>
    ///     Read container contents from TESContainer tList at +120/+124.
    ///     Reuses the same ContainerObject reading logic as NPC inventory.
    /// </summary>
    private List<InventoryItem> ReadContainerContents(byte[] buffer)
    {
        var items = new List<InventoryItem>();

        // Read inline first node
        var firstDataPtr = BinaryUtils.ReadUInt32BE(buffer, ContContentsDataOffset);
        var firstNextPtr = BinaryUtils.ReadUInt32BE(buffer, ContContentsNextOffset);

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
