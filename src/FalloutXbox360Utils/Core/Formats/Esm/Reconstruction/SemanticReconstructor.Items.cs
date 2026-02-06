using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class SemanticReconstructor
{
    #region ReconstructKeys

    /// <summary>
    ///     Reconstruct all Key records from the scan result.
    /// </summary>
    public List<ReconstructedKey> ReconstructKeys()
    {
        var keys = new List<ReconstructedKey>();
        var keyRecords = GetRecordsByType("KEYM").ToList();

        foreach (var record in keyRecords)
        {
            keys.Add(new ReconstructedKey
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        // Merge keys from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(keys.Select(k => k.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x2E || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeKey(entry);
                if (item != null)
                {
                    keys.Add(item);
                    runtimeCount++;
                }
            }
        }

        return keys;
    }

    #endregion

    #region Private Helper - ReadFormId

    private static uint ReadFormId(ReadOnlySpan<byte> data, bool bigEndian)
    {
        if (data.Length < 4)
        {
            return 0;
        }

        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data)
            : BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    #endregion

    #region ReconstructWeapons

    /// <summary>
    ///     Reconstruct all Weapon records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data (typically hundreds of weapons vs ~34 ESM records).
    /// </summary>
    public List<ReconstructedWeapon> ReconstructWeapons()
    {
        var weapons = new List<ReconstructedWeapon>();
        var weaponRecords = GetRecordsByType("WEAP").ToList();

        // Track FormIDs from ESM records to avoid duplicates
        var esmFormIds = new HashSet<uint>();

        if (_accessor == null)
        {
            foreach (var record in weaponRecords)
            {
                var weapon = new ReconstructedWeapon
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                };
                weapons.Add(weapon);
                esmFormIds.Add(weapon.FormId);
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in weaponRecords)
                {
                    var weapon = ReconstructWeaponFromAccessor(record, buffer);
                    if (weapon != null)
                    {
                        weapons.Add(weapon);
                        esmFormIds.Add(weapon.FormId);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge weapons from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x28 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var weapon = _runtimeReader.ReadRuntimeWeapon(entry);
                if (weapon != null)
                {
                    weapons.Add(weapon);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} weapons from runtime struct reading " +
                    $"(total: {weapons.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return weapons;
    }

    private ReconstructedWeapon? ReconstructWeaponFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedWeapon
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;

        // DATA subrecord (15 bytes)
        var value = 0;
        var health = 0;
        float weight = 0;
        short damage = 0;
        byte clipSize = 0;

        // DNAM subrecord (204 bytes)
        WeaponType weaponType = 0;
        uint animationType = 0;
        var speed = 1.0f;
        float reach = 0;
        byte ammoPerShot = 1;
        float minSpread = 0;
        float spread = 0;
        float drift = 0;
        uint? ammoFormId = null;
        uint? projectileFormId = null;
        byte vatsToHitChance = 0;
        byte numProjectiles = 1;
        float minRange = 0;
        float maxRange = 0;
        float shotsPerSec = 1;
        float actionPoints = 0;
        uint strengthRequirement = 0;
        uint skillRequirement = 0;

        // CRDT subrecord
        short criticalDamage = 0;
        var criticalChance = 1.0f;
        uint? criticalEffectFormId = null;

        // Sound subrecords
        uint? pickupSoundFormId = null;
        uint? putdownSoundFormId = null;
        uint? fireSound3DFormId = null;
        uint? fireSoundDistFormId = null;
        uint? fireSound2DFormId = null;
        uint? dryFireSoundFormId = null;
        uint? idleSoundFormId = null;
        uint? equipSoundFormId = null;
        uint? unequipSoundFormId = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ENAM" when sub.DataLength == 4:
                    ammoFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 15:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    health = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    damage = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData[12..])
                        : BinaryPrimitives.ReadInt16LittleEndian(subData[12..]);
                    clipSize = subData[14];
                    break;
                case "DNAM" when sub.DataLength >= 64:
                    // Parse key DNAM fields
                    animationType = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    speed = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    reach = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    // Animation type (DNAM byte 0, already read as uint32) is the weapon type
                    weaponType = (WeaponType)(animationType <= 11 ? animationType : 0);
                    minSpread = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[20..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[20..]);
                    spread = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[24..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[24..]);
                    // DNAM offset 36: Projectile FormID [PROJ]
                    if (sub.DataLength >= 40)
                    {
                        var projId = ReadFormId(subData[36..], record.IsBigEndian);
                        if (projId != 0)
                        {
                            projectileFormId = projId;
                        }
                    }

                    if (sub.DataLength >= 100)
                    {
                        // offset 64: Fire Rate (shots/sec), offset 68: AP override
                        shotsPerSec = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[64..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[64..]);
                        actionPoints = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[68..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[68..]);
                        // offset 44: Min Range, offset 48: Max Range
                        minRange = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[44..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[44..]);
                        maxRange = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[48..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[48..]);
                    }

                    break;
                case "CRDT" when sub.DataLength >= 12:
                    criticalDamage = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData)
                        : BinaryPrimitives.ReadInt16LittleEndian(subData);
                    criticalChance = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    criticalEffectFormId = ReadFormId(subData[8..], record.IsBigEndian);
                    break;
                // Sound subrecords - each is a single FormID [SOUN]
                case "YNAM" when sub.DataLength == 4:
                    pickupSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ZNAM" when sub.DataLength == 4:
                    putdownSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 4:
                    // SNAM is paired: Shoot 3D FormID + Shoot Dist FormID (8 bytes)
                    fireSound3DFormId = ReadFormId(subData, record.IsBigEndian);
                    if (sub.DataLength >= 8)
                    {
                        fireSoundDistFormId = ReadFormId(subData[4..], record.IsBigEndian);
                    }

                    break;
                case "XNAM" when sub.DataLength == 4:
                    fireSound2DFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TNAM" when sub.DataLength == 4:
                    dryFireSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "UNAM" when sub.DataLength == 4:
                    idleSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM9" when sub.DataLength == 4:
                    equipSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM8" when sub.DataLength == 4:
                    unequipSoundFormId = ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new ReconstructedWeapon
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Health = health,
            Weight = weight,
            Damage = damage,
            ClipSize = clipSize,
            WeaponType = weaponType,
            AnimationType = animationType,
            Speed = speed,
            Reach = reach,
            AmmoPerShot = ammoPerShot,
            MinSpread = minSpread,
            Spread = spread,
            Drift = drift,
            AmmoFormId = ammoFormId,
            ProjectileFormId = projectileFormId,
            VatsToHitChance = vatsToHitChance,
            NumProjectiles = numProjectiles,
            MinRange = minRange,
            MaxRange = maxRange,
            ShotsPerSec = shotsPerSec,
            ActionPoints = actionPoints,
            StrengthRequirement = strengthRequirement,
            SkillRequirement = skillRequirement,
            CriticalDamage = criticalDamage,
            CriticalChance = criticalChance,
            CriticalEffectFormId = criticalEffectFormId,
            PickupSoundFormId = pickupSoundFormId,
            PutdownSoundFormId = putdownSoundFormId,
            FireSound3DFormId = fireSound3DFormId,
            FireSoundDistFormId = fireSoundDistFormId,
            FireSound2DFormId = fireSound2DFormId,
            DryFireSoundFormId = dryFireSoundFormId,
            IdleSoundFormId = idleSoundFormId,
            EquipSoundFormId = equipSoundFormId,
            UnequipSoundFormId = unequipSoundFormId,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Enrich weapon records with projectile physics data (gravity, speed, range,
    ///     explosion, in-flight sounds) read from the BGSProjectile runtime struct.
    /// </summary>
    private void EnrichWeaponsWithProjectileData(List<ReconstructedWeapon> weapons)
    {
        if (_runtimeReader == null || weapons.Count == 0)
        {
            return;
        }

        // Build: projectile FormID -> TesFormOffset (from runtime EditorID hash table)
        var projectileEntries = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
            {
                projectileEntries.TryAdd(entry.FormId, entry);
            }
        }

        if (projectileEntries.Count == 0)
        {
            return;
        }

        var enrichedCount = 0;
        for (var i = 0; i < weapons.Count; i++)
        {
            var weapon = weapons[i];
            if (!weapon.ProjectileFormId.HasValue)
            {
                continue;
            }

            if (!projectileEntries.TryGetValue(weapon.ProjectileFormId.Value, out var projEntry))
            {
                continue;
            }

            var projData = _runtimeReader.ReadProjectilePhysics(
                projEntry.TesFormOffset!.Value, projEntry.FormId);

            if (projData != null)
            {
                weapons[i] = weapon with { ProjectileData = projData };
                enrichedCount++;
            }
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{weapons.Count} weapons with projectile physics " +
                $"({projectileEntries.Count} projectiles in hash table)");
        }
    }

    #endregion

    #region ReconstructArmor

    /// <summary>
    ///     Reconstruct all Armor records from the scan result.
    /// </summary>
    public List<ReconstructedArmor> ReconstructArmor()
    {
        var armor = new List<ReconstructedArmor>();
        var armorRecords = GetRecordsByType("ARMO").ToList();

        if (_accessor == null)
        {
            foreach (var record in armorRecords)
            {
                armor.Add(new ReconstructedArmor
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in armorRecords)
                {
                    var item = ReconstructArmorFromAccessor(record, buffer);
                    if (item != null)
                    {
                        armor.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge armor from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(armor.Select(a => a.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x18 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeArmor(entry);
                if (item != null)
                {
                    armor.Add(item);
                    runtimeCount++;
                }
            }
        }

        return armor;
    }

    private ReconstructedArmor? ReconstructArmorFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedArmor
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        var value = 0;
        var health = 0;
        float weight = 0;
        float damageThreshold = 0;
        var damageResistance = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 12:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    health = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    break;
                case "DNAM" when sub.DataLength >= 8:
                    // DNAM layout: DR (int16) + unused (2) + DT (float) + Flags (uint16) + unused (2)
                    damageResistance = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt16BigEndian(subData)
                        : BinaryPrimitives.ReadInt16LittleEndian(subData);
                    damageThreshold = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
            }
        }

        return new ReconstructedArmor
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Health = health,
            Weight = weight,
            DamageThreshold = damageThreshold,
            DamageResistance = damageResistance,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region ReconstructAmmo

    /// <summary>
    ///     Reconstruct all Ammo records from the scan result.
    /// </summary>
    public List<ReconstructedAmmo> ReconstructAmmo()
    {
        var ammo = new List<ReconstructedAmmo>();
        var ammoRecords = GetRecordsByType("AMMO").ToList();

        if (_accessor == null)
        {
            foreach (var record in ammoRecords)
            {
                ammo.Add(new ReconstructedAmmo
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in ammoRecords)
                {
                    var item = ReconstructAmmoFromAccessor(record, buffer);
                    if (item != null)
                    {
                        ammo.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge ammo from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(ammo.Select(a => a.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x29 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeAmmo(entry);
                if (item != null)
                {
                    ammo.Add(item);
                    runtimeCount++;
                }
            }
        }

        return ammo;
    }

    private ReconstructedAmmo? ReconstructAmmoFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedAmmo
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        float speed = 0;
        byte flags = 0;
        uint value = 0;
        byte clipRounds = 0;
        uint? projectileFormId = null;
        float weight = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 13:
                    speed = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    flags = subData[4];
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                    clipRounds = subData[12];
                    break;
                case "DAT2" when sub.DataLength >= 8:
                    // DAT2 layout: ProjectilePerShot (U32), Projectile FormID (U32), Weight (float), ...
                    var projId = ReadFormId(subData[4..], record.IsBigEndian);
                    if (projId != 0)
                    {
                        projectileFormId = projId;
                    }

                    if (sub.DataLength >= 12)
                    {
                        weight = record.IsBigEndian
                            ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                            : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    }

                    break;
            }
        }

        return new ReconstructedAmmo
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Speed = speed,
            Flags = flags,
            Value = value,
            ClipRounds = clipRounds,
            ProjectileFormId = projectileFormId,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Cross-references weapons and ammo to populate ProjectileFormId and ProjectileModelPath
    ///     on ammo records. Each weapon has an AmmoFormId and a ProjectileFormId. We reverse-map:
    ///     ammo FormID -> weapon -> projectile FormID -> BGSProjectile model path at dump offset +80.
    /// </summary>
    private void EnrichAmmoWithProjectileModels(
        List<ReconstructedWeapon> weapons,
        List<ReconstructedAmmo> ammo)
    {
        if (_runtimeReader == null || ammo.Count == 0)
        {
            return;
        }

        // Build: ammo FormID -> projectile FormID (from weapons that reference both)
        var ammoToProjectile = new Dictionary<uint, uint>();
        foreach (var weapon in weapons)
        {
            if (weapon.AmmoFormId is > 0 && weapon.ProjectileFormId is > 0)
            {
                ammoToProjectile.TryAdd(weapon.AmmoFormId.Value, weapon.ProjectileFormId.Value);
            }
        }

        if (ammoToProjectile.Count == 0)
        {
            return;
        }

        // Build: projectile FormID -> TesFormOffset (from runtime EditorID hash table)
        // PROJ = FormType 0x33
        var projectileOffsets = new Dictionary<uint, long>();
        foreach (var entry in _scanResult.RuntimeEditorIds)
        {
            if (entry.FormType == 0x33 && entry.TesFormOffset.HasValue)
            {
                projectileOffsets.TryAdd(entry.FormId, entry.TesFormOffset.Value);
            }
        }

        // Enrich each ammo record with projectile FormID and model path
        var enrichedCount = 0;
        for (var i = 0; i < ammo.Count; i++)
        {
            var a = ammo[i];
            if (!ammoToProjectile.TryGetValue(a.FormId, out var projFormId))
            {
                continue;
            }

            string? projModelPath = null;
            if (projectileOffsets.TryGetValue(projFormId, out var projFileOffset))
            {
                // Read model path BSStringT at dump offset +80 (TESModel.cModel in BGSProjectile)
                projModelPath = _runtimeReader.ReadBSStringT(projFileOffset, 80);
            }

            // Create updated record with projectile data
            // (records are immutable, so we replace in the list)
            ammo[i] = a with
            {
                ProjectileFormId = projFormId,
                ProjectileModelPath = projModelPath
            };
            enrichedCount++;
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{ammo.Count} ammo records with projectile data " +
                $"({projectileOffsets.Count} projectiles in hash table)");
        }
    }

    #endregion

    #region ReconstructConsumables

    /// <summary>
    ///     Reconstruct all Consumable (ALCH) records from the scan result.
    /// </summary>
    public List<ReconstructedConsumable> ReconstructConsumables()
    {
        var consumables = new List<ReconstructedConsumable>();
        var alchRecords = GetRecordsByType("ALCH").ToList();

        if (_accessor == null)
        {
            foreach (var record in alchRecords)
            {
                consumables.Add(new ReconstructedConsumable
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in alchRecords)
                {
                    var item = ReconstructConsumableFromAccessor(record, buffer);
                    if (item != null)
                    {
                        consumables.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge consumables from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(consumables.Select(c => c.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x2F || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeConsumable(entry);
                if (item != null)
                {
                    consumables.Add(item);
                    runtimeCount++;
                }
            }
        }

        return consumables;
    }

    private ReconstructedConsumable? ReconstructConsumableFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedConsumable
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        float weight = 0;
        uint value = 0;
        uint? addictionFormId = null;
        float addictionChance = 0;
        var effectFormIds = new List<uint>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 4:
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData)
                        : BinaryPrimitives.ReadSingleLittleEndian(subData);
                    break;
                case "ENIT" when sub.DataLength >= 16:
                    // ENIT layout: Value (S32 @0), Flags (U8 @4), unused (3),
                    //   Withdrawal Effect [SPEL] (@8), Addiction Chance (float @12),
                    //   Consume Sound [SOUN] (@16)
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    addictionFormId = ReadFormId(subData[8..], record.IsBigEndian);
                    addictionChance = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[12..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[12..]);
                    break;
                case "EFID" when sub.DataLength == 4:
                    effectFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedConsumable
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Weight = weight,
            Value = value,
            AddictionFormId = addictionFormId != 0 ? addictionFormId : null,
            AddictionChance = addictionChance,
            EffectFormIds = effectFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region ReconstructMiscItems

    /// <summary>
    ///     Reconstruct all Misc Item records from the scan result.
    /// </summary>
    public List<ReconstructedMiscItem> ReconstructMiscItems()
    {
        var miscItems = new List<ReconstructedMiscItem>();
        var miscRecords = GetRecordsByType("MISC").ToList();

        if (_accessor == null)
        {
            foreach (var record in miscRecords)
            {
                miscItems.Add(new ReconstructedMiscItem
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(4096);
            try
            {
                foreach (var record in miscRecords)
                {
                    var item = ReconstructMiscItemFromAccessor(record, buffer);
                    if (item != null)
                    {
                        miscItems.Add(item);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge misc items from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(miscItems.Select(m => m.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x1F || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var item = _runtimeReader.ReadRuntimeMiscItem(entry);
                if (item != null)
                {
                    miscItems.Add(item);
                    runtimeCount++;
                }
            }
        }

        return miscItems;
    }

    private ReconstructedMiscItem? ReconstructMiscItemFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedMiscItem
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        var value = 0;
        float weight = 0;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 8:
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    break;
            }
        }

        return new ReconstructedMiscItem
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Value = value,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region ReconstructContainers

    /// <summary>
    ///     Reconstruct all Container records from the scan result.
    /// </summary>
    public List<ReconstructedContainer> ReconstructContainers()
    {
        var containers = new List<ReconstructedContainer>();
        var containerRecords = GetRecordsByType("CONT").ToList();

        // Track FormIDs from ESM records to avoid duplicates when merging runtime data
        var esmFormIds = new HashSet<uint>();

        if (_accessor == null)
        {
            // Without accessor, use basic reconstruction (no CNTO parsing)
            foreach (var record in containerRecords)
            {
                containers.Add(new ReconstructedContainer
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
                esmFormIds.Add(record.FormId);
            }
        }
        else
        {
            // With accessor, read full record data for CNTO subrecord parsing
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in containerRecords)
                {
                    var container = ReconstructContainerFromAccessor(record, buffer);
                    if (container != null)
                    {
                        containers.Add(container);
                        esmFormIds.Add(container.FormId);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge containers from runtime struct reading
        if (_runtimeReader != null)
        {
            // Enrich ESM containers with runtime contents (current game state)
            var runtimeEnrichments = new Dictionary<uint, ReconstructedContainer>();
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x1B || !esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var rtc = _runtimeReader.ReadRuntimeContainer(entry);
                if (rtc != null && rtc.Contents.Count > 0)
                {
                    runtimeEnrichments[entry.FormId] = rtc;
                }
            }

            if (runtimeEnrichments.Count > 0)
            {
                for (var i = 0; i < containers.Count; i++)
                {
                    if (runtimeEnrichments.TryGetValue(containers[i].FormId, out var rtc))
                    {
                        containers[i] = containers[i] with
                        {
                            Contents = rtc.Contents,
                            Flags = rtc.Flags,
                            ModelPath = containers[i].ModelPath ?? rtc.ModelPath,
                            Script = containers[i].Script ?? rtc.Script
                        };
                    }
                }

                Logger.Instance.Debug(
                    $"  [Semantic] Enriched {runtimeEnrichments.Count} ESM containers with runtime contents");
            }

            // Add runtime-only containers (not in ESM)
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x1B || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var container = _runtimeReader.ReadRuntimeContainer(entry);
                if (container != null)
                {
                    containers.Add(container);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} containers from runtime struct reading " +
                    $"(total: {containers.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return containers;
    }

    private ReconstructedContainer? ReconstructContainerFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedContainer
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        uint? script = null;
        var contents = new List<InventoryItem>();

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "FULL":
                    fullName = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    script = ReadFormId(subData, record.IsBigEndian);
                    break;
                case "CNTO" when sub.DataLength >= 8:
                    var itemFormId = ReadFormId(subData[..4], record.IsBigEndian);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[4..]);
                    contents.Add(new InventoryItem(itemFormId, count));
                    break;
            }
        }

        return new ReconstructedContainer
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Script = script,
            Contents = contents,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
