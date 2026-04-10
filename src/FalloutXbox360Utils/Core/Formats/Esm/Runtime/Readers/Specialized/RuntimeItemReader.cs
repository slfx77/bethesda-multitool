using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Parsing;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

/// <summary>
///     Reads item-related runtime structs (weapons, armor, ammo, misc items, keys,
///     consumables) from Xbox 360 memory dumps. Container reading is delegated to
///     <see cref="RuntimeContainerReader" />. Weapon field parsing is delegated to
///     <see cref="RuntimeItemFieldHelpers" />.
/// </summary>
internal sealed class RuntimeItemReader(
    RuntimeMemoryContext context,
    RuntimeWeaponSoundProbeResult? weaponSoundProbe = null)
{
    private readonly RuntimeMemoryContext _context = context;

    // Build-specific offset shift: Proto Debug PDB + _s = actual dump offset.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    // Fine-grained shift on top of the chosen layout (for builds that drift slightly).
    private readonly int _weaponSoundShift = weaponSoundProbe?.FineShift ?? 0;

    // Selected weapon sound layout variant (V1 = early FO3-derived, V2 = FNV).
    // The probe picks whichever pattern-matches better; default to V2 if no probe result.
    private readonly RuntimeWeaponSoundLayoutVariant _weaponSoundVariant =
        weaponSoundProbe?.Variant ?? RuntimeWeaponSoundLayoutVariant.V2;

    // Delegate container reading to dedicated class.
    private RuntimeContainerReader? _containerReader;

    // Weapon/container field helpers (share layouts with this reader).
    private RuntimeItemFieldHelpers? _fieldHelpers;

    // Shared layouts for field reading.
    private RuntimeItemLayouts? _layouts;

    private RuntimeItemLayouts Layouts => _layouts ??=
        new RuntimeItemLayouts(_s, _weaponSoundShift, _weaponSoundVariant);

    private RuntimeItemFieldHelpers FieldHelpers => _fieldHelpers ??= new RuntimeItemFieldHelpers(_context, Layouts);
    private RuntimeContainerReader ContainerReader => _containerReader ??= new RuntimeContainerReader(_context);

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
        if (offset + Layouts.WeapStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[Layouts.WeapStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, Layouts.WeapStructSize);
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
        var baseFields = FieldHelpers.ReadWeaponBaseClassFields(buffer);
        var combatFields = FieldHelpers.ReadWeaponCombatFields(buffer);
        var critFields = FieldHelpers.ReadWeaponCriticalFields(buffer);
        var modSlotFields = FieldHelpers.ReadWeaponModSlots(buffer);
        var phase3 = FieldHelpers.ReadWeaponPhase3Fields(buffer);
        var vatsAttack = FieldHelpers.ReadWeaponVatsAttack(buffer);
        var modelVariants = FieldHelpers.ReadWeaponModelVariants(buffer, offset);

        // Follow ammo pointer to get ammo FormID
        var ammoFormId = _context.FollowPointerToFormId(buffer, Layouts.WeapAmmoPtrOffset);

        // Read model path via BSStringT at TESModel offset
        var modelPath = _context.ReadBSStringT(offset, Layouts.WeapModelPathOffset);
        var embeddedWeaponNode = ReadBSStringTSafe(offset, Layouts.WeapEmbeddedWeaponNodeOffset);

        // Read sound pointers (TESSound* at various offsets). Offsets may be -1 for fields
        // that don't exist in the V1 (FO3-derived) layout — those reads safely return null.
        var pickupSound = _context.FollowPointerToFormId(buffer, Layouts.WeapPickupSoundOffset);
        var putdownSound = _context.FollowPointerToFormId(buffer, Layouts.WeapPutdownSoundOffset);
        var fireSound3D = FollowPointerSafe(buffer, Layouts.WeapFireSound3DOffset);
        var fireSoundDist = FollowPointerSafe(buffer, Layouts.WeapFireSoundDistOffset);
        var fireSound2D = FollowPointerSafe(buffer, Layouts.WeapFireSound2DOffset);
        var attackLoopSound = FollowPointerSafe(buffer, Layouts.WeapAttackLoopOffset);
        var dryFireSound = FollowPointerSafe(buffer, Layouts.WeapDryFireSoundOffset);
        var meleeBlockSound = FollowPointerSafe(buffer, Layouts.WeapMeleeBlockSoundOffset);
        var idleSound = FollowPointerSafe(buffer, Layouts.WeapIdleSoundOffset);
        var equipSound = FollowPointerSafe(buffer, Layouts.WeapEquipSoundOffset);
        var unequipSound = FollowPointerSafe(buffer, Layouts.WeapUnequipSoundOffset);
        var modSilenced3D = FollowPointerSafe(buffer, Layouts.WeapModSilencedSound3DOffset);
        var modSilencedDist = FollowPointerSafe(buffer, Layouts.WeapModSilencedSoundDistOffset);
        var modSilenced2D = FollowPointerSafe(buffer, Layouts.WeapModSilencedSound2DOffset);
        var impactDataSet = FollowPointerSafe(buffer, Layouts.WeapImpactDataSetOffset);

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
            NumProjectiles = combatFields.NumProjectiles,
            AmmoPerShot = combatFields.AmmoPerShot,
            Skill = combatFields.Skill,
            AmmoFormId = ammoFormId,
            ProjectileFormId = _context.FollowPointerToFormId(buffer,
                Layouts.WeapDataStart + RuntimeItemLayouts.DnamProjectileRelOffset),
            CriticalDamage = critFields.Damage,
            CriticalChance = critFields.Chance,
            CriticalEffectFormId = critFields.EffectFormId,
            ModelPath = modelPath,
            EmbeddedWeaponNode = embeddedWeaponNode,
            Bounds = ReadBounds(buffer),
            PickupSoundFormId = pickupSound,
            PutdownSoundFormId = putdownSound,
            FireSound3DFormId = fireSound3D,
            FireSoundDistFormId = fireSoundDist,
            FireSound2DFormId = fireSound2D,
            AttackLoopSoundFormId = attackLoopSound,
            DryFireSoundFormId = dryFireSound,
            MeleeBlockSoundFormId = meleeBlockSound,
            IdleSoundFormId = idleSound,
            EquipSoundFormId = equipSound,
            UnequipSoundFormId = unequipSound,
            ModSilencedSound3DFormId = modSilenced3D,
            ModSilencedSoundDistFormId = modSilencedDist,
            ModSilencedSound2DFormId = modSilenced2D,
            ImpactDataSetFormId = impactDataSet,
            ModSlots = modSlotFields,
            DamageToWeaponMult = phase3.DamageToWeaponMult,
            Resistance = phase3.Resistance,
            IronSightUseMult = phase3.IronSightUseMult,
            AmmoRegenRate = phase3.AmmoRegenRate,
            KillImpulse = phase3.KillImpulse,
            KillImpulseDistance = phase3.KillImpulseDistance,
            SemiAutoFireDelayMin = phase3.SemiAutoMin,
            SemiAutoFireDelayMax = phase3.SemiAutoMax,
            AnimShotsPerSecond = phase3.AnimShotsPerSec,
            AnimReloadTime = phase3.AnimReloadTime,
            AnimJamTime = phase3.AnimJamTime,
            PowerAttackOverrideAnim = phase3.PowerAttackOverride,
            ModReloadClipAnimation = phase3.ModReloadAnim,
            ModFireAnimation = phase3.ModFireAnim,
            CookTimer = phase3.CookTimer,
            RumbleLeftMotor = phase3.RumbleLeft,
            RumbleRightMotor = phase3.RumbleRight,
            RumbleDuration = phase3.RumbleDuration,
            RumblePattern = phase3.RumblePattern,
            RumbleWavelength = phase3.RumbleWavelength,
            VatsAttack = vatsAttack,
            ModelVariants = modelVariants,
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
        if (offset + Layouts.ArmoStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[Layouts.ArmoStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, Layouts.ArmoStructSize);
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

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, Layouts.ArmoValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, Layouts.ArmoWeightOffset, 0, 500);

        var health = RuntimeMemoryContext.ReadInt32BE(buffer, Layouts.ArmoHealthOffset);
        if (health < 0 || health > 100000)
        {
            health = 0;
        }

        // BipedFlags: TESBipedModelForm.iBipedObjectSlots (uint32 at offset 116)
        var bipedFlags = BinaryUtils.ReadUInt32BE(buffer, Layouts.ArmoBipedFlagsOffset);

        // DamageResistance: OBJ_ARMO.sRating (uint16 at offset 376)
        var damageResistance = (int)BinaryUtils.ReadUInt16BE(buffer, Layouts.ArmoRatingOffset);

        // DamageThreshold: OBJ_ARMO.fDamageThreshold (float32 at offset 380)
        var damageThreshold =
            RuntimeMemoryContext.ReadValidatedFloat(buffer, Layouts.ArmoDamageThresholdOffset, 0, 100);

        // EquipmentType: BGSEquipType.eEquipType (int32 enum at offset 344)
        var equipTypeValue = RuntimeMemoryContext.ReadInt32BE(buffer, Layouts.ArmoEquipTypeOffset);
        var equipmentType = equipTypeValue >= -1 && equipTypeValue <= 13
            ? (EquipmentType)equipTypeValue
            : EquipmentType.None;

        // Model paths from TESBipedModelForm.bipedModel / worldModel (TESModelTextureSwap.cModel at +4)
        var modelPath = _context.ReadBSStringT(offset, Layouts.ArmoBipedModelPathOffset)
                        ?? _context.ReadBSStringT(offset, Layouts.ArmoWorldModelPathOffset);

        return new ArmorRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = value,
            Weight = weight,
            Health = health,
            BipedFlags = bipedFlags,
            DamageResistance = damageResistance,
            DamageThreshold = damageThreshold,
            EquipmentType = equipmentType,
            ModelPath = modelPath,
            Bounds = ReadBounds(buffer),
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
        if (offset + Layouts.AmmoStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[Layouts.AmmoStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, Layouts.AmmoStructSize);
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

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, Layouts.AmmoValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        // Read world model path via BSStringT at TESModel offset (+80)
        var modelPath = _context.ReadBSStringT(offset, Layouts.WeapModelPathOffset);

        // AMMO_DATA: speed (float) + flags (uint32)
        var speed = RuntimeMemoryContext.ReadValidatedFloat(buffer, Layouts.AmmoSpeedOffset, 0, 100000);
        var flags = BinaryUtils.ReadUInt32BE(buffer, Layouts.AmmoFlagsOffset);
        var clipRounds = buffer[Layouts.AmmoClipRoundsOffset];

        // AMMO_DATA_NV: pProjectile (BGSProjectile*)
        var projectileFormId = _context.FollowPointerToFormId(buffer, Layouts.AmmoProjectilePtrOffset);

        return new AmmoRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = (uint)value,
            Speed = speed,
            Flags = (byte)flags,
            ClipRounds = clipRounds,
            ProjectileFormId = projectileFormId,
            ModelPath = modelPath,
            Bounds = ReadBounds(buffer),
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
        if (offset + Layouts.MiscStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[Layouts.MiscStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, Layouts.MiscStructSize);
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

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, Layouts.MiscValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, Layouts.MiscWeightOffset, 0, 500);

        // Read model path via BSStringT at TESModel offset (+80)
        var modelPath = _context.ReadBSStringT(offset, Layouts.WeapModelPathOffset);

        return new MiscItemRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = value,
            Weight = weight,
            ModelPath = modelPath,
            Bounds = ReadBounds(buffer),
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
        if (offset + Layouts.MiscStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[Layouts.MiscStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, Layouts.MiscStructSize);
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

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, Layouts.MiscValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, Layouts.MiscWeightOffset, 0, 500);

        // Read model path via BSStringT at TESModel offset (+80)
        var modelPath = _context.ReadBSStringT(offset, Layouts.WeapModelPathOffset);

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
    ///     Delegates container reading to <see cref="RuntimeContainerReader" />.
    /// </summary>
    public ContainerRecord? ReadRuntimeContainer(RuntimeEditorIdEntry entry)
    {
        return ContainerReader.ReadRuntimeContainer(entry);
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
        if (offset + Layouts.AlchStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[Layouts.AlchStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, Layouts.AlchStructSize);
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

        var weight = RuntimeMemoryContext.ReadValidatedFloat(buffer, Layouts.AlchWeightOffset, 0, 500);

        var value = RuntimeMemoryContext.ReadInt32BE(buffer, Layouts.AlchValueOffset);
        if (value < 0 || value > 1000000)
        {
            value = 0;
        }

        // ENIT flags: AlchemyItemData.iFlags (byte at offset 188)
        var flags = (uint)buffer[Layouts.AlchFlagsOffset];

        // Addiction: SpellItem* pointer → follow to FormID
        var addictionFormId = _context.FollowPointerToFormId(buffer, Layouts.AlchAddictionPtrOffset);
        var addictionChance = RuntimeMemoryContext.ReadValidatedFloat(
            buffer, Layouts.AlchAddictionChanceOffset, 0, 1);

        // Walk EffectItem list (BSSimpleList<EffectItem*> at offset 80)
        var effects = WalkEffectItemList(buffer, Layouts.AlchEffectListOffset);

        return new ConsumableRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Value = (uint)value,
            Weight = weight,
            Flags = flags,
            AddictionFormId = addictionFormId,
            AddictionChance = addictionChance,
            Effects = effects,
            Bounds = ReadBounds(buffer),
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read TESBoundObject.BoundData (12 bytes = 6 × int16) from a buffer.
    ///     Returns null if bounds are all zero (uninitialized).
    /// </summary>
    private ObjectBounds? ReadBounds(byte[] buffer)
    {
        var off = Layouts.BoundsOffset;
        if (off + 12 > buffer.Length) return null;
        var bounds = RecordParserContext.ReadObjectBounds(
            buffer.AsSpan(off, 12), true);
        return bounds is { X1: 0, Y1: 0, Z1: 0, X2: 0, Y2: 0, Z2: 0 } ? null : bounds;
    }

    /// <summary>
    ///     Walk a BSSimpleList of EffectItem* pointers (same structure as SPEL/ENCH effects).
    ///     Each EffectItem is 24 bytes: pSetting(4) + magnitude(4) + area(4) + duration(4) + type(4) + actorValue(4).
    /// </summary>
    private List<EnchantmentEffect> WalkEffectItemList(byte[] structBuffer, int listOffset)
    {
        var result = new List<EnchantmentEffect>();

        var headVa = BinaryUtils.ReadUInt32BE(structBuffer, listOffset);
        if (headVa == 0 || !_context.IsValidPointer(headVa))
        {
            return result;
        }

        var visited = new HashSet<uint>();
        var currentVa = headVa;

        for (var i = 0; i < 32; i++)
        {
            if (currentVa == 0 || !visited.Add(currentVa))
            {
                break;
            }

            var nodeFileOffset = _context.VaToFileOffset(currentVa);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuffer = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuffer == null)
            {
                break;
            }

            var itemVa = BinaryUtils.ReadUInt32BE(nodeBuffer);
            var nextVa = BinaryUtils.ReadUInt32BE(nodeBuffer, 4);

            if (itemVa != 0)
            {
                var itemOffset = _context.VaToFileOffset(itemVa);
                if (itemOffset != null)
                {
                    var eiBuf = _context.ReadBytes(itemOffset.Value, 24);
                    if (eiBuf != null)
                    {
                        var settingFormId = _context.FollowPointerVaToFormId(
                            BinaryUtils.ReadUInt32BE(eiBuf));
                        var magnitude = BinaryUtils.ReadFloatBE(eiBuf, 4);
                        var area = BinaryUtils.ReadUInt32BE(eiBuf, 8);
                        var duration = BinaryUtils.ReadUInt32BE(eiBuf, 12);
                        var type = BinaryUtils.ReadUInt32BE(eiBuf, 16);
                        var actorValue = unchecked((int)BinaryUtils.ReadUInt32BE(eiBuf, 20));

                        if (settingFormId is > 0)
                        {
                            result.Add(new EnchantmentEffect
                            {
                                EffectFormId = settingFormId.Value,
                                Magnitude = RuntimeMemoryContext.IsNormalFloat(magnitude) ? magnitude : 0f,
                                Area = area,
                                Duration = duration,
                                Type = type,
                                ActorValue = actorValue
                            });
                        }
                    }
                }
            }

            currentVa = nextVa;
        }

        return result;
    }

    /// <summary>
    ///     Follow a pointer at the given buffer offset, but skip the read if the offset is
    ///     negative — used for fields that don't exist in the V1 (FO3-derived) layout.
    /// </summary>
    private uint? FollowPointerSafe(byte[] buffer, int offset)
    {
        return offset < 0 ? null : _context.FollowPointerToFormId(buffer, offset);
    }

    /// <summary>
    ///     Read a BSStringT, but skip the read if the offset is negative.
    /// </summary>
    private string? ReadBSStringTSafe(long structFileOffset, int relOffset)
    {
        return relOffset < 0 ? null : _context.ReadBSStringT(structFileOffset, relOffset);
    }
}
