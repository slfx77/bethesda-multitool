namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     Identifies which weapon mods are applied to a model variant.
///     Each weapon defines a base model and 7 alternates for each combination of installed mods.
/// </summary>
public enum WeaponModCombination
{
    None = 0,
    Mod1 = 1,
    Mod2 = 2,
    Mod3 = 3,
    Mod12 = 12,
    Mod13 = 13,
    Mod23 = 23,
    Mod123 = 123
}
