namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Physics and sound data extracted from a BGSProjectile runtime struct.
///     Cross-referenced into weapons via Weapon → ProjectileFormId lookup.
/// </summary>
public record ProjectilePhysicsData
{
    /// <summary>Gravity factor applied to the projectile.</summary>
    public float Gravity { get; init; }

    /// <summary>Projectile travel speed (units/sec).</summary>
    public float Speed { get; init; }

    /// <summary>Maximum effective range (units).</summary>
    public float Range { get; init; }

    /// <summary>Explosion type FormID (BGSExplosion*).</summary>
    public uint? ExplosionFormId { get; init; }

    /// <summary>In-flight looping sound FormID (TESSound*).</summary>
    public uint? ActiveSoundLoopFormId { get; init; }

    /// <summary>Countdown/arm sound FormID (TESSound*).</summary>
    public uint? CountdownSoundFormId { get; init; }

    /// <summary>Deactivation sound FormID (TESSound*).</summary>
    public uint? DeactivateSoundFormId { get; init; }

    /// <summary>Muzzle flash duration in seconds.</summary>
    public float MuzzleFlashDuration { get; init; }

    /// <summary>Impact force applied to target.</summary>
    public float Force { get; init; }

    /// <summary>World model path (NIF) from TESModel.cModel.</summary>
    public string? ModelPath { get; init; }
}
