using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     A weapon model variant — the world (3rd-person) model and 1st-person STAT pointer
///     used when a specific combination of weapon mods is installed.
/// </summary>
public record WeaponModelVariant
{
    /// <summary>Which mod combination this variant represents.</summary>
    public WeaponModCombination Combination { get; init; }

    /// <summary>3rd-person world model NIF path (from TESModelTextureSwap.cModel).</summary>
    public string? ThirdPersonModelPath { get; init; }

    /// <summary>FormID of the TESObjectSTAT used as the 1st-person model.</summary>
    public uint? FirstPersonObjectFormId { get; init; }

    /// <summary>Human-readable name for the mod combination.</summary>
    public string CombinationName => Combination switch
    {
        WeaponModCombination.None => "Base",
        WeaponModCombination.Mod1 => "Mod 1",
        WeaponModCombination.Mod2 => "Mod 2",
        WeaponModCombination.Mod3 => "Mod 3",
        WeaponModCombination.Mod12 => "Mods 1+2",
        WeaponModCombination.Mod13 => "Mods 1+3",
        WeaponModCombination.Mod23 => "Mods 2+3",
        WeaponModCombination.Mod123 => "Mods 1+2+3",
        _ => Combination.ToString()
    };
}
