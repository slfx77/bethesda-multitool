namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Layouts;

/// <summary>
///     Struct offset constants for item-related runtime types (weapons, armor, ammo, etc.)
///     derived from the Proto Debug PDB, adjusted by build-specific shift passed to the constructor.
/// </summary>
internal sealed class RuntimeItemLayouts
{
    private readonly int _s;

    /// <summary>
    ///     Fine-grained shift applied on top of the chosen weapon sound layout variant.
    ///     Detected by <see cref="Probes.RuntimeWeaponSoundProbe" />. Most builds use 0;
    ///     used for minor build drift within a layout variant.
    /// </summary>
    private readonly int _weapSoundShift;

    /// <summary>
    ///     Selected weapon sound layout variant.
    ///     V2 = FNV-era builds (14 sound fields). V1 = early FO3-derived builds (7 sound
    ///     fields, no Distant / AttackLoop / MeleeBlock / Idle / ModSilenced).
    /// </summary>
    private readonly RuntimeWeaponSoundLayoutVariant _weapSoundVariant;

    internal RuntimeItemLayouts(
        int pdbShift,
        int weaponSoundShift = 0,
        RuntimeWeaponSoundLayoutVariant weaponSoundVariant = RuntimeWeaponSoundLayoutVariant.V2)
    {
        _s = pdbShift;
        _weapSoundShift = weaponSoundShift;
        _weapSoundVariant = weaponSoundVariant;
    }

    /// <summary>
    ///     TESBoundObject.BoundData offset — 12 bytes (6 × int16).
    ///     Proto Debug PDB offset 36, all TESBoundObject-derived types.
    /// </summary>
    internal int BoundsOffset => 36 + _s;

    #region TESObjectWEAP — PDB size 908, Debug dump 912, Release dump 924

    internal int WeapStructSize => 908 + _s;
    internal int WeapModelPathOffset => 64 + _s;
    internal int WeapValueOffset => 136 + _s;
    internal int WeapWeightOffset => 144 + _s;
    internal int WeapHealthOffset => 152 + _s;
    internal int WeapDamageOffset => 160 + _s;
    internal int WeapAmmoPtrOffset => 168 + _s;
    internal int WeapClipRoundsOffset => 176 + _s;
    internal int WeapDataStart => 244 + _s;
    internal int WeapAnimTypeOffset => 244 + _s;
    internal int WeapSpeedOffset => 248 + _s;
    internal int WeapReachOffset => 252 + _s;

    // DNAM relative offsets — fixed within the DNAM data block, not TESForm-derived
    internal const int DnamAmmoPerShotRelOffset = 14;
    internal const int DnamMinSpreadRelOffset = 16;
    internal const int DnamSpreadRelOffset = 20;
    internal const int DnamProjectileRelOffset = 36;
    internal const int DnamVatsChanceRelOffset = 40;
    internal const int DnamNumProjectilesRelOffset = 42;
    internal const int DnamMinRangeRelOffset = 44;
    internal const int DnamMaxRangeRelOffset = 48;
    internal const int DnamActionPointsRelOffset = 68;
    internal const int DnamShotsPerSecRelOffset = 64; // PDB OBJ_WEAP.fShotsPerSec
    internal const int DnamSkillRelOffset = 104;

    // Phase 3 — additional OBJ_WEAP fields verified against PDB OBJ_WEAP type definition.
    internal const int DnamRumbleLeftMotorRelOffset = 72;
    internal const int DnamRumbleRightMotorRelOffset = 76;
    internal const int DnamRumbleDurationRelOffset = 80;
    internal const int DnamDamageToWeaponMultRelOffset = 84;
    internal const int DnamAnimShotsPerSecondRelOffset = 88;
    internal const int DnamAnimReloadTimeRelOffset = 92;
    internal const int DnamAnimJamTimeRelOffset = 96;
    internal const int DnamRumblePatternRelOffset = 108;
    internal const int DnamRumbleWavelengthRelOffset = 112;
    internal const int DnamResistanceRelOffset = 120;
    internal const int DnamIronSightUseMultRelOffset = 124;
    internal const int DnamSemiAutoDelayMinRelOffset = 128;
    internal const int DnamSemiAutoDelayMaxRelOffset = 132;
    internal const int DnamCookTimerRelOffset = 136;
    internal const int DnamPowerAttackOverrideRelOffset = 164;
    internal const int DnamModReloadClipAnimationRelOffset = 172;
    internal const int DnamModFireAnimationRelOffset = 173;
    internal const int DnamAmmoRegenRateRelOffset = 176;
    internal const int DnamKillImpulseRelOffset = 180;
    internal const int DnamKillImpulseDistanceRelOffset = 196;

    // Mod action fields (FNV DNAM-relative offsets)
    internal const int DnamModActionOneRelOffset = 140;
    internal const int DnamModActionTwoRelOffset = 144;
    internal const int DnamModActionThreeRelOffset = 148;
    internal const int DnamModValueOneRelOffset = 152;
    internal const int DnamModValueTwoRelOffset = 156;
    internal const int DnamModValueThreeRelOffset = 160;
    internal const int DnamModValueTwoOneRelOffset = 184;
    internal const int DnamModValueTwoTwoRelOffset = 188;
    internal const int DnamModValueTwoThreeRelOffset = 192;

    // pModObject pointers (V2 only — V1 has no weapon mods)
    internal int WeapModObjectOneOffset => 864 + _s + _weapSoundShift;
    internal int WeapModObjectTwoOffset => 868 + _s + _weapSoundShift;
    internal int WeapModObjectThreeOffset => 872 + _s + _weapSoundShift;

    internal int WeapCritDamageOffset => 440 + _s;
    internal int WeapCritChanceOffset => 444 + _s;
    internal int WeapCritEffectPtrOffset => 452 + _s;

    // Pickup/Putdown sounds — BGSPickupPutdownSounds, before the variable-size data block.
    // Stable across all observed builds.
    internal int WeapPickupSoundOffset => 236 + _s; // PDB 252
    internal int WeapPutdownSoundOffset => 240 + _s; // PDB 256

    // TESObjectWEAP sound block.
    // V2 (FNV — 14 fields, struct ~924 bytes): probed empirically against the 10mm Pistol.
    //   Fire3D@548, FireDist@552, Fire2D@556, AttackLoop@560, DryFire@564, MeleeBlock@568,
    //   Idle@572, Equip@576, Unequip@580, ModSilenced@584/588/592, ImpactData@596,
    //   p1stPersonObject@600. (PDB-style file offsets relative to struct start.)
    // V1 (FO3-derived early builds — 7 fields, smaller struct): no Distant / AttackLoop /
    //   MeleeBlock / Idle / ModSilenced fields. Layout:
    //   Fire3D@548, Fire2D@552, DryFire@560, Equip@572, Unequip@576, ImpactData@588,
    //   p1stPersonObject@592.
    // Both anchor on Fire3D = 548 (code base 532 + _s 16).
    private bool IsV1 => _weapSoundVariant == RuntimeWeaponSoundLayoutVariant.V1;

    internal int WeapFireSound3DOffset => 532 + _s + _weapSoundShift;

    internal int WeapFireSoundDistOffset =>
        IsV1 ? -1 : 536 + _s + _weapSoundShift; // V1 has no Distant slot

    internal int WeapFireSound2DOffset =>
        IsV1 ? 536 + _s + _weapSoundShift : 540 + _s + _weapSoundShift;

    internal int WeapAttackLoopOffset =>
        IsV1 ? -1 : 544 + _s + _weapSoundShift; // V1 has no AttackLoop slot

    internal int WeapDryFireSoundOffset =>
        IsV1 ? 544 + _s + _weapSoundShift : 548 + _s + _weapSoundShift;

    internal int WeapMeleeBlockSoundOffset =>
        IsV1 ? -1 : 552 + _s + _weapSoundShift; // V1 has no MeleeBlock slot

    internal int WeapIdleSoundOffset =>
        IsV1 ? -1 : 556 + _s + _weapSoundShift; // V1 has no Idle slot

    internal int WeapEquipSoundOffset =>
        IsV1 ? 556 + _s + _weapSoundShift : 560 + _s + _weapSoundShift;

    internal int WeapUnequipSoundOffset =>
        IsV1 ? 560 + _s + _weapSoundShift : 564 + _s + _weapSoundShift;

    internal int WeapModSilencedSound3DOffset =>
        IsV1 ? -1 : 568 + _s + _weapSoundShift; // V1 has no ModSilenced slots

    internal int WeapModSilencedSoundDistOffset =>
        IsV1 ? -1 : 572 + _s + _weapSoundShift;

    internal int WeapModSilencedSound2DOffset =>
        IsV1 ? -1 : 576 + _s + _weapSoundShift;

    internal int WeapImpactDataSetOffset =>
        IsV1 ? 572 + _s + _weapSoundShift : 580 + _s + _weapSoundShift;

    internal int WeapEmbeddedWeaponNodeOffset =>
        IsV1 ? -1 : 852 + _s + _weapSoundShift; // V1 doesn't have embedded weapon node

    // VATS attack data (V2 only — added by FNV)
    internal int WeapVatsAttackNameOffset =>
        IsV1 ? -1 : 864 + _s + _weapSoundShift;
    internal int WeapVatsDataOffset =>
        IsV1 ? -1 : 872 + _s + _weapSoundShift;

    // Modded model variants (V2 only)
    internal int Weap1stPersonObjectOffset =>
        IsV1 ? 576 + _s + _weapSoundShift : 584 + _s + _weapSoundShift;
    internal int WeapWorldModelMod1Offset =>
        IsV1 ? -1 : 616 + _s + _weapSoundShift;

    #endregion

    #region TESObjectARMO — PDB size 400, Debug dump 404, Release dump 416

    internal int ArmoStructSize => 400 + _s;
    internal int ArmoBipedModelPathOffset => 144 + _s; // TESBipedModelForm.bipedModel(+140).cModel(+4) BSStringT
    internal int ArmoWorldModelPathOffset => 208 + _s; // TESBipedModelForm.worldModel(+204).cModel(+4) BSStringT
    internal int ArmoValueOffset => 92 + _s;
    internal int ArmoWeightOffset => 100 + _s;
    internal int ArmoHealthOffset => 108 + _s;
    internal int ArmoBipedFlagsOffset => 116 + _s; // TESBipedModelForm.iBipedObjectSlots
    internal int ArmoEquipTypeOffset => 344 + _s; // BGSEquipType.eEquipType (Int32 enum)
    internal int ArmoRatingOffset => 376 + _s; // OBJ_ARMO.sRating (DamageResistance, UInt16)
    internal int ArmoDamageThresholdOffset => 380 + _s; // OBJ_ARMO.fDamageThreshold (Float32)

    #endregion

    #region TESObjectAMMO — PDB size ~220, Debug dump ~224, Release dump 236

    internal int AmmoStructSize => 220 + _s;
    internal int AmmoValueOffset => 124 + _s;
    internal int AmmoClipRoundsOffset => 132 + _s; // TESAmmo.cClipRounds (uint8)
    internal int AmmoSpeedOffset => 168 + _s; // AMMO_DATA.fSpeed (float32, first field)
    internal int AmmoFlagsOffset => 172 + _s; // AMMO_DATA.iFlags (uint32, after speed)
    internal int AmmoProjectilePtrOffset => 176 + _s; // AMMO_DATA_NV.pProjectile (BGSProjectile*)

    #endregion

    #region TESObjectALCH — PDB size ~216, Debug dump ~220, Release dump 232

    internal int AlchStructSize => 216 + _s;
    internal int AlchEffectListOffset => 80 + _s; // BSSimpleList<EffectItem*> (inherited from EffectItemList)
    internal int AlchWeightOffset => 152 + _s;
    internal int AlchValueOffset => 184 + _s;
    internal int AlchFlagsOffset => 188 + _s; // AlchemyItemData.iFlags (byte)
    internal int AlchAddictionPtrOffset => 192 + _s; // AlchemyItemData.pAddiction (SpellItem*)
    internal int AlchAddictionChanceOffset => 196 + _s; // AlchemyItemData.fAddictionChance (float32)

    #endregion

    #region TESObjectMISC / TESKey — PDB size 172, Debug dump 176, Release dump 188

    internal int MiscStructSize => 172 + _s;
    internal int MiscValueOffset => 120 + _s;
    internal int MiscWeightOffset => 128 + _s;

    #endregion

    #region TESObjectCONT — PDB size 156, Debug dump 160, Release dump 172

    internal int ContStructSize => 156 + _s;
    internal int ContModelPathOffset => 64 + _s;
    internal int ContScriptPtrOffset => 108 + _s; // TESScriptableForm::pFormScript (base+104, field+4)
    internal int ContContentsDataOffset => 52 + _s;
    internal int ContContentsNextOffset => 56 + _s;
    internal int ContFlagsOffset => 124 + _s;

    #endregion
}
