using FalloutXbox360Utils.Core.Formats.Esm.Enums;

namespace FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;

/// <summary>
///     Parsed Weapon record.
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

    /// <summary>Decoded WeaponFlags accessor (computed from <see cref="Flags" />).</summary>
    public WeaponFlags FlagBits => (WeaponFlags)Flags;

    /// <summary>True if this weapon is fully automatic (Flags bit 0x02).</summary>
    public bool IsAutomatic => (FlagBits & WeaponFlags.Automatic) != 0;

    /// <summary>True if this weapon has an iron sight scope (Flags bit 0x04).</summary>
    public bool HasScope => (FlagBits & WeaponFlags.HasScope) != 0;

    /// <summary>True if this weapon cannot be dropped (Flags bit 0x08).</summary>
    public bool CantDrop => (FlagBits & WeaponFlags.CantDrop) != 0;

    /// <summary>True if this weapon hides the player's backpack when equipped (Flags bit 0x10).</summary>
    public bool HideBackpack => (FlagBits & WeaponFlags.HideBackpack) != 0;

    /// <summary>True if this is an embedded weapon (e.g., creature attack — Flags bit 0x20).</summary>
    public bool IsEmbeddedWeapon => (FlagBits & WeaponFlags.EmbeddedWeapon) != 0;

    /// <summary>True if first-person iron sight animations are disabled (Flags bit 0x40).</summary>
    public bool DontUseFirstPersonIsAnimations => (FlagBits & WeaponFlags.DontUseFirstPersonIsAnimations) != 0;

    /// <summary>True if this weapon is non-playable (Flags bit 0x80).</summary>
    public bool IsNonPlayable => (FlagBits & WeaponFlags.NonPlayable) != 0;

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

    /// <summary>Decoded WeaponFlagsEx accessor.</summary>
    public WeaponFlagsEx FlagBitsEx => (WeaponFlagsEx)FlagsEx;

    /// <summary>True if this weapon is restricted to the player (FlagsEx bit 0x01).</summary>
    public bool IsPlayerOnly => (FlagBitsEx & WeaponFlagsEx.PlayerOnly) != 0;

    /// <summary>True if NPCs use ammo when firing this weapon (FlagsEx bit 0x02).</summary>
    public bool NpcsUseAmmo => (FlagBitsEx & WeaponFlagsEx.NpcsUseAmmo) != 0;

    /// <summary>True if no jam after reload (FlagsEx bit 0x04).</summary>
    public bool NoJamAfterReload => (FlagBitsEx & WeaponFlagsEx.NoJamAfterReload) != 0;

    /// <summary>True if firing this weapon is a minor crime (FlagsEx bit 0x10).</summary>
    public bool IsMinorCrime => (FlagBitsEx & WeaponFlagsEx.MinorCrime) != 0;

    /// <summary>True if range is fixed (FlagsEx bit 0x20).</summary>
    public bool IsRangeFixed => (FlagBitsEx & WeaponFlagsEx.RangeFixed) != 0;

    /// <summary>True if this weapon is not used in normal combat (FlagsEx bit 0x40).</summary>
    public bool NotUsedInNormalCombat => (FlagBitsEx & WeaponFlagsEx.NotUsedInNormalCombat) != 0;

    /// <summary>True if third-person iron sight animations are disabled (FlagsEx bit 0x100).</summary>
    public bool DontUseThirdPersonIsAnimations => (FlagBitsEx & WeaponFlagsEx.DontUseThirdPersonIsAnimations) != 0;

    /// <summary>True if this weapon supports long bursts (FlagsEx bit 0x200).</summary>
    public bool HasLongBursts => (FlagBitsEx & WeaponFlagsEx.LongBursts) != 0;

    // ── Phase 3: previously-unparsed DNAM fields ──

    /// <summary>Override damage multiplier applied to the weapon's own condition. DNAM +84.</summary>
    public float DamageToWeaponMult { get; init; } = 1.0f;

    /// <summary>Resistance bonus applied by the weapon (DNAM +120).</summary>
    public uint Resistance { get; init; }

    /// <summary>Sight Usage / Iron sight scope multiplier. DNAM +124.</summary>
    public float IronSightUseMult { get; init; } = 1.0f;

    /// <summary>Ammo regeneration rate per second (e.g., MF Hyperbreeder). DNAM +176.</summary>
    public float AmmoRegenRate { get; init; }

    /// <summary>Kill impulse force applied to ragdolls. DNAM +180.</summary>
    public float KillImpulse { get; init; }

    /// <summary>Distance over which kill impulse is applied. DNAM +196.</summary>
    public float KillImpulseDistance { get; init; }

    /// <summary>Semi-automatic fire delay minimum (seconds). DNAM +128.</summary>
    public float SemiAutoFireDelayMin { get; init; }

    /// <summary>Semi-automatic fire delay maximum (seconds). DNAM +132.</summary>
    public float SemiAutoFireDelayMax { get; init; }

    /// <summary>Animation shots per second override. DNAM +88.</summary>
    public float AnimShotsPerSecond { get; init; }

    /// <summary>Animation reload time override (seconds). DNAM +92.</summary>
    public float AnimReloadTime { get; init; }

    /// <summary>Animation jam time override (seconds). DNAM +96.</summary>
    public float AnimJamTime { get; init; }

    /// <summary>Power attack override animation type. DNAM +164.</summary>
    public byte PowerAttackOverrideAnim { get; init; }

    /// <summary>Mod-attached reload clip animation override. DNAM +172 (signed).</summary>
    public sbyte ModReloadClipAnimation { get; init; }

    /// <summary>Mod-attached fire animation override. DNAM +173 (signed).</summary>
    public sbyte ModFireAnimation { get; init; }

    /// <summary>Grenade cook timer (seconds). DNAM +136.</summary>
    public float CookTimer { get; init; }

    /// <summary>Rumble: left motor strength. DNAM +72.</summary>
    public float RumbleLeftMotor { get; init; }

    /// <summary>Rumble: right motor strength. DNAM +76.</summary>
    public float RumbleRightMotor { get; init; }

    /// <summary>Rumble: duration (seconds). DNAM +80.</summary>
    public float RumbleDuration { get; init; }

    /// <summary>Rumble: pattern enum. DNAM +108.</summary>
    public uint RumblePattern { get; init; }

    /// <summary>Rumble: wavelength. DNAM +112.</summary>
    public float RumbleWavelength { get; init; }

    /// <summary>VATS attack data parsed from the VATS subrecord (20 bytes).</summary>
    public VatsAttackData? VatsAttack { get; init; }

    /// <summary>Inventory image path from ICON subrecord (Pip-Boy menu icon).</summary>
    public string? InventoryIconPath { get; init; }

    /// <summary>Message icon path from MICO subrecord.</summary>
    public string? MessageIconPath { get; init; }

    /// <summary>Shell casing model path from MOD2 subrecord.</summary>
    public string? ShellCasingModelPath { get; init; }

    /// <summary>Repair item list FormID from REPL subrecord (BGSListForm).</summary>
    public uint? RepairItemListFormId { get; init; }

    /// <summary>
    ///     Modded model variants — 3rd-person world model + 1st-person STAT object FormID
    ///     for each combination of installed mods (None / Mod1 / Mod2 / Mod3 / Mod12 / Mod13 / Mod23 / Mod123).
    /// </summary>
    public List<WeaponModelVariant> ModelVariants { get; init; } = [];

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

    /// <summary>Governing skill ActorValue code (e.g., 41=Guns, 34=Energy Weapons, 33=Big Guns).</summary>
    public uint Skill { get; init; }

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

    /// <summary>Embedded weapon attach node name (NNAM subrecord / runtime BSStringT).</summary>
    public string? EmbeddedWeaponNode { get; init; }

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

    /// <summary>Attack loop sound FormID (e.g., minigun spin-up). PDB +568 pAttackLoop.</summary>
    public uint? AttackLoopSoundFormId { get; init; }

    /// <summary>Melee block sound FormID. PDB +576 pMeleeBlockSound.</summary>
    public uint? MeleeBlockSoundFormId { get; init; }

    /// <summary>Modded weapon attack sound (3D), used when a silencer mod is attached. PDB +592.</summary>
    public uint? ModSilencedSound3DFormId { get; init; }

    /// <summary>Modded weapon attack sound (Distant), used when a silencer mod is attached. PDB +596.</summary>
    public uint? ModSilencedSoundDistFormId { get; init; }

    /// <summary>Modded weapon attack sound (2D), used when a silencer mod is attached. PDB +600.</summary>
    public uint? ModSilencedSound2DFormId { get; init; }

    // Weapon mod slots (from DNAM — defines what mods the weapon accepts and their effects)
    /// <summary>Mod slots with action types, values, and assigned IMOD FormIDs.</summary>
    public List<WeaponModSlot> ModSlots { get; init; } = [];

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
        WeaponType.HandToHandMelee => "Hand-to-Hand",
        WeaponType.OneHandMelee => "Melee (1H)",
        WeaponType.TwoHandMelee => "Melee (2H)",
        WeaponType.OneHandPistol => "Pistol",
        WeaponType.OneHandPistolEnergy => "Pistol (Energy)",
        WeaponType.TwoHandRifle => "Rifle",
        WeaponType.TwoHandAutomatic => "Automatic (2H)",
        WeaponType.TwoHandRifleEnergy => "Rifle (Energy)",
        WeaponType.TwoHandHandle => "Handle",
        WeaponType.TwoHandLauncher => "Launcher",
        WeaponType.OneHandGrenade => "Grenade",
        WeaponType.OneHandMine => "Mine",
        WeaponType.OneHandLunchboxMine => "Lunchbox Mine",
        WeaponType.OneHandThrown => "Thrown / Spear",
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
