using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

/// <summary>
///     Handles reconstruction of WEAP records from ESM data and runtime structs.
/// </summary>
internal sealed class WeaponRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    /// <summary>
    ///     Reconstruct all Weapon records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data (typically hundreds of weapons vs ~34 ESM records).
    /// </summary>
    internal List<WeaponRecord> ReconstructWeapons()
    {
        var weapons = new List<WeaponRecord>();
        var weaponRecords = _context.GetRecordsByType("WEAP").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in weaponRecords)
            {
                weapons.Add(new WeaponRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
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
                foreach (var record in weaponRecords)
                {
                    var weapon = ReconstructWeaponFromAccessor(record, buffer);
                    if (weapon != null)
                    {
                        weapons.Add(weapon);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        _context.MergeRuntimeRecords(weapons, 0x28, w => w.FormId,
            (reader, entry) => reader.ReadRuntimeWeapon(entry), "weapons");

        return weapons;
    }

    private WeaponRecord? ReconstructWeaponFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new WeaponRecord
            {
                FormId = record.FormId,
                EditorId = _context.GetEditorId(record.FormId),
                FullName = _context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        ObjectBounds? bounds = null;

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
        byte flags = 0;
        var handGrip = HandGripAnimation.Default;
        byte ammoPerShot = 1;
        var reloadAnim = ReloadAnimation.ReloadA;
        float minSpread = 0;
        float spread = 0;
        float drift = 0;
        float ironSightFov = 0;
        uint? ammoFormId = null;
        uint? projectileFormId = null;
        byte vatsToHitChance = 0;
        var attackAnim = AttackAnimation.Default;
        byte numProjectiles = 1;
        float minRange = 0;
        float maxRange = 0;
        var onHit = OnHitBehavior.Normal;
        uint flagsEx = 0;
        float attackMultiplier = 1;
        float shotsPerSec = 1;
        float actionPoints = 0;
        float aimArc = 0;
        float limbDamageMult = 1;
        uint strengthRequirement = 0;
        uint skillRequirement = 0;
        var equipmentType = EquipmentType.None;

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
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "ENAM" when sub.DataLength == 4:
                    ammoFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 15:
                {
                    var fields = SubrecordDataReader.ReadFields("DATA", "WEAP", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        value = SubrecordDataReader.GetInt32(fields, "Value");
                        health = SubrecordDataReader.GetInt32(fields, "Health");
                        weight = SubrecordDataReader.GetFloat(fields, "Weight");
                        damage = SubrecordDataReader.GetInt16(fields, "Damage");
                        clipSize = SubrecordDataReader.GetByte(fields, "ClipSize");
                    }

                    break;
                }
                case "DNAM" when sub.DataLength >= 64:
                {
                    var fields = SubrecordDataReader.ReadFields("DNAM", "WEAP", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        var wt = SubrecordDataReader.GetByte(fields, "WeaponType");
                        animationType = wt;
                        weaponType = (WeaponType)(wt <= 11 ? wt : 0);
                        speed = SubrecordDataReader.GetFloat(fields, "Speed");
                        reach = SubrecordDataReader.GetFloat(fields, "Reach");
                        flags = SubrecordDataReader.GetByte(fields, "Flags");
                        var grip = SubrecordDataReader.GetByte(fields, "HandGripAnim");
                        handGrip = Enum.IsDefined(typeof(HandGripAnimation), grip)
                            ? (HandGripAnimation)grip
                            : HandGripAnimation.Default;
                        ammoPerShot = SubrecordDataReader.GetByte(fields, "AmmoPerShot");
                        var reload = SubrecordDataReader.GetByte(fields, "ReloadAnim");
                        reloadAnim = reload <= 10
                            ? (ReloadAnimation)reload
                            : ReloadAnimation.ReloadA;
                        minSpread = SubrecordDataReader.GetFloat(fields, "MinSpread");
                        spread = SubrecordDataReader.GetFloat(fields, "Spread");
                        drift = SubrecordDataReader.GetFloat(fields, "Drift");
                        ironSightFov = SubrecordDataReader.GetFloat(fields, "IronFov");
                        var projId = SubrecordDataReader.GetUInt32(fields, "Projectile");
                        if (projId != 0)
                        {
                            projectileFormId = projId;
                        }

                        vatsToHitChance = SubrecordDataReader.GetByte(fields, "VatToHitChance");
                        var attack = SubrecordDataReader.GetByte(fields, "AttackAnim");
                        attackAnim = Enum.IsDefined(typeof(AttackAnimation), attack)
                            ? (AttackAnimation)attack
                            : AttackAnimation.Default;
                        numProjectiles = SubrecordDataReader.GetByte(fields, "NumProjectiles");
                        minRange = SubrecordDataReader.GetFloat(fields, "MinRange");
                        maxRange = SubrecordDataReader.GetFloat(fields, "MaxRange");
                        onHit = (OnHitBehavior)SubrecordDataReader.GetUInt32(fields, "HitBehavior");
                        flagsEx = SubrecordDataReader.GetUInt32(fields, "FlagsEx");
                        attackMultiplier = SubrecordDataReader.GetFloat(fields, "AttackMult");
                        shotsPerSec = SubrecordDataReader.GetFloat(fields, "ShotsPerSec");
                        actionPoints = SubrecordDataReader.GetFloat(fields, "ActionPoints");
                        aimArc = SubrecordDataReader.GetFloat(fields, "AimArc");
                        limbDamageMult = SubrecordDataReader.GetFloat(fields, "LimbDamageMult");
                        strengthRequirement = SubrecordDataReader.GetUInt32(fields, "StrengthRequirement");
                        skillRequirement = SubrecordDataReader.GetUInt32(fields, "SkillRequirement");
                    }

                    break;
                }
                case "ETYP" when sub.DataLength == 4:
                {
                    var etypValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData)
                        : BinaryPrimitives.ReadInt32LittleEndian(subData);
                    if (etypValue >= -1 && etypValue <= 13)
                    {
                        equipmentType = (EquipmentType)etypValue;
                    }

                    break;
                }
                case "CRDT" when sub.DataLength >= 16:
                {
                    var fields = SubrecordDataReader.ReadFields("CRDT", null, subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        criticalDamage = SubrecordDataReader.GetInt16(fields, "CriticalDamage");
                        criticalChance = SubrecordDataReader.GetFloat(fields, "CriticalChanceMult");
                        criticalEffectFormId = SubrecordDataReader.GetUInt32(fields, "CriticalEffect");
                    }

                    break;
                }
                // Sound subrecords - each is a single FormID [SOUN]
                case "YNAM" when sub.DataLength == 4:
                    pickupSoundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ZNAM" when sub.DataLength == 4:
                    putdownSoundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength >= 4:
                    // SNAM is paired: Shoot 3D FormID + Shoot Dist FormID (8 bytes)
                    fireSound3DFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    if (sub.DataLength >= 8)
                    {
                        fireSoundDistFormId = RecordParserContext.ReadFormId(subData[4..], record.IsBigEndian);
                    }

                    break;
                case "XNAM" when sub.DataLength == 4:
                    fireSound2DFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TNAM" when sub.DataLength == 4:
                    dryFireSoundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "UNAM" when sub.DataLength == 4:
                    idleSoundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM9" when sub.DataLength == 4:
                    equipSoundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM8" when sub.DataLength == 4:
                    unequipSoundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new WeaponRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            Bounds = bounds,
            Value = value,
            Health = health,
            Weight = weight,
            Damage = damage,
            ClipSize = clipSize,
            WeaponType = weaponType,
            AnimationType = animationType,
            Speed = speed,
            Reach = reach,
            Flags = flags,
            HandGrip = handGrip,
            AmmoPerShot = ammoPerShot,
            ReloadAnim = reloadAnim,
            MinSpread = minSpread,
            Spread = spread,
            Drift = drift,
            IronSightFov = ironSightFov,
            AmmoFormId = ammoFormId,
            ProjectileFormId = projectileFormId,
            VatsToHitChance = vatsToHitChance,
            AttackAnim = attackAnim,
            NumProjectiles = numProjectiles,
            MinRange = minRange,
            MaxRange = maxRange,
            OnHit = onHit,
            FlagsEx = flagsEx,
            AttackMultiplier = attackMultiplier,
            ShotsPerSec = shotsPerSec,
            ActionPoints = actionPoints,
            AimArc = aimArc,
            LimbDamageMult = limbDamageMult,
            StrengthRequirement = strengthRequirement,
            SkillRequirement = skillRequirement,
            EquipmentType = equipmentType,
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
    internal void EnrichWeaponsWithProjectileData(List<WeaponRecord> weapons)
    {
        if (_context.RuntimeReader == null || weapons.Count == 0)
        {
            return;
        }

        // Build: projectile FormID -> TesFormOffset (from runtime EditorID hash table)
        var projectileEntries = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in _context.ScanResult.RuntimeEditorIds)
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

            var projData = _context.RuntimeReader.ReadProjectilePhysics(
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
}
