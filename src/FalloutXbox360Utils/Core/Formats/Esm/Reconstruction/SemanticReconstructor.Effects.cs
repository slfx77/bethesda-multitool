using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class SemanticReconstructor
{
    #region Enchantments

    /// <summary>
    ///     Reconstruct all Enchantment (ENCH) records.
    /// </summary>
    public List<ReconstructedEnchantment> ReconstructEnchantments()
    {
        var enchantments = new List<ReconstructedEnchantment>();

        if (_accessor == null)
        {
            return enchantments;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("ENCH"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? fullName = null;
                uint enchantType = 0, chargeAmount = 0, enchantCost = 0;
                byte flags = 0;
                var effects = new List<EnchantmentEffect>();
                uint currentEffectId = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ENIT" when sub.DataLength >= 12:
                            {
                                var span = data.AsSpan(sub.DataOffset);
                                if (record.IsBigEndian)
                                {
                                    enchantType = BinaryPrimitives.ReadUInt32BigEndian(span);
                                    chargeAmount = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                    enchantCost = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                }
                                else
                                {
                                    enchantType = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                    chargeAmount = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                    enchantCost = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                }

                                if (sub.DataLength >= 13)
                                {
                                    flags = data[sub.DataOffset + 12];
                                }

                                break;
                            }
                        case "EFID" when sub.DataLength >= 4:
                            currentEffectId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sub.DataOffset));
                            break;
                        case "EFIT" when sub.DataLength >= 12:
                            {
                                var span = data.AsSpan(sub.DataOffset);
                                float magnitude;
                                uint area, duration, type;
                                int actorValue;
                                if (record.IsBigEndian)
                                {
                                    magnitude = BinaryPrimitives.ReadSingleBigEndian(span);
                                    area = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                    duration = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                    type = sub.DataLength >= 16 ? BinaryPrimitives.ReadUInt32BigEndian(span[12..]) : 0;
                                    actorValue = sub.DataLength >= 20
                                        ? BinaryPrimitives.ReadInt32BigEndian(span[16..])
                                        : -1;
                                }
                                else
                                {
                                    magnitude = BinaryPrimitives.ReadSingleLittleEndian(span);
                                    area = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                    duration = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                    type = sub.DataLength >= 16 ? BinaryPrimitives.ReadUInt32LittleEndian(span[12..]) : 0;
                                    actorValue = sub.DataLength >= 20
                                        ? BinaryPrimitives.ReadInt32LittleEndian(span[16..])
                                        : -1;
                                }

                                effects.Add(new EnchantmentEffect
                                {
                                    EffectFormId = currentEffectId,
                                    Magnitude = magnitude,
                                    Area = area,
                                    Duration = duration,
                                    Type = type,
                                    ActorValue = actorValue
                                });
                                break;
                            }
                    }
                }

                enchantments.Add(new ReconstructedEnchantment
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    EnchantType = enchantType,
                    ChargeAmount = chargeAmount,
                    EnchantCost = enchantCost,
                    Flags = flags,
                    Effects = effects,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return enchantments;
    }

    #endregion

    #region Base Effects

    /// <summary>
    ///     Reconstruct all Base Effect (MGEF) records.
    /// </summary>
    public List<ReconstructedBaseEffect> ReconstructBaseEffects()
    {
        var effects = new List<ReconstructedBaseEffect>();

        if (_accessor == null)
        {
            return effects;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in GetRecordsByType("MGEF"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, description = null;
                string? icon = null, modelPath = null;
                uint flags = 0, associatedItem = 0, archetype = 0, projectile = 0, explosion = 0;
                float baseCost = 0;
                int magicSchool = -1, resistValue = -1, actorValue = -1;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description =
                                EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset,
                                sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 36:
                            {
                                var span = data.AsSpan(sub.DataOffset);
                                if (record.IsBigEndian)
                                {
                                    flags = BinaryPrimitives.ReadUInt32BigEndian(span);
                                    baseCost = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                    associatedItem = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                    magicSchool = BinaryPrimitives.ReadInt32BigEndian(span[12..]);
                                    resistValue = BinaryPrimitives.ReadInt32BigEndian(span[16..]);
                                    archetype = BinaryPrimitives.ReadUInt32BigEndian(span[24..]);
                                    actorValue = BinaryPrimitives.ReadInt32BigEndian(span[28..]);
                                    if (sub.DataLength >= 44)
                                    {
                                        projectile = BinaryPrimitives.ReadUInt32BigEndian(span[36..]);
                                    }

                                    if (sub.DataLength >= 48)
                                    {
                                        explosion = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
                                    }
                                }
                                else
                                {
                                    flags = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                    baseCost = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                    associatedItem = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                    magicSchool = BinaryPrimitives.ReadInt32LittleEndian(span[12..]);
                                    resistValue = BinaryPrimitives.ReadInt32LittleEndian(span[16..]);
                                    archetype = BinaryPrimitives.ReadUInt32LittleEndian(span[24..]);
                                    actorValue = BinaryPrimitives.ReadInt32LittleEndian(span[28..]);
                                    if (sub.DataLength >= 44)
                                    {
                                        projectile = BinaryPrimitives.ReadUInt32LittleEndian(span[36..]);
                                    }

                                    if (sub.DataLength >= 48)
                                    {
                                        explosion = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                                    }
                                }

                                break;
                            }
                    }
                }

                effects.Add(new ReconstructedBaseEffect
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Flags = flags,
                    BaseCost = baseCost,
                    AssociatedItem = associatedItem,
                    MagicSchool = magicSchool,
                    ResistValue = resistValue,
                    Archetype = archetype,
                    ActorValue = actorValue,
                    Projectile = projectile,
                    Explosion = explosion,
                    Icon = icon,
                    ModelPath = modelPath,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return effects;
    }

    #endregion

    #region Projectiles

    /// <summary>
    ///     Reconstruct all Projectile (PROJ) records.
    /// </summary>
    public List<ReconstructedProjectile> ReconstructProjectiles()
    {
        var projectiles = new List<ReconstructedProjectile>();

        if (_accessor == null)
        {
            return projectiles;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("PROJ"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, modelPath = null;
                ushort projFlags = 0, projType = 0;
                float gravity = 0, speed = 0, range = 0;
                float muzzleFlashDuration = 0, fadeDuration = 0, impactForce = 0, timer = 0;
                uint light = 0, muzzleFlashLight = 0, explosion = 0, sound = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset,
                                sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 52:
                            {
                                var span = data.AsSpan(sub.DataOffset);
                                if (record.IsBigEndian)
                                {
                                    projFlags = BinaryPrimitives.ReadUInt16BigEndian(span);
                                    projType = BinaryPrimitives.ReadUInt16BigEndian(span[2..]);
                                    gravity = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                    speed = BinaryPrimitives.ReadSingleBigEndian(span[8..]);
                                    range = BinaryPrimitives.ReadSingleBigEndian(span[12..]);
                                    light = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
                                    muzzleFlashLight = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
                                    muzzleFlashDuration = BinaryPrimitives.ReadSingleBigEndian(span[28..]);
                                    fadeDuration = BinaryPrimitives.ReadSingleBigEndian(span[32..]);
                                    impactForce = BinaryPrimitives.ReadSingleBigEndian(span[36..]);
                                    sound = BinaryPrimitives.ReadUInt32BigEndian(span[40..]);
                                    timer = BinaryPrimitives.ReadSingleBigEndian(span[48..]);
                                    explosion = sub.DataLength >= 56 ? BinaryPrimitives.ReadUInt32BigEndian(span[52..]) : 0;
                                }
                                else
                                {
                                    projFlags = BinaryPrimitives.ReadUInt16LittleEndian(span);
                                    projType = BinaryPrimitives.ReadUInt16LittleEndian(span[2..]);
                                    gravity = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                    speed = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
                                    range = BinaryPrimitives.ReadSingleLittleEndian(span[12..]);
                                    light = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
                                    muzzleFlashLight = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
                                    muzzleFlashDuration = BinaryPrimitives.ReadSingleLittleEndian(span[28..]);
                                    fadeDuration = BinaryPrimitives.ReadSingleLittleEndian(span[32..]);
                                    impactForce = BinaryPrimitives.ReadSingleLittleEndian(span[36..]);
                                    sound = BinaryPrimitives.ReadUInt32LittleEndian(span[40..]);
                                    timer = BinaryPrimitives.ReadSingleLittleEndian(span[48..]);
                                    explosion = sub.DataLength >= 56
                                        ? BinaryPrimitives.ReadUInt32LittleEndian(span[52..])
                                        : 0;
                                }

                                break;
                            }
                    }
                }

                projectiles.Add(new ReconstructedProjectile
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    ModelPath = modelPath,
                    Flags = projFlags,
                    ProjectileType = projType,
                    Gravity = gravity,
                    Speed = speed,
                    Range = range,
                    Light = light,
                    MuzzleFlashLight = muzzleFlashLight,
                    Explosion = explosion,
                    Sound = sound,
                    MuzzleFlashDuration = muzzleFlashDuration,
                    FadeDuration = fadeDuration,
                    ImpactForce = impactForce,
                    Timer = timer,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return projectiles;
    }

    #endregion

    #region Explosions

    /// <summary>
    ///     Reconstruct all Explosion (EXPL) records.
    /// </summary>
    public List<ReconstructedExplosion> ReconstructExplosions()
    {
        var explosions = new List<ReconstructedExplosion>();

        if (_accessor == null)
        {
            return explosions;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("EXPL"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, modelPath = null;
                float force = 0, damage = 0, radius = 0, isRadius = 0;
                uint light = 0, sound1 = 0, flags = 0, impactDataSet = 0, sound2 = 0, enchantment = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset,
                                sub.DataLength));
                            break;
                        case "EITM" when sub.DataLength >= 4:
                            enchantment = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sub.DataOffset));
                            break;
                        case "DATA" when sub.DataLength >= 36:
                            {
                                var span = data.AsSpan(sub.DataOffset);
                                if (record.IsBigEndian)
                                {
                                    force = BinaryPrimitives.ReadSingleBigEndian(span);
                                    damage = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                    radius = BinaryPrimitives.ReadSingleBigEndian(span[8..]);
                                    light = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                                    sound1 = BinaryPrimitives.ReadUInt32BigEndian(span[16..]);
                                    flags = BinaryPrimitives.ReadUInt32BigEndian(span[20..]);
                                    isRadius = BinaryPrimitives.ReadSingleBigEndian(span[24..]);
                                    impactDataSet = BinaryPrimitives.ReadUInt32BigEndian(span[28..]);
                                    sound2 = BinaryPrimitives.ReadUInt32BigEndian(span[32..]);
                                }
                                else
                                {
                                    force = BinaryPrimitives.ReadSingleLittleEndian(span);
                                    damage = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                    radius = BinaryPrimitives.ReadSingleLittleEndian(span[8..]);
                                    light = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                                    sound1 = BinaryPrimitives.ReadUInt32LittleEndian(span[16..]);
                                    flags = BinaryPrimitives.ReadUInt32LittleEndian(span[20..]);
                                    isRadius = BinaryPrimitives.ReadSingleLittleEndian(span[24..]);
                                    impactDataSet = BinaryPrimitives.ReadUInt32LittleEndian(span[28..]);
                                    sound2 = BinaryPrimitives.ReadUInt32LittleEndian(span[32..]);
                                }

                                break;
                            }
                    }
                }

                explosions.Add(new ReconstructedExplosion
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    ModelPath = modelPath,
                    Force = force,
                    Damage = damage,
                    Radius = radius,
                    Light = light,
                    Sound1 = sound1,
                    Flags = flags,
                    ISRadius = isRadius,
                    ImpactDataSet = impactDataSet,
                    Sound2 = sound2,
                    Enchantment = enchantment,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return explosions;
    }

    #endregion

    #region Perks

    /// <summary>
    ///     Reconstruct all Perk records from the scan result.
    /// </summary>
    public List<ReconstructedPerk> ReconstructPerks()
    {
        var perks = new List<ReconstructedPerk>();
        var perkRecords = GetRecordsByType("PERK").ToList();

        if (_accessor == null)
        {
            foreach (var record in perkRecords)
            {
                perks.Add(new ReconstructedPerk
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
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in perkRecords)
                {
                    var perk = ReconstructPerkFromAccessor(record, buffer);
                    if (perk != null)
                    {
                        perks.Add(perk);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return perks;
    }

    private ReconstructedPerk? ReconstructPerkFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedPerk
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
        string? description = null;
        string? iconPath = null;
        byte trait = 0;
        byte minLevel = 0;
        byte ranks = 1;
        byte playable = 1;
        var entries = new List<PerkEntry>();

        // Track current entry being built
        byte currentEntryType = 0;
        byte currentEntryRank = 0;
        byte currentEntryPriority = 0;

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
                case "DESC":
                    description = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ICON":
                case "MICO":
                    iconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DATA" when sub.DataLength >= 5:
                    trait = subData[0];
                    minLevel = subData[1];
                    ranks = subData[2];
                    playable = subData[3];
                    break;
                case "PRKE" when sub.DataLength >= 3:
                    // Start new perk entry
                    currentEntryType = subData[0];
                    currentEntryRank = subData[1];
                    currentEntryPriority = subData[2];
                    break;
                case "EPFT" when sub.DataLength >= 1:
                    // Entry point function type - finalize entry
                    entries.Add(new PerkEntry
                    {
                        Type = currentEntryType,
                        Rank = currentEntryRank,
                        Priority = currentEntryPriority
                    });
                    break;
            }
        }

        return new ReconstructedPerk
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Description = description,
            IconPath = iconPath,
            Trait = trait,
            MinLevel = minLevel,
            Ranks = ranks,
            Playable = playable,
            Entries = entries,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Spells

    /// <summary>
    ///     Reconstruct all Spell records from the scan result.
    /// </summary>
    public List<ReconstructedSpell> ReconstructSpells()
    {
        var spells = new List<ReconstructedSpell>();
        var spellRecords = GetRecordsByType("SPEL").ToList();

        if (_accessor == null)
        {
            foreach (var record in spellRecords)
            {
                spells.Add(new ReconstructedSpell
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
                foreach (var record in spellRecords)
                {
                    var spell = ReconstructSpellFromAccessor(record, buffer);
                    if (spell != null)
                    {
                        spells.Add(spell);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return spells;
    }

    private ReconstructedSpell? ReconstructSpellFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new ReconstructedSpell
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
        SpellType type = 0;
        uint cost = 0;
        uint level = 0;
        byte flags = 0;
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
                case "SPIT" when sub.DataLength >= 16:
                    type = (SpellType)(record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData));
                    cost = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    level = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                    flags = subData[12];
                    break;
                case "EFID" when sub.DataLength == 4:
                    effectFormIds.Add(ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new ReconstructedSpell
        {
            FormId = record.FormId,
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            Type = type,
            Cost = cost,
            Level = level,
            Flags = flags,
            EffectFormIds = effectFormIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
