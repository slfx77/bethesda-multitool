using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class EffectRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Enchantments

    /// <summary>
    ///     Reconstruct all Enchantment (ENCH) records.
    /// </summary>
    internal List<EnchantmentRecord> ReconstructEnchantments()
    {
        var enchantments = new List<EnchantmentRecord>();

        if (_context.Accessor == null)
        {
            return enchantments;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("ENCH"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ENIT" when sub.DataLength >= 12:
                        {
                            var fields = SubrecordDataReader.ReadFields("ENIT", "ENCH",
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                enchantType = SubrecordDataReader.GetUInt32(fields, "Type");
                                chargeAmount = SubrecordDataReader.GetUInt32(fields, "ChargeAmount");
                                enchantCost = SubrecordDataReader.GetUInt32(fields, "EnchantCost");
                                flags = SubrecordDataReader.GetByte(fields, "Flags");
                            }

                            break;
                        }
                        case "EFID" when sub.DataLength >= 4:
                            currentEffectId = RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, 4), record.IsBigEndian);
                            break;
                        case "EFIT" when sub.DataLength >= 12:
                        {
                            var fields = SubrecordDataReader.ReadFields("EFIT", null,
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                effects.Add(new EnchantmentEffect
                                {
                                    EffectFormId = currentEffectId,
                                    Magnitude = SubrecordDataReader.GetFloat(fields, "Magnitude"),
                                    Area = SubrecordDataReader.GetUInt32(fields, "Area"),
                                    Duration = SubrecordDataReader.GetUInt32(fields, "Duration"),
                                    Type = SubrecordDataReader.GetUInt32(fields, "Type"),
                                    ActorValue = SubrecordDataReader.GetInt32(fields, "ActorValue", -1)
                                });
                            }

                            break;
                        }
                    }
                }

                enchantments.Add(new EnchantmentRecord
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
    internal List<BaseEffectRecord> ReconstructBaseEffects()
    {
        var effects = new List<BaseEffectRecord>();

        if (_context.Accessor == null)
        {
            return effects;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("MGEF"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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
                            var fields = SubrecordDataReader.ReadFields("DATA", "MGEF",
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                flags = SubrecordDataReader.GetUInt32(fields, "Flags");
                                baseCost = SubrecordDataReader.GetFloat(fields, "BaseCost");
                                associatedItem = SubrecordDataReader.GetUInt32(fields, "AssocItem");
                                magicSchool = SubrecordDataReader.GetInt32(fields, "MagicSchool");
                                resistValue = SubrecordDataReader.GetInt32(fields, "ResistanceValue");
                                archetype = SubrecordDataReader.GetUInt32(fields, "Archtype");
                                actorValue = SubrecordDataReader.GetInt32(fields, "ActorValue");
                            }

                            break;
                        }
                    }
                }

                effects.Add(new BaseEffectRecord
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
    internal List<ProjectileRecord> ReconstructProjectiles()
    {
        var projectiles = new List<ProjectileRecord>();

        if (_context.Accessor == null)
        {
            return projectiles;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("PROJ"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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
                            var fields = SubrecordDataReader.ReadFields("DATA", "PROJ",
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                var flagsAndType = SubrecordDataReader.GetUInt32(fields, "FlagsAndType");
                                projFlags = (ushort)(flagsAndType & 0xFFFF);
                                projType = (ushort)((flagsAndType >> 16) & 0xFFFF);
                                gravity = SubrecordDataReader.GetFloat(fields, "Gravity");
                                speed = SubrecordDataReader.GetFloat(fields, "Speed");
                                range = SubrecordDataReader.GetFloat(fields, "Range");
                                light = SubrecordDataReader.GetUInt32(fields, "Light");
                                muzzleFlashLight = SubrecordDataReader.GetUInt32(fields, "MuzzleFlashLight");
                                explosion = SubrecordDataReader.GetUInt32(fields, "Explosion");
                                sound = SubrecordDataReader.GetUInt32(fields, "Sound");
                                muzzleFlashDuration = SubrecordDataReader.GetFloat(fields, "MuzzleFlashDuration");
                                fadeDuration = SubrecordDataReader.GetFloat(fields, "FadeDuration");
                                impactForce = SubrecordDataReader.GetFloat(fields, "ImpactForce");
                                timer = SubrecordDataReader.GetFloat(fields, "ExplosionAltTriggerTimer");
                            }

                            break;
                        }
                    }
                }

                projectiles.Add(new ProjectileRecord
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
    internal List<ExplosionRecord> ReconstructExplosions()
    {
        var explosions = new List<ExplosionRecord>();

        if (_context.Accessor == null)
        {
            return explosions;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("EXPL"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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
                            enchantment = RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, 4), record.IsBigEndian);
                            break;
                        case "DATA" when sub.DataLength >= 36:
                        {
                            var fields = SubrecordDataReader.ReadFields("DATA", "EXPL",
                                data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                force = SubrecordDataReader.GetFloat(fields, "Force");
                                damage = SubrecordDataReader.GetFloat(fields, "Damage");
                                radius = SubrecordDataReader.GetFloat(fields, "Radius");
                                light = SubrecordDataReader.GetUInt32(fields, "Light");
                                sound1 = SubrecordDataReader.GetUInt32(fields, "Sound1");
                                flags = SubrecordDataReader.GetUInt32(fields, "Flags");
                                isRadius = SubrecordDataReader.GetFloat(fields, "ISRadius");
                                impactDataSet = SubrecordDataReader.GetUInt32(fields, "ImpactDataSet");
                                sound2 = SubrecordDataReader.GetUInt32(fields, "Sound2");
                            }

                            break;
                        }
                    }
                }

                explosions.Add(new ExplosionRecord
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
    internal List<PerkRecord> ReconstructPerks()
    {
        var perks = new List<PerkRecord>();
        var perkRecords = _context.GetRecordsByType("PERK").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in perkRecords)
            {
                perks.Add(new PerkRecord
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

    private PerkRecord? ReconstructPerkFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new PerkRecord
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

        return new PerkRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
    internal List<SpellRecord> ReconstructSpells()
    {
        var spells = new List<SpellRecord>();
        var spellRecords = _context.GetRecordsByType("SPEL").ToList();

        if (_context.Accessor == null)
        {
            foreach (var record in spellRecords)
            {
                spells.Add(new SpellRecord
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

    private SpellRecord? ReconstructSpellFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new SpellRecord
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
                {
                    var fields = SubrecordDataReader.ReadFields("SPIT", "SPEL", subData, record.IsBigEndian);
                    if (fields.Count > 0)
                    {
                        type = (SpellType)SubrecordDataReader.GetUInt32(fields, "Type");
                        cost = SubrecordDataReader.GetUInt32(fields, "Cost");
                        level = SubrecordDataReader.GetUInt32(fields, "Level");
                        flags = SubrecordDataReader.GetByte(fields, "Flags");
                    }

                    break;
                }
                case "EFID" when sub.DataLength == 4:
                    effectFormIds.Add(RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    break;
            }
        }

        return new SpellRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
