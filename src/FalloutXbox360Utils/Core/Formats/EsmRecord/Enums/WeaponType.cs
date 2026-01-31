namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;

/// <summary>
///     Weapon animation type from DNAM subrecord byte 0.
///     This is the reliable weapon classification (both ESM and runtime struct sources).
/// </summary>
public enum WeaponType : byte
{
    HandToHand = 0,
    Melee1H = 1,
    Melee2H = 2,
    Pistol = 3,
    PistolAutomatic = 4,
    Rifle = 5,
    RifleAutomatic = 6,
    Handle = 7,
    Launcher = 8,
    GrenadeThrow = 9,
    LandMine = 10,
    MinePlacement = 11
}
