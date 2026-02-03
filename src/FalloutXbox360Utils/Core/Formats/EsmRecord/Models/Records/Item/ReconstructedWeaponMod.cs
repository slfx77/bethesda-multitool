namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Reconstructed Weapon Mod (IMOD) from memory dump.
///     FNV-specific record type for weapon modifications.
/// </summary>
public record ReconstructedWeaponMod
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }
    public string? Description { get; init; }

    /// <summary>Model path from MODL subrecord.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Icon path from ICON subrecord.</summary>
    public string? Icon { get; init; }

    /// <summary>Value in caps from DATA.</summary>
    public int Value { get; init; }

    /// <summary>Weight from DATA.</summary>
    public float Weight { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
