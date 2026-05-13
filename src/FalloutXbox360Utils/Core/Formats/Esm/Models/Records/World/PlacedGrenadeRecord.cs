namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

/// <summary>
///     Placed Grenade (PGRE) record. A REFR-family record representing a
///     pre-thrown grenade placed in the world. PDB struct: GrenadeProjectile
///     (400 bytes, FormType 0x3E) — inherits from TESObjectREFR / Projectile.
///     For Phase 10 we capture identity, base object, and position only;
///     the per-frame physics state is intentionally skipped.
/// </summary>
public record PlacedGrenadeRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }

    /// <summary>Base object FormID (the WEAP or PROJ this grenade represents — NAME subrecord).</summary>
    public uint BaseFormId { get; init; }

    /// <summary>World position X (DATA subrecord, float).</summary>
    public float PositionX { get; init; }

    /// <summary>World position Y.</summary>
    public float PositionY { get; init; }

    /// <summary>World position Z.</summary>
    public float PositionZ { get; init; }

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }
}
