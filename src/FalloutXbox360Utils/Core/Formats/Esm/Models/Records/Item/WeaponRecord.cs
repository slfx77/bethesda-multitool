using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     Fully reconstructed Weapon from memory dump.
///     Aggregates data from WEAP main record header, DATA (15 bytes), DNAM (204 bytes), CRDT, etc.
/// </summary>
public record WeaponRecord
{
    // Core identification
    /// <summary>FormID of the weapon record.</summary>
    public uint FormId { get; init; }

    /// <summary>Editor ID (e.g., "WeapGunPistol10mm").</summary>
    public string? EditorId { get; init; }

    /// <summary>Display name (e.g., "10mm Pistol").</summary>
    public string? FullName { get; init; }

    // DATA subrecord (15 bytes)
    /// <summary>Base value in caps.</summary>
    public int Value { get; init; }

    /// <summary>Weapon health/condition.</summary>
    public int Health { get; init; }

    /// <summary>Weight in units.</summary>
    public float Weight { get; init; }

    /// <summary>Base damage.</summary>
    public short Damage { get; init; }

    /// <summary>Clip/magazine size.</summary>
    public byte ClipSize { get; init; }

    // DNAM subrecord (204 bytes) - key combat fields
    /// <summary>Weapon type classification.</summary>
    public WeaponType WeaponType { get; init; }

    /// <summary>Animation type (for attack speed).</summary>
    public uint AnimationType { get; init; }

    /// <summary>Attack speed multiplier.</summary>
    public float Speed { get; init; }

    /// <summary>Melee reach distance.</summary>
    public float Reach { get; init; }

    /// <summary>Weapon flags (DNAM byte 16).</summary>
    public byte Flags { get; init; }

    /// <summary>Hand grip animation type (DNAM byte 17).</summary>
    public HandGripAnimation HandGrip { get; init; }

    /// <summary>Ammo consumed per shot.</summary>
    public byte AmmoPerShot { get; init; }

    /// <summary>Reload animation type (DNAM byte 19).</summary>
    public ReloadAnimation ReloadAnim { get; init; }

    /// <summary>Minimum spread (accuracy).</summary>
    public float MinSpread { get; init; }

    /// <summary>Maximum spread (inaccuracy).</summary>
    public float Spread { get; init; }

    /// <summary>Sight/sway drift.</summary>
    public float Drift { get; init; }

    /// <summary>Iron sight FOV zoom.</summary>
    public float IronSightFov { get; init; }

    /// <summary>Ammo type FormID (ENAM subrecord).</summary>
    public uint? AmmoFormId { get; init; }

    /// <summary>Projectile type FormID.</summary>
    public uint? ProjectileFormId { get; init; }

    /// <summary>Impact data set FormID (BGSImpactDataSet*, dump +584).</summary>
    public uint? ImpactDataSetFormId { get; init; }

    /// <summary>VATS to-hit chance bonus.</summary>
    public byte VatsToHitChance { get; init; }

    /// <summary>Attack animation type (DNAM byte 34).</summary>
    public AttackAnimation AttackAnim { get; init; }

    /// <summary>Number of projectiles per shot.</summary>
    public byte NumProjectiles { get; init; }

    /// <summary>Minimum effective range.</summary>
    public float MinRange { get; init; }

    /// <summary>Maximum effective range.</summary>
    public float MaxRange { get; init; }

    /// <summary>On-hit behavior (dismemberment/explosion).</summary>
    public OnHitBehavior OnHit { get; init; }

    /// <summary>Extended weapon flags (DNAM FlagsEx uint32).</summary>
    public uint FlagsEx { get; init; }

    /// <summary>Attack damage multiplier.</summary>
    public float AttackMultiplier { get; init; }

    /// <summary>Shots per second (fire rate).</summary>
    public float ShotsPerSec { get; init; }

    /// <summary>Action point cost in VATS.</summary>
    public float ActionPoints { get; init; }

    /// <summary>Aim arc (accuracy cone).</summary>
    public float AimArc { get; init; }

    /// <summary>Limb damage multiplier.</summary>
    public float LimbDamageMult { get; init; }

    /// <summary>Strength requirement to use effectively.</summary>
    public uint StrengthRequirement { get; init; }

    /// <summary>Skill requirement to use effectively.</summary>
    public uint SkillRequirement { get; init; }

    // ETYP subrecord (4 bytes)
    /// <summary>Equipment type — determines hotkey icon category.</summary>
    public EquipmentType EquipmentType { get; init; } = EquipmentType.None;

    /// <summary>Human-readable equipment type name.</summary>
    public string EquipmentTypeName => EquipmentType switch
    {
        EquipmentType.None => "None",
        EquipmentType.BigGuns => "Big Guns",
        EquipmentType.EnergyWeapons => "Energy Weapons",
        EquipmentType.SmallGuns => "Small Guns",
        EquipmentType.MeleeWeapons => "Melee Weapons",
        EquipmentType.UnarmedWeapon => "Unarmed Weapon",
        EquipmentType.ThrownWeapons => "Thrown Weapons",
        EquipmentType.Mine => "Mine",
        _ => $"Unknown ({(int)EquipmentType})"
    };

    // CRDT subrecord (critical data)
    /// <summary>Critical hit bonus damage.</summary>
    public short CriticalDamage { get; init; }

    /// <summary>Critical hit chance multiplier.</summary>
    public float CriticalChance { get; init; }

    /// <summary>Critical effect FormID.</summary>
    public uint? CriticalEffectFormId { get; init; }

    // Model reference
    /// <summary>Model file path (MODL subrecord).</summary>
    public string? ModelPath { get; init; }

    // Sound effects (TESSound* pointers, empirically verified at dump offsets)
    /// <summary>Pickup sound FormID (BGSPickupPutdownSounds, dump +252).</summary>
    public uint? PickupSoundFormId { get; init; }

    /// <summary>Putdown sound FormID (BGSPickupPutdownSounds, dump +256).</summary>
    public uint? PutdownSoundFormId { get; init; }

    /// <summary>3D fire/attack sound FormID (dump +548).</summary>
    public uint? FireSound3DFormId { get; init; }

    /// <summary>Distant fire sound FormID (dump +552).</summary>
    public uint? FireSoundDistFormId { get; init; }

    /// <summary>2D fire/attack sound FormID (dump +556).</summary>
    public uint? FireSound2DFormId { get; init; }

    /// <summary>Dry fire / attack fail sound FormID (dump +564).</summary>
    public uint? DryFireSoundFormId { get; init; }

    /// <summary>Idle / ambient sound FormID (dump +572).</summary>
    public uint? IdleSoundFormId { get; init; }

    /// <summary>Equip sound FormID (dump +576).</summary>
    public uint? EquipSoundFormId { get; init; }

    /// <summary>Unequip sound FormID (dump +580).</summary>
    public uint? UnequipSoundFormId { get; init; }

    // Projectile physics (cross-referenced from BGSProjectile runtime struct)
    /// <summary>Projectile physics data, if a projectile is assigned and readable.</summary>
    public ProjectilePhysicsData? ProjectileData { get; init; }

    /// <summary>Object bounds (OBND subrecord).</summary>
    public ObjectBounds? Bounds { get; init; }

    // Computed
    /// <summary>Calculated damage per second.</summary>
    public float DamagePerSecond => Damage * ShotsPerSec;

    /// <summary>Offset in the dump where this record was found.</summary>
    public long Offset { get; init; }

    /// <summary>Whether the record was detected as big-endian (Xbox 360).</summary>
    public bool IsBigEndian { get; init; }

    /// <summary>Human-readable weapon type name.</summary>
    public string WeaponTypeName => WeaponType switch
    {
        WeaponType.HandToHand => "Hand-to-Hand",
        WeaponType.Melee1H => "Melee (1H)",
        WeaponType.Melee2H => "Melee (2H)",
        WeaponType.Pistol => "Pistol",
        WeaponType.PistolAutomatic => "Pistol (Auto)",
        WeaponType.Rifle => "Rifle",
        WeaponType.RifleAutomatic => "Rifle (Auto)",
        WeaponType.Handle => "Handle",
        WeaponType.Launcher => "Launcher",
        WeaponType.GrenadeThrow => "Grenade",
        WeaponType.LandMine => "Land Mine",
        WeaponType.MinePlacement => "Mine Placement",
        _ => $"Unknown ({(byte)WeaponType})"
    };

    /// <summary>Human-readable hand grip animation name.</summary>
    public string HandGripName => HandGrip switch
    {
        HandGripAnimation.HandGrip1 => "HandGrip1",
        HandGripAnimation.HandGrip2 => "HandGrip2",
        HandGripAnimation.HandGrip3 => "HandGrip3",
        HandGripAnimation.Default => "Default",
        _ => $"Unknown ({(byte)HandGrip})"
    };

    /// <summary>Human-readable reload animation name.</summary>
    public string ReloadAnimName => ReloadAnim switch
    {
        >= ReloadAnimation.ReloadA and <= ReloadAnimation.ReloadK =>
            $"Reload{(char)('A' + (byte)ReloadAnim)}",
        _ => $"Unknown ({(byte)ReloadAnim})"
    };

    /// <summary>Human-readable attack animation name.</summary>
    public string AttackAnimName => AttackAnim switch
    {
        AttackAnimation.AttackLeft => "AttackLeft",
        AttackAnimation.AttackRight => "AttackRight",
        AttackAnimation.Attack3 => "Attack3",
        AttackAnimation.Attack4 => "Attack4",
        AttackAnimation.Attack5 => "Attack5",
        AttackAnimation.Attack6 => "Attack6",
        AttackAnimation.Attack7 => "Attack7",
        AttackAnimation.Attack8 => "Attack8",
        AttackAnimation.AttackLoop => "AttackLoop",
        AttackAnimation.AttackSpin => "AttackSpin",
        AttackAnimation.AttackSpin2 => "AttackSpin2",
        AttackAnimation.PlaceMine => "PlaceMine",
        AttackAnimation.PlaceMine2 => "PlaceMine2",
        AttackAnimation.AttackThrow => "AttackThrow",
        AttackAnimation.AttackThrow2 => "AttackThrow2",
        AttackAnimation.AttackThrow3 => "AttackThrow3",
        AttackAnimation.AttackThrow4 => "AttackThrow4",
        AttackAnimation.AttackThrow5 => "AttackThrow5",
        AttackAnimation.Default => "Default",
        _ => $"Unknown ({(byte)AttackAnim})"
    };

    /// <summary>Human-readable on-hit behavior name.</summary>
    public string OnHitName => OnHit switch
    {
        OnHitBehavior.Normal => "Normal",
        OnHitBehavior.DismemberOnly => "Dismember Only",
        OnHitBehavior.ExplodeOnly => "Explode Only",
        OnHitBehavior.NoDismemberOrExplode => "No Dismember/Explode",
        _ => $"Unknown ({(uint)OnHit})"
    };
}
