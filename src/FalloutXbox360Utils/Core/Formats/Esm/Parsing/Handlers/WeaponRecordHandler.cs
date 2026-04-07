using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

/// <summary>
///     Handles parsing of WEAP records from ESM data and runtime structs.
/// </summary>
internal sealed class WeaponRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    /// <summary>
    ///     Parse all Weapon records from the scan result.
    ///     Uses two-track approach: ESM records for subrecord detail + runtime C++ structs
    ///     for records not found as raw ESM data (typically hundreds of weapons vs ~34 ESM records).
    /// </summary>
    internal List<WeaponRecord> ParseWeapons()
    {
        var weapons = ParseRecordList("WEAP", 4096, ParseWeaponFromAccessor,
            record => new WeaponRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(weapons, 0x28, w => w.FormId,
            (reader, entry) => reader.ReadRuntimeWeapon(entry), "weapons");

        return weapons;
    }

    private WeaponRecord? ParseWeaponFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new WeaponRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? modelPath = null;
        string? embeddedWeaponNode = null;
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
        uint skill = 0;
        uint strengthRequirement = 0;
        uint skillRequirement = 0;
        var equipmentType = EquipmentType.None;

        // Mod slot data (DNAM)
        var modSlots = new List<WeaponModSlot>();

        // Phase 4: VATS Attack data
        VatsAttackData? vatsAttack = null;

        // Phase 5: Art assets
        string? inventoryIconPath = null;
        string? messageIconPath = null;
        string? shellCasingModelPath = null;
        uint? repairItemListFormId = null;
        uint? impactDataSetFormIdEsm = null;

        // Phase 6: Modified model variants
        // INAM = Impact Data Set; WNAM = base 1st-person STAT; WNM1..WNM7 = modded 1st-person STATs;
        // MWD1..MWD7 = modded 3rd-person world model paths.
        // Combination order: 1, 2, 3, 1+2, 1+3, 2+3, 1+2+3
        uint? firstPersonObjectBase = null;
        var firstPersonModObjects = new uint?[7];
        var modWorldMeshes = new string?[7];

        // Phase 3: Additional DNAM fields previously dropped by the parser
        float damageToWeaponMult = 1.0f;
        uint resistance = 0;
        var ironSightUseMult = 1.0f;
        float ammoRegenRate = 0;
        float killImpulse = 0;
        float killImpulseDistance = 0;
        float semiAutoFireDelayMin = 0;
        float semiAutoFireDelayMax = 0;
        float animShotsPerSecond = 0;
        float animReloadTime = 0;
        float animJamTime = 0;
        byte powerAttackOverrideAnim = 0;
        sbyte modReloadClipAnimation = 0;
        sbyte modFireAnimation = 0;
        float cookTimer = 0;
        float rumbleLeftMotor = 0;
        float rumbleRightMotor = 0;
        float rumbleDuration = 0;
        uint rumblePattern = 0;
        float rumbleWavelength = 0;

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
        uint? modSilencedSound3DFormId = null;
        uint? modSilencedSound2DFormId = null;

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
                case "NNAM":
                    embeddedWeaponNode = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "ENAM" when sub.DataLength == 4:
                    // FNV: ENAM = Ammo FormID (4 bytes)
                    ammoFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "NAM0" when sub.DataLength == 4:
                    // FO3: NAM0 = Ammo FormID (4 bytes) — FO3 uses NAM0 instead of ENAM for ammo
                    ammoFormId ??= RecordParserContext.ReadFormId(subData, record.IsBigEndian);
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
                        weaponType = Enum.IsDefined(typeof(WeaponType), wt)
                            ? (WeaponType)wt
                            : WeaponType.HandToHandMelee;
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
                        skill = SubrecordDataReader.GetUInt32(fields, "Skill");
                        strengthRequirement = SubrecordDataReader.GetUInt32(fields, "StrengthRequirement");
                        skillRequirement = SubrecordDataReader.GetUInt32(fields, "SkillRequirement");

                        // Phase 3: Additional DNAM fields
                        damageToWeaponMult = SubrecordDataReader.GetFloat(fields, "DamageToWeaponMult");
                        resistance = SubrecordDataReader.GetUInt32(fields, "Resistance");
                        ironSightUseMult = SubrecordDataReader.GetFloat(fields, "IronSightUseMult");
                        ammoRegenRate = SubrecordDataReader.GetFloat(fields, "AmmoRegenRate");
                        killImpulse = SubrecordDataReader.GetFloat(fields, "KillImpulse");
                        killImpulseDistance = SubrecordDataReader.GetFloat(fields, "KillImpulseDistance");
                        semiAutoFireDelayMin = SubrecordDataReader.GetFloat(fields, "SemiAutoDelayMin");
                        semiAutoFireDelayMax = SubrecordDataReader.GetFloat(fields, "SemiAutoDelayMax");
                        animShotsPerSecond = SubrecordDataReader.GetFloat(fields, "AnimShotsPerSecond");
                        animReloadTime = SubrecordDataReader.GetFloat(fields, "AnimReloadTime");
                        animJamTime = SubrecordDataReader.GetFloat(fields, "AnimJamTime");
                        powerAttackOverrideAnim = SubrecordDataReader.GetByte(fields, "PowerAttackOverrideAnim");
                        modReloadClipAnimation = (sbyte)SubrecordDataReader.GetByte(fields, "ModReloadClipAnimation");
                        modFireAnimation = (sbyte)SubrecordDataReader.GetByte(fields, "ModFireAnimation");
                        cookTimer = SubrecordDataReader.GetFloat(fields, "CookTimer");
                        rumbleLeftMotor = SubrecordDataReader.GetFloat(fields, "RumbleLeftMotor");
                        rumbleRightMotor = SubrecordDataReader.GetFloat(fields, "RumbleRightMotor");
                        rumbleDuration = SubrecordDataReader.GetFloat(fields, "RumbleDuration");
                        rumblePattern = SubrecordDataReader.GetUInt32(fields, "RumblePattern");
                        rumbleWavelength = SubrecordDataReader.GetFloat(fields, "RumbleWavelength");

                        // Mod slot data (FNV-specific, DNAM size 204)
                        if (sub.DataLength >= 196)
                        {
                            var modActions = new[]
                            {
                                (SubrecordDataReader.GetUInt32(fields, "ModActionOne"),
                                    SubrecordDataReader.GetFloat(fields, "ModActionOneValue"),
                                    SubrecordDataReader.GetFloat(fields, "ModActionOneValueTwo")),
                                (SubrecordDataReader.GetUInt32(fields, "ModActionTwo"),
                                    SubrecordDataReader.GetFloat(fields, "ModActionTwoValue"),
                                    SubrecordDataReader.GetFloat(fields, "ModActionTwoValueTwo")),
                                (SubrecordDataReader.GetUInt32(fields, "ModActionThree"),
                                    SubrecordDataReader.GetFloat(fields, "ModActionThreeValue"),
                                    SubrecordDataReader.GetFloat(fields, "ModActionThreeValueTwo"))
                            };

                            for (var mi = 0; mi < 3; mi++)
                            {
                                var (modAction, modVal, modVal2) = modActions[mi];
                                if (modAction != 0)
                                {
                                    modSlots.Add(new WeaponModSlot
                                    {
                                        SlotIndex = mi + 1,
                                        Action = Enum.IsDefined(typeof(WeaponModAction), modAction)
                                            ? (WeaponModAction)modAction
                                            : WeaponModAction.None,
                                        Value = modVal,
                                        ValueTwo = modVal2
                                    });
                                }
                            }
                        }
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
                case "SNAM" when sub.DataLength == 4:
                    // SNAM appears TWICE in WEAP records: first occurrence is Attack Sound (3D),
                    // second occurrence is Attack Sound (Dist). Each is a 4-byte FormID.
                    if (fireSound3DFormId == null)
                    {
                        fireSound3DFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    }
                    else
                    {
                        fireSoundDistFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
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
                case "ICON":
                    inventoryIconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MICO":
                    messageIconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MOD2":
                    shellCasingModelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "REPL" when sub.DataLength == 4:
                    repairItemListFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "INAM" when sub.DataLength == 4:
                    // Impact Data Set FormID (BGSImpactDataSet)
                    impactDataSetFormIdEsm = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNAM" when sub.DataLength == 4:
                    // Base 1st-person STAT FormID
                    firstPersonObjectBase = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNM1" when sub.DataLength == 4:
                    firstPersonModObjects[0] = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNM2" when sub.DataLength == 4:
                    firstPersonModObjects[1] = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNM3" when sub.DataLength == 4:
                    firstPersonModObjects[2] = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNM4" when sub.DataLength == 4:
                    firstPersonModObjects[3] = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNM5" when sub.DataLength == 4:
                    firstPersonModObjects[4] = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNM6" when sub.DataLength == 4:
                    firstPersonModObjects[5] = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WNM7" when sub.DataLength == 4:
                    firstPersonModObjects[6] = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "MWD1":
                    modWorldMeshes[0] = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MWD2":
                    modWorldMeshes[1] = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MWD3":
                    modWorldMeshes[2] = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MWD4":
                    modWorldMeshes[3] = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MWD5":
                    modWorldMeshes[4] = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MWD6":
                    modWorldMeshes[5] = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MWD7":
                    modWorldMeshes[6] = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "VATS" when sub.DataLength == 20:
                {
                    // VATS Attack subrecord (20 bytes — matches PDB OBJ_WEAP_VATS_SPECIAL):
                    // +0 pVATSSpecialEffect (FormID, 4)
                    // +4 fVATSSpecialAP (float)
                    // +8 fVATSSpecialMultiplier (float)
                    // +12 fVATSSkillRequired (float)
                    // +16 bSilent (1B), +17 bModRequired (1B), +18 cFlags (1B), +19 padding
                    var vatsEffect = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    var vatsAp = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[4..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[4..]);
                    var vatsDamMult = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[8..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[8..]);
                    var vatsSkill = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[12..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[12..]);
                    vatsAttack = new VatsAttackData
                    {
                        EffectFormId = vatsEffect,
                        ActionPointCost = float.IsNormal(vatsAp) || vatsAp == 0 ? vatsAp : 0,
                        DamageMultiplier = float.IsNormal(vatsDamMult) || vatsDamMult == 0 ? vatsDamMult : 0,
                        SkillRequired = float.IsNormal(vatsSkill) || vatsSkill == 0 ? vatsSkill : 0,
                        IsSilent = subData[16] != 0,
                        RequiresMod = subData[17] != 0,
                        ExtraFlags = subData[18]
                    };
                    break;
                }
                case "WMS1" when sub.DataLength == 4:
                    // Modded weapon attack sound (3D) — used when a silencer mod is attached
                    modSilencedSound3DFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "WMS2" when sub.DataLength == 4:
                    // Modded weapon attack sound (2D) — used when a silencer mod is attached
                    modSilencedSound2DFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
            }
        }

        return new WeaponRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            ModelPath = modelPath,
            EmbeddedWeaponNode = embeddedWeaponNode,
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
            Skill = skill,
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
            ModSilencedSound3DFormId = modSilencedSound3DFormId,
            ModSilencedSound2DFormId = modSilencedSound2DFormId,
            ModSlots = modSlots,
            DamageToWeaponMult = damageToWeaponMult,
            Resistance = resistance,
            IronSightUseMult = ironSightUseMult,
            AmmoRegenRate = ammoRegenRate,
            KillImpulse = killImpulse,
            KillImpulseDistance = killImpulseDistance,
            SemiAutoFireDelayMin = semiAutoFireDelayMin,
            SemiAutoFireDelayMax = semiAutoFireDelayMax,
            AnimShotsPerSecond = animShotsPerSecond,
            AnimReloadTime = animReloadTime,
            AnimJamTime = animJamTime,
            PowerAttackOverrideAnim = powerAttackOverrideAnim,
            ModReloadClipAnimation = modReloadClipAnimation,
            ModFireAnimation = modFireAnimation,
            CookTimer = cookTimer,
            RumbleLeftMotor = rumbleLeftMotor,
            RumbleRightMotor = rumbleRightMotor,
            RumbleDuration = rumbleDuration,
            RumblePattern = rumblePattern,
            RumbleWavelength = rumbleWavelength,
            VatsAttack = vatsAttack,
            InventoryIconPath = inventoryIconPath,
            MessageIconPath = messageIconPath,
            ShellCasingModelPath = shellCasingModelPath,
            RepairItemListFormId = repairItemListFormId,
            ImpactDataSetFormId = impactDataSetFormIdEsm ?? null,
            ModelVariants = BuildModelVariants(firstPersonObjectBase, firstPersonModObjects, modWorldMeshes),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    /// <summary>
    ///     Build the WeaponModelVariants list from the parsed WNAM/WNM*/MWD* subrecords.
    ///     The 7 array indices correspond to mod combinations: Mod1, Mod2, Mod3, Mod1+2, Mod1+3, Mod2+3, Mod1+2+3.
    /// </summary>
    private static List<WeaponModelVariant> BuildModelVariants(
        uint? firstPersonObjectBase,
        uint?[] firstPersonModObjects,
        string?[] modWorldMeshes)
    {
        var result = new List<WeaponModelVariant>();
        var combinations = new[]
        {
            WeaponModCombination.Mod1, WeaponModCombination.Mod2, WeaponModCombination.Mod3,
            WeaponModCombination.Mod12, WeaponModCombination.Mod13, WeaponModCombination.Mod23,
            WeaponModCombination.Mod123
        };

        // Base entry (no mods) — only added if WNAM is present
        if (firstPersonObjectBase is > 0)
        {
            result.Add(new WeaponModelVariant
            {
                Combination = WeaponModCombination.None,
                FirstPersonObjectFormId = firstPersonObjectBase
            });
        }

        for (var i = 0; i < 7; i++)
        {
            if (firstPersonModObjects[i] is null or 0 && string.IsNullOrEmpty(modWorldMeshes[i]))
            {
                continue;
            }

            result.Add(new WeaponModelVariant
            {
                Combination = combinations[i],
                ThirdPersonModelPath = modWorldMeshes[i],
                FirstPersonObjectFormId = firstPersonModObjects[i]
            });
        }

        return result;
    }

    /// <summary>
    ///     Enrich weapon records with projectile physics data parsed from PROJ ESM records.
    ///     This is the ESM-only path — no DMP required. Parses the PROJ DATA subrecord
    ///     using the existing schema to extract speed, gravity, range, etc.
    /// </summary>
    internal void EnrichWeaponsWithEsmProjectileData(List<WeaponRecord> weapons)
    {
        if (Context.Accessor == null || weapons.Count == 0)
        {
            return;
        }

        // Collect unique projectile FormIDs that need enrichment
        var neededProjIds = new HashSet<uint>();
        foreach (var weapon in weapons)
        {
            if (weapon.ProjectileFormId.HasValue && weapon.ProjectileData == null)
            {
                neededProjIds.Add(weapon.ProjectileFormId.Value);
            }
        }

        if (neededProjIds.Count == 0)
        {
            return;
        }

        // Parse PROJ records from ESM and build lookup
        var projDataLookup = new Dictionary<uint, ProjectilePhysicsData>();
        var buffer = new byte[4096];

        foreach (var projRecord in Context.GetRecordsByType("PROJ"))
        {
            if (!neededProjIds.Contains(projRecord.FormId))
            {
                continue;
            }

            var recordData = Context.ReadRecordData(projRecord, buffer);
            if (recordData == null)
            {
                continue;
            }

            var (data, dataSize) = recordData.Value;
            string? modelPath = null;
            string? fullName = null;
            ProjectilePhysicsData? projPhysics = null;

            foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, projRecord.IsBigEndian))
            {
                var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                switch (sub.Signature)
                {
                    case "MODL":
                        modelPath = EsmStringUtils.ReadNullTermString(subData);
                        break;
                    case "FULL":
                        fullName = EsmStringUtils.ReadNullTermString(subData);
                        break;
                    case "DATA" when sub.DataLength >= 64:
                    {
                        var fields =
                            SubrecordDataReader.ReadFields("DATA", "PROJ", subData, projRecord.IsBigEndian);
                        if (fields.Count > 0)
                        {
                            projPhysics = new ProjectilePhysicsData
                            {
                                Flags = SubrecordDataReader.GetUInt32(fields, "FlagsAndType"),
                                Gravity = SubrecordDataReader.GetFloat(fields, "Gravity"),
                                Speed = SubrecordDataReader.GetFloat(fields, "Speed"),
                                Range = SubrecordDataReader.GetFloat(fields, "Range"),
                                LightFormId = NullIfZero(SubrecordDataReader.GetUInt32(fields, "Light")),
                                MuzzleFlashLightFormId =
                                    NullIfZero(SubrecordDataReader.GetUInt32(fields, "MuzzleFlashLight")),
                                TracerChance = SubrecordDataReader.GetFloat(fields, "TracerChance"),
                                ExplosionProximity =
                                    SubrecordDataReader.GetFloat(fields, "ExplosionAltTriggerProximity"),
                                ExplosionTimer =
                                    SubrecordDataReader.GetFloat(fields, "ExplosionAltTriggerTimer"),
                                ExplosionFormId =
                                    NullIfZero(SubrecordDataReader.GetUInt32(fields, "Explosion")),
                                ActiveSoundLoopFormId =
                                    NullIfZero(SubrecordDataReader.GetUInt32(fields, "Sound")),
                                MuzzleFlashDuration =
                                    SubrecordDataReader.GetFloat(fields, "MuzzleFlashDuration"),
                                FadeOutTime = SubrecordDataReader.GetFloat(fields, "FadeDuration"),
                                Force = SubrecordDataReader.GetFloat(fields, "ImpactForce"),
                                CountdownSoundFormId =
                                    NullIfZero(SubrecordDataReader.GetUInt32(fields, "SoundCountdown")),
                                DeactivateSoundFormId =
                                    NullIfZero(SubrecordDataReader.GetUInt32(fields, "SoundDisable")),
                                DefaultWeaponSourceFormId =
                                    NullIfZero(SubrecordDataReader.GetUInt32(fields, "DefaultWeaponSource")),
                                RotationX = SubrecordDataReader.GetFloat(fields, "RotationX"),
                                RotationY = SubrecordDataReader.GetFloat(fields, "RotationY"),
                                RotationZ = SubrecordDataReader.GetFloat(fields, "RotationZ"),
                                BounceMultiplier = SubrecordDataReader.GetFloat(fields, "BouncyMult")
                            };
                        }

                        break;
                    }
                }
            }

            if (projPhysics != null)
            {
                projDataLookup[projRecord.FormId] = projPhysics with
                {
                    ModelPath = modelPath ?? projPhysics.ModelPath,
                    FullName = fullName ?? projPhysics.FullName
                };
            }
        }

        if (projDataLookup.Count == 0)
        {
            return;
        }

        // Merge into weapons
        var enrichedCount = 0;
        for (var i = 0; i < weapons.Count; i++)
        {
            var weapon = weapons[i];
            if (weapon.ProjectileData != null || !weapon.ProjectileFormId.HasValue)
            {
                continue;
            }

            if (projDataLookup.TryGetValue(weapon.ProjectileFormId.Value, out var projData))
            {
                weapons[i] = weapon with { ProjectileData = projData };
                enrichedCount++;
            }
        }

        if (enrichedCount > 0)
        {
            Logger.Instance.Debug(
                $"  [Semantic] Enriched {enrichedCount}/{weapons.Count} weapons with ESM projectile data " +
                $"({projDataLookup.Count} PROJ records parsed)");
        }
    }

    private static uint? NullIfZero(uint value)
    {
        return value == 0 ? null : value;
    }

    /// <summary>
    ///     Enrich weapon records with projectile physics data (gravity, speed, range,
    ///     explosion, in-flight sounds) read from the BGSProjectile runtime struct.
    /// </summary>
    internal void EnrichWeaponsWithProjectileData(List<WeaponRecord> weapons)
    {
        if (Context.RuntimeReader == null || weapons.Count == 0)
        {
            return;
        }

        // Build: projectile FormID -> TesFormOffset (from runtime EditorID hash table)
        var projectileEntries = new Dictionary<uint, RuntimeEditorIdEntry>();
        foreach (var entry in Context.ScanResult.RuntimeEditorIds)
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

            var projData = Context.RuntimeReader.ReadProjectilePhysics(
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
