namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Layouts;

/// <summary>
///     Struct offset constants for item-related runtime types (weapons, armor, ammo, etc.)
///     derived from the Proto Debug PDB, adjusted by build-specific shift passed to the constructor.
/// </summary>
internal sealed class RuntimeItemLayouts
{
    private readonly int _s;

    internal RuntimeItemLayouts(int pdbShift)
    {
        _s = pdbShift;
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
    internal const int DnamMinSpreadRelOffset = 16;
    internal const int DnamSpreadRelOffset = 20;
    internal const int DnamProjectileRelOffset = 36;
    internal const int DnamVatsChanceRelOffset = 40;
    internal const int DnamMinRangeRelOffset = 44;
    internal const int DnamMaxRangeRelOffset = 48;
    internal const int DnamActionPointsRelOffset = 68;
    internal const int DnamShotsPerSecRelOffset = 88;

    internal int WeapCritDamageOffset => 440 + _s;
    internal int WeapCritChanceOffset => 444 + _s;
    internal int WeapPickupSoundOffset => 236 + _s;
    internal int WeapPutdownSoundOffset => 240 + _s;
    internal int WeapFireSound3DOffset => 532 + _s;
    internal int WeapFireSoundDistOffset => 536 + _s;
    internal int WeapFireSound2DOffset => 540 + _s;
    internal int WeapDryFireSoundOffset => 548 + _s;
    internal int WeapIdleSoundOffset => 556 + _s;
    internal int WeapEquipSoundOffset => 560 + _s;
    internal int WeapUnequipSoundOffset => 564 + _s;
    internal int WeapImpactDataSetOffset => 568 + _s;
    internal int WeapEmbeddedWeaponNodeOffset => 876 + _s;

    #endregion

    #region TESObjectARMO — PDB size 400, Debug dump 404, Release dump 416

    internal int ArmoStructSize => 400 + _s;
    internal int ArmoValueOffset => 92 + _s;
    internal int ArmoWeightOffset => 100 + _s;
    internal int ArmoHealthOffset => 108 + _s;
    internal int ArmoBipedFlagsOffset => 116 + _s; // TESBipedModelForm.iBipedObjectSlots
    internal int ArmoRatingOffset => 376 + _s; // OBJ_ARMO.sRating (DamageResistance, UInt16)
    internal int ArmoDamageThresholdOffset => 380 + _s; // OBJ_ARMO.fDamageThreshold (Float32)

    #endregion

    #region TESObjectAMMO — PDB size ~220, Debug dump ~224, Release dump 236

    internal int AmmoStructSize => 220 + _s;
    internal int AmmoValueOffset => 124 + _s;

    #endregion

    #region TESObjectALCH — PDB size ~216, Debug dump ~220, Release dump 232

    internal int AlchStructSize => 216 + _s;
    internal int AlchWeightOffset => 152 + _s;
    internal int AlchValueOffset => 184 + _s;
    internal int AlchFlagsOffset => 188 + _s; // AlchemyItemData.iFlags (byte)

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
