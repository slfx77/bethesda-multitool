using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class EffectRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{

    #region Enchantments

    /// <summary>
    ///     Parse all Enchantment (ENCH) records.
    /// </summary>
    internal List<EnchantmentRecord> ParseEnchantments()
    {
        var enchantments = new List<EnchantmentRecord>();

        if (Context.Accessor == null)
        {
            return enchantments;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in Context.GetRecordsByType("ENCH"))
            {
                var recordData = Context.ReadRecordData(record, buffer);
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
                                Context.FormIdToEditorId[record.FormId] = editorId;
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
                            currentEffectId =
                                RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, 4), record.IsBigEndian);
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

        Context.MergeRuntimeRecords(enchantments, 0x13, e => e.FormId,
            (reader, entry) => reader.ReadRuntimeEnchantment(entry), "enchantments");

        return enchantments;
    }

    #endregion

    #region Base Effects

    /// <summary>
    ///     Parse all Base Effect (MGEF) records.
    /// </summary>
    internal List<BaseEffectRecord> ParseBaseEffects()
    {
        var effects = new List<BaseEffectRecord>();

        if (Context.Accessor == null)
        {
            return effects;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in Context.GetRecordsByType("MGEF"))
            {
                var recordData = Context.ReadRecordData(record, buffer);
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
                uint? lightFormId = null, effectShaderFormId = null, enchantEffectFormId = null;
                uint? castingSoundFormId = null, boltSoundFormId = null, hitSoundFormId = null, areaSoundFormId = null;
                float? projectileSpeed = null, ceEnchantFactor = null, ceBarterFactor = null;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                Context.FormIdToEditorId[record.FormId] = editorId;
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
                                var light = SubrecordDataReader.GetUInt32(fields, "Light");
                                if (light != 0) lightFormId = light;
                                projectileSpeed = SubrecordDataReader.GetFloat(fields, "ProjectileSpeed");
                                var shader = SubrecordDataReader.GetUInt32(fields, "EffectShader");
                                if (shader != 0) effectShaderFormId = shader;
                                var enchEff = SubrecordDataReader.GetUInt32(fields, "EnchantEffect");
                                if (enchEff != 0) enchantEffectFormId = enchEff;
                                var castSnd = SubrecordDataReader.GetUInt32(fields, "CastingSound");
                                if (castSnd != 0) castingSoundFormId = castSnd;
                                var boltSnd = SubrecordDataReader.GetUInt32(fields, "BoltSound");
                                if (boltSnd != 0) boltSoundFormId = boltSnd;
                                var hitSnd = SubrecordDataReader.GetUInt32(fields, "HitSound");
                                if (hitSnd != 0) hitSoundFormId = hitSnd;
                                var areaSnd = SubrecordDataReader.GetUInt32(fields, "AreaSound");
                                if (areaSnd != 0) areaSoundFormId = areaSnd;
                                ceEnchantFactor =
                                    SubrecordDataReader.GetFloat(fields, "ConstantEffectEnchantmentFactor");
                                ceBarterFactor = SubrecordDataReader.GetFloat(fields, "ConstantEffectBarterFactor");
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
                    LightFormId = lightFormId,
                    ProjectileSpeed = projectileSpeed,
                    EffectShaderFormId = effectShaderFormId,
                    EnchantEffectFormId = enchantEffectFormId,
                    CastingSoundFormId = castingSoundFormId,
                    BoltSoundFormId = boltSoundFormId,
                    HitSoundFormId = hitSoundFormId,
                    AreaSoundFormId = areaSoundFormId,
                    CEEnchantFactor = ceEnchantFactor,
                    CEBarterFactor = ceBarterFactor,
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

        Context.MergeRuntimeRecords(effects, 0x10, e => e.FormId,
            (reader, entry) => reader.ReadRuntimeBaseEffect(entry), "base effects");

        return effects;
    }

    #endregion

    #region Perks

    /// <summary>
    ///     Parse all Perk records from the scan result.
    /// </summary>
    internal List<PerkRecord> ParsePerks()
    {
        var perks = new List<PerkRecord>();
        var perkRecords = Context.GetRecordsByType("PERK").ToList();

        if (Context.Accessor == null)
        {
            foreach (var record in perkRecords)
            {
                perks.Add(new PerkRecord
                {
                    FormId = record.FormId,
                    EditorId = Context.GetEditorId(record.FormId),
                    FullName = Context.FindFullNameNear(record.Offset),
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
                    var perk = ParsePerkFromAccessor(record, buffer);
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

        Context.MergeRuntimeRecords(perks, 0x56, p => p.FormId,
            (reader, entry) => reader.ReadRuntimePerk(entry), "perks");

        return perks;
    }

    private PerkRecord? ParsePerkFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new PerkRecord
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
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
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
    ///     Parse all Spell records from the scan result.
    /// </summary>
    internal List<SpellRecord> ParseSpells()
    {
        var spells = new List<SpellRecord>();
        var spellRecords = Context.GetRecordsByType("SPEL").ToList();

        if (Context.Accessor == null)
        {
            foreach (var record in spellRecords)
            {
                spells.Add(new SpellRecord
                {
                    FormId = record.FormId,
                    EditorId = Context.GetEditorId(record.FormId),
                    FullName = Context.FindFullNameNear(record.Offset),
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
                    var spell = ParseSpellFromAccessor(record, buffer);
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

        Context.MergeRuntimeRecords(spells, 0x14, s => s.FormId,
            (reader, entry) => reader.ReadRuntimeSpell(entry), "spells");

        return spells;
    }

    private SpellRecord? ParseSpellFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new SpellRecord
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
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
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
