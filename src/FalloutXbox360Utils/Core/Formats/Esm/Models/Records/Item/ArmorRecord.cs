using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Armor from ESM/memory dump.
///     Aggregates data from ARMO main record header, DATA (12 bytes), DNAM (12 bytes),
///     BMDT (8 bytes), ETYP (4 bytes).
/// </summary>
public record ArmorRecord
{
    /// <summary>FormID of the armor record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (12 bytes): Value, Health, Weight
    /// <summary>Base value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Armor health/condition.</summary>
    public int Health { get; init; }

    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    // DNAM subrecord (12 bytes): DR (int16), DT (float)
    /// <summary>Damage Threshold (DT) — the primary armor stat in Fallout New Vegas.</summary>
    public float DamageThreshold { get; init; }

    /// <summary>Damage Resistance (DR) — deprecated in FNV, typically 0.</summary>
    public int DamageResistance { get; init; }

    // BMDT subrecord (8 bytes): BipedFlags (uint32), GeneralFlags (uint8)
    /// <summary>Biped slot flags — which body slots the armor occupies (BMDT bytes 0-3).</summary>
    public uint BipedFlags { get; init; }

    /// <summary>General armor flags — Power Armor, Non-Playable, Heavy (BMDT byte 4).</summary>
    public byte GeneralFlags { get; init; }

    // ETYP subrecord (4 bytes): Equipment Type (int32)
    /// <summary>Equipment type — determines hotkey icon category.</summary>
    public EquipmentType EquipmentType { get; init; } = EquipmentType.None;

    /// <summary>Human-readable equipment type name.</summary>
    public string EquipmentTypeName => EquipmentType switch
    {
        EquipmentType.None => "None",
        EquipmentType.BigGuns => "Big Guns",
        EquipmentType.EnergyWeapons => "Energy Weapons",
        EquipmentType.SmallGuns => "Small Guns",
        EquipmentType.MeleeWeapons => "Melee Weapons",
        EquipmentType.UnarmedWeapon => "Unarmed Weapon",
        EquipmentType.ThrownWeapons => "Thrown Weapons",
        EquipmentType.Mine => "Mine",
        EquipmentType.BodyWear => "Body Wear",
        EquipmentType.HeadWear => "Head Wear",
        EquipmentType.HandWear => "Hand Wear",
        EquipmentType.Chems => "Chems",
        EquipmentType.Stimpack => "Stimpack",
        EquipmentType.Food => "Food",
        EquipmentType.Alcohol => "Alcohol",
        _ => $"Unknown ({(int)EquipmentType})"
    };

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
