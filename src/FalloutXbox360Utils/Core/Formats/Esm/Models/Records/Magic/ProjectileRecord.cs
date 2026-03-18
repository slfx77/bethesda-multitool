namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Parsed Projectile (PROJ) record.
/// </summary>
public record ProjectileRecord
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public string? FullName { get; init; }

    /// <summary>Model path from MODL subrecord.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Projectile flags from DATA.</summary>
    public ushort Flags { get; init; }

    /// <summary>Projectile type from DATA.</summary>
    public ushort ProjectileType { get; init; }

    /// <summary>Gravity from DATA.</summary>
    public float Gravity { get; init; }

    /// <summary>Speed from DATA.</summary>
    public float Speed { get; init; }

    /// <summary>Range from DATA.</summary>
    public float Range { get; init; }

    /// <summary>Light FormID from DATA.</summary>
    public uint Light { get; init; }

    /// <summary>Muzzle flash light FormID from DATA.</summary>
    public uint MuzzleFlashLight { get; init; }

    /// <summary>Tracer chance (0.0–1.0) from DATA.</summary>
    public float TracerChance { get; init; }

    /// <summary>Explosion proximity trigger distance from DATA.</summary>
    public float ExplosionProximity { get; init; }

    /// <summary>Explosion countdown timer (seconds) from DATA.</summary>
    public float ExplosionTimer { get; init; }

    /// <summary>Explosion FormID from DATA.</summary>
    public uint Explosion { get; init; }

    /// <summary>Sound FormID from DATA.</summary>
    public uint Sound { get; init; }

    /// <summary>Muzzle flash duration from DATA.</summary>
    public float MuzzleFlashDuration { get; init; }

    /// <summary>Fade duration from DATA.</summary>
    public float FadeDuration { get; init; }

    /// <summary>Impact force from DATA.</summary>
    public float ImpactForce { get; init; }

    /// <summary>Countdown sound FormID from DATA.</summary>
    public uint CountdownSound { get; init; }

    /// <summary>Deactivate sound FormID from DATA.</summary>
    public uint DeactivateSound { get; init; }

    /// <summary>Default weapon source FormID from DATA.</summary>
    public uint DefaultWeaponSource { get; init; }

    /// <summary>Initial X rotation (radians) from DATA.</summary>
    public float RotationX { get; init; }

    /// <summary>Initial Y rotation (radians) from DATA.</summary>
    public float RotationY { get; init; }

    /// <summary>Initial Z rotation (radians) from DATA.</summary>
    public float RotationZ { get; init; }

    /// <summary>Bounce multiplier from DATA.</summary>
    public float BounceMultiplier { get; init; }

    /// <summary>Sound attenuation level from VNAM subrecord.</summary>
    public uint SoundLevel { get; init; }

    /// <summary>Countdown timer from DATA (legacy alias for ExplosionTimer).</summary>
    public float Timer => ExplosionTimer;

    public long Offset { get; init; }
    public bool IsBigEndian { get; init; }

    public string TypeName => ProjectileType switch
    {
        1 => "Missile",
        2 => "Lobber",
        4 => "Beam",
        8 => "Flame",
        _ => $"Unknown({ProjectileType})"
    };
}
