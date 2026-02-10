namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Reconstructed Projectile (PROJ) from memory dump.
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

    /// <summary>Countdown timer from DATA.</summary>
    public float Timer { get; init; }

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
