namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Armor from memory dump.
///     Aggregates data from ARMO main record header, DATA (12 bytes), DNAM (12 bytes).
/// </summary>
public record ReconstructedArmor
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

    // DNAM subrecord (12 bytes): DR (int16), DT (float), Flags (uint16)
    /// <summary>Damage Threshold (DT) — the primary armor stat in Fallout New Vegas.</summary>
    public float DamageThreshold { get; init; }

    /// <summary>Damage Resistance (DR) — deprecated in FNV, typically 0.</summary>
    public int DamageResistance { get; init; }

    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
