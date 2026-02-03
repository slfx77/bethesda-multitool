namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Fully reconstructed Ammo from memory dump.
///     Aggregates data from AMMO main record header, DATA (13 bytes).
/// </summary>
public record ReconstructedAmmo
{
    /// <summary>FormID of the ammo record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID.</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name.</summary>
    public string? FullName { get; init; }

    // DATA subrecord (13 bytes)
    /// <summary>Projectile speed.</summary>
    public float Speed { get; init; }

    /// <summary>Ammo flags.</summary>
    public byte Flags { get; init; }

    /// <summary>Base value in caps.</summary>
    public uint Value { get; init; }

    /// <summary>Rounds per clip (for display).</summary>
    public byte ClipRounds { get; init; }

    // DAT2 subrecord (FNV-specific, 20 bytes)
    /// <summary>Projectile FormID (from DAT2 subrecord).</summary>
    public uint? ProjectileFormId { get; init; }

    /// <summary>Weight (from DAT2 subrecord).</summary>
    public float Weight { get; init; }

    /// <summary>Model file path (MODL subrecord) — the ammo's world model.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Model file path of the associated projectile (BGSProjectile.TESModel.cModel).</summary>
    public string? ProjectileModelPath { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
