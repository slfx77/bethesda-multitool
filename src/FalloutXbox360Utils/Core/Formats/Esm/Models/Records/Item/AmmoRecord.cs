namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

/// <summary>
///     Parsed Ammo record.
///     Aggregates data from AMMO main record header, DATA (13 bytes).
/// </summary>
public record AmmoRecord
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

    /// <summary>All projectile FormIDs inferred from direct AMMO data and weapon reverse references.</summary>
    public List<uint> ProjectileFormIds { get; init; } = [];

    /// <summary>Weight (from DAT2 subrecord).</summary>
    public float Weight { get; init; }

    /// <summary>Model file path (MODL subrecord) — the ammo's world model.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Model file path of the associated projectile (BGSProjectile.TESModel.cModel).</summary>
    public string? ProjectileModelPath { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    /// <summary>Inventory image path from ICON subrecord.</summary>
    public string? IconPath { get; init; }

    /// <summary>Message icon path from MICO subrecord.</summary>
    public string? MessageIconPath { get; init; }

    /// <summary>Texture hash data from MODT subrecord (opaque bytes — engine validates).</summary>
    public byte[]? TextureHashData { get; init; }

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }
}
