namespace FalloutXbox360Utils.Core.Formats.Esm.Enums;

/// <summary>
///     Equipment type from ETYP subrecord (int32).
///     Determines which icon is displayed for hot-keyed items.
/// </summary>
public enum EquipmentType
{
    None = -1,
    BigGuns = 0,
    EnergyWeapons = 1,
    SmallGuns = 2,
    MeleeWeapons = 3,
    UnarmedWeapon = 4,
    ThrownWeapons = 5,
    Mine = 6,
    BodyWear = 7,
    HeadWear = 8,
    HandWear = 9,
    Chems = 10,
    Stimpack = 11,
    Food = 12,
    Alcohol = 13
}
