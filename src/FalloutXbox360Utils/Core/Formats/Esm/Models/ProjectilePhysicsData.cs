namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Physics and sound data extracted from a BGSProjectile runtime struct.
///     Cross-referenced into weapons via Weapon → ProjectileFormId lookup.
///     Fields map to BGSProjectileData (84 bytes at BGSProjectile+112 in Final PDB).
/// </summary>
public record ProjectilePhysicsData
{
    /// <summary>Projectile flags (BGSProjectileData.iFlags uint32).</summary>
    public uint Flags { get; init; }

    /// <summary>Gravity factor applied to the projectile.</summary>
    public float Gravity { get; init; }

    /// <summary>Projectile travel speed (units/sec).</summary>
    public float Speed { get; init; }

    /// <summary>Maximum effective range (units).</summary>
    public float Range { get; init; }

    /// <summary>Light FormID (TESObjectLIGH*) for in-flight illumination.</summary>
    public uint? LightFormId { get; init; }

    /// <summary>Muzzle flash light FormID (TESObjectLIGH*).</summary>
    public uint? MuzzleFlashLightFormId { get; init; }

    /// <summary>Tracer chance (0.0–1.0).</summary>
    public float TracerChance { get; init; }

    /// <summary>Explosion proximity trigger distance.</summary>
    public float ExplosionProximity { get; init; }

    /// <summary>Explosion countdown timer (seconds).</summary>
    public float ExplosionTimer { get; init; }

    /// <summary>Explosion type FormID (BGSExplosion*).</summary>
    public uint? ExplosionFormId { get; init; }

    /// <summary>In-flight looping sound FormID (TESSound*).</summary>
    public uint? ActiveSoundLoopFormId { get; init; }

    /// <summary>Muzzle flash duration in seconds.</summary>
    public float MuzzleFlashDuration { get; init; }

    /// <summary>Fade-out time in seconds.</summary>
    public float FadeOutTime { get; init; }

    /// <summary>Impact force applied to target.</summary>
    public float Force { get; init; }

    /// <summary>Countdown/arm sound FormID (TESSound*).</summary>
    public uint? CountdownSoundFormId { get; init; }

    /// <summary>Deactivation sound FormID (TESSound*).</summary>
    public uint? DeactivateSoundFormId { get; init; }

    /// <summary>Default weapon source FormID (TESObjectWEAP*).</summary>
    public uint? DefaultWeaponSourceFormId { get; init; }

    /// <summary>Initial X rotation (radians).</summary>
    public float RotationX { get; init; }

    /// <summary>Initial Y rotation (radians).</summary>
    public float RotationY { get; init; }

    /// <summary>Initial Z rotation (radians).</summary>
    public float RotationZ { get; init; }

    /// <summary>Bounce multiplier for bouncy projectiles.</summary>
    public float BounceMultiplier { get; init; }

    /// <summary>World model path (NIF) from TESModel.cModel.</summary>
    public string? ModelPath { get; init; }

    /// <summary>Display name from TESFullName.</summary>
    public string? FullName { get; init; }

    /// <summary>Sound attenuation level (BGSProjectile.eSoundLevel enum).</summary>
    public uint SoundLevel { get; init; }
}
