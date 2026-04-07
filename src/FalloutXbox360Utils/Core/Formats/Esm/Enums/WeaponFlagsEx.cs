namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     Extended bit flags from the WEAP DNAM FlagsEx uint32 (offset 56).
///     Per UESP/FNVEdit FNV WEAP DNAM documentation.
/// </summary>
[Flags]
public enum WeaponFlagsEx : uint
{
    None = 0,
    PlayerOnly = 0x01,
    NpcsUseAmmo = 0x02,
    NoJamAfterReload = 0x04,
    OverrideProjectileRange = 0x08,
    MinorCrime = 0x10,
    RangeFixed = 0x20,
    NotUsedInNormalCombat = 0x40,
    OverrideDamageToWeaponMult = 0x80,
    DontUseThirdPersonIsAnimations = 0x100,
    LongBursts = 0x200,
    DontHold = 0x400,
    HasRevBAnimations = 0x800
}
