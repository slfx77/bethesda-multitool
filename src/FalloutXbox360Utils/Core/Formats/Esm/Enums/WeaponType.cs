namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     Weapon animation type from WEAP DNAM byte 0 / GECK GetWeaponAnimType.
///     This is the runtime-facing humanoid animation family used for holster/draw semantics.
/// </summary>
public enum WeaponType : byte
{
    HandToHandMelee = 0,
    OneHandMelee = 1,
    TwoHandMelee = 2,
    OneHandPistol = 3,
    OneHandPistolEnergy = 4,
    TwoHandRifle = 5,
    TwoHandAutomatic = 6,
    TwoHandRifleEnergy = 7,
    TwoHandHandle = 8,
    TwoHandLauncher = 9,
    OneHandGrenade = 10,
    OneHandMine = 11,
    OneHandLunchboxMine = 12,
    OneHandThrown = 13
}
