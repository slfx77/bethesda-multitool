using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     A weapon's mod slot — defines what IMOD can be attached and what effect it has.
///     Each weapon has up to 3 mod slots defined in its DNAM data.
/// </summary>
public record WeaponModSlot
{
    /// <summary>Slot index (1, 2, or 3).</summary>
    public int SlotIndex { get; init; }

    /// <summary>The mod action type (what stat is modified).</summary>
    public WeaponModAction Action { get; init; }

    /// <summary>Primary effect value (e.g., +3 damage, +5 clip size).</summary>
    public float Value { get; init; }

    /// <summary>Secondary effect value (used by some action types).</summary>
    public float ValueTwo { get; init; }

    /// <summary>FormID of the IMOD record that fits this slot (null if no mod assigned).</summary>
    public uint? ModFormId { get; init; }

    /// <summary>Human-readable action name.</summary>
    public string ActionName => Action switch
    {
        WeaponModAction.None => "None",
        WeaponModAction.IncreaseDamage => "Increase Damage",
        WeaponModAction.IncreaseClipSize => "Increase Clip Size",
        WeaponModAction.DecreaseSpread => "Decrease Spread",
        WeaponModAction.DecreaseWeight => "Decrease Weight",
        WeaponModAction.RegenerateAmmo => "Regenerate Ammo",
        WeaponModAction.DecreaseEquipTime => "Decrease Equip Time",
        WeaponModAction.IncreaseRateOfFire => "Increase Rate of Fire",
        WeaponModAction.IncreaseProjectileSpeed => "Increase Projectile Speed",
        WeaponModAction.IncreaseMaxCondition => "Increase Max Condition",
        WeaponModAction.Silence => "Silence",
        WeaponModAction.SplitBeam => "Split Beam",
        WeaponModAction.VatsBonus => "VATS Bonus",
        WeaponModAction.IncreaseZoom => "Increase Zoom",
        _ => $"Unknown ({(uint)Action})"
    };
}
