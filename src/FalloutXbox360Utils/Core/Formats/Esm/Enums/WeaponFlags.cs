namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     Bit flags from the WEAP DNAM Flags byte (offset 12).
///     Per UESP/FNVEdit FNV WEAP DNAM documentation.
/// </summary>
[Flags]
public enum WeaponFlags : byte
{
    None = 0,
    NotPlayable = 0x01,
    Automatic = 0x02,
    HasScope = 0x04,
    CantDrop = 0x08,
    HideBackpack = 0x10,
    EmbeddedWeapon = 0x20,
    DontUseFirstPersonIsAnimations = 0x40,
    NonPlayable = 0x80
}
