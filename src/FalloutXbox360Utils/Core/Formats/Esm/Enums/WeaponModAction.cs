namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     Weapon mod action type — defines what stat a mod slot changes when an IMOD is attached.
///     Stored per-slot on TESObjectWEAP in the DNAM subrecord.
/// </summary>
public enum WeaponModAction : uint
{
    None = 0,
    IncreaseDamage = 1,
    IncreaseClipSize = 2,
    DecreaseSpread = 3,
    DecreaseWeight = 4,
    RegenerateAmmo = 5,
    DecreaseEquipTime = 6,
    IncreaseRateOfFire = 7,
    IncreaseProjectileSpeed = 8,
    IncreaseMaxCondition = 9,
    Silence = 10,
    SplitBeam = 11,
    VatsBonus = 12,
    IncreaseZoom = 13
}
