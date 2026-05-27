using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Magic;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class EffectRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    #region Enchantments

    /// <summary>
    ///     Parse all Enchantment (ENCH) records.
    /// </summary>
    internal List<EnchantmentRecord> ParseEnchantments()
    {
        var enchantments = ParseAccessorOnly("ENCH", 2048, ParseEnchantmentFromAccessor);

        Context.MergeRuntimeRecords(enchantments, 0x13, e => e.FormId,
            (reader, entry) => reader.ReadRuntimeEnchantment(entry), "enchantments");

        return enchantments;
    }

    private EnchantmentRecord? ParseEnchantmentFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
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
                    if (SubrecordSchemaView.TryRead("ENIT", "ENCH",
                            data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian) is { } v)
                    {
                        enchantType = v.UInt32("Type");
                        chargeAmount = v.UInt32("ChargeAmount");
                        enchantCost = v.UInt32("EnchantCost");
                        flags = v.Byte("Flags");
                    }

                    break;
                }
                case "EFID" when sub.DataLength >= 4:
                    currentEffectId =
                        RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, 4), record.IsBigEndian);
                    break;
                case "EFIT" when sub.DataLength >= 12:
                {
                    if (SubrecordSchemaView.TryRead("EFIT", null,
                            data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian) is { } v)
                    {
                        var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
                        var magnitude = GameStatNormalizer.EffectMagnitude(subData, record.IsBigEndian);
                        var area = v.UInt32("Area");
                        var duration = v.UInt32("Duration");
                        var effectTargetType = v.UInt32("Type");
                        var actorValue = v.Int32("ActorValue", -1);

                        effects.Add(new EnchantmentEffect
                        {
                            EffectFormId = currentEffectId,
                            Magnitude = magnitude,
                            Area = GameStatNormalizer.IsPlausibleEffectArea(area) ? area : 0,
                            Duration = GameStatNormalizer.IsPlausibleEffectDuration(duration) ? duration : 0,
                            Type = GameStatNormalizer.IsPlausibleEffectTarget(effectTargetType) ? effectTargetType : 0,
                            ActorValue = GameStatNormalizer.IsPlausibleActorValue(actorValue) ? actorValue : -1
                        });
                    }

                    break;
                }
            }
        }

        return new EnchantmentRecord
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
        };
    }

    #endregion

    #region Base Effects

    /// <summary>
    ///     Parse all Base Effect (MGEF) records.
    /// </summary>
    internal List<BaseEffectRecord> ParseBaseEffects()
    {
        var effects = ParseAccessorOnly("MGEF", 4096, ParseBaseEffectFromAccessor);

        Context.MergeRuntimeRecords(effects, 0x10, e => e.FormId,
            (reader, entry) => reader.ReadRuntimeBaseEffect(entry), "base effects");

        return effects;
    }

    private BaseEffectRecord? ParseBaseEffectFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
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
                    if (SubrecordSchemaView.TryRead("DATA", "MGEF",
                            data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian) is { } v)
                    {
                        flags = v.UInt32("Flags");
                        baseCost = v.Float("BaseCost");
                        associatedItem = v.UInt32("AssocItem");
                        magicSchool = v.Int32("MagicSchool");
                        resistValue = v.Int32("ResistanceValue");
                        archetype = v.UInt32("Archtype");
                        actorValue = v.Int32("ActorValue");
                        var light = v.UInt32("Light");
                        if (light != 0) lightFormId = light;
                        projectileSpeed = v.Float("ProjectileSpeed");
                        var shader = v.UInt32("EffectShader");
                        if (shader != 0) effectShaderFormId = shader;
                        var enchEff = v.UInt32("EnchantEffect");
                        if (enchEff != 0) enchantEffectFormId = enchEff;
                        var castSnd = v.UInt32("CastingSound");
                        if (castSnd != 0) castingSoundFormId = castSnd;
                        var boltSnd = v.UInt32("BoltSound");
                        if (boltSnd != 0) boltSoundFormId = boltSnd;
                        var hitSnd = v.UInt32("HitSound");
                        if (hitSnd != 0) hitSoundFormId = hitSnd;
                        var areaSnd = v.UInt32("AreaSound");
                        if (areaSnd != 0) areaSoundFormId = areaSnd;
                        ceEnchantFactor =
                            v.Float("ConstantEffectEnchantmentFactor");
                        ceBarterFactor = v.Float("ConstantEffectBarterFactor");
                    }

                    break;
                }
            }
        }

        return new BaseEffectRecord
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
        };
    }

    #endregion

    #region Perks

    /// <summary>
    ///     Parse all Perk records from the scan result.
    /// </summary>
    internal List<PerkRecord> ParsePerks()
    {
        var perks = ParseRecordList("PERK", 8192, ParsePerkFromAccessor,
            record => new PerkRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

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
        var conditions = new List<PerkCondition>();

        // Track current entry being built
        byte currentEntryType = 0;
        byte currentEntryRank = 0;
        byte currentEntryPriority = 0;
        uint? currentAbilityFormId = null;
        uint? currentQuestFormId = null;
        int? currentQuestStage = null;
        byte? currentEntryPoint = null;
        byte? currentFunctionType = null;
        float? currentEffectValue = null;
        uint? currentEffectFormId = null;
        string? currentEffectData = null;
        byte[]? currentRawEntryData = null;
        byte[]? currentRawFunctionData = null;
        byte? currentConditionTabCount = null;
        var currentEntryConditions = new List<PerkCondition>();
        var currentEntryActive = false;

        void FinalizeCurrentEntry()
        {
            if (!currentEntryActive)
            {
                return;
            }

            entries.Add(new PerkEntry
            {
                Type = currentEntryType,
                Rank = currentEntryRank,
                Priority = currentEntryPriority,
                AbilityFormId = currentAbilityFormId,
                QuestFormId = currentQuestFormId,
                QuestStage = currentQuestStage,
                EntryPoint = currentEntryPoint,
                FunctionType = currentFunctionType,
                EffectValue = currentEffectValue,
                EffectFormId = currentEffectFormId,
                EffectData = currentEffectData,
                RawEntryData = currentRawEntryData,
                RawFunctionData = currentRawFunctionData,
                ConditionTabCount = currentConditionTabCount,
                Conditions = currentEntryConditions
            });

            currentAbilityFormId = null;
            currentQuestFormId = null;
            currentQuestStage = null;
            currentEntryPoint = null;
            currentFunctionType = null;
            currentEffectValue = null;
            currentEffectFormId = null;
            currentEffectData = null;
            currentRawEntryData = null;
            currentRawFunctionData = null;
            currentConditionTabCount = null;
            currentEntryConditions = [];
            currentEntryActive = false;
        }

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
                case "DATA" when !currentEntryActive && sub.DataLength >= 5:
                    trait = subData[0];
                    minLevel = subData[1];
                    ranks = subData[2];
                    playable = subData[3];
                    break;
                case "DATA" when currentEntryActive:
                    currentRawEntryData = subData.ToArray();
                    ParsePerkEntryData(
                        subData,
                        record.IsBigEndian,
                        currentEntryType,
                        ref currentAbilityFormId,
                        ref currentQuestFormId,
                        ref currentQuestStage,
                        ref currentEntryPoint,
                        ref currentEffectData);
                    break;
                case "CTDA" when sub.DataLength >= 24:
                {
                    var condition = ParsePerkCondition(subData, record.IsBigEndian);
                    if (condition != null)
                    {
                        conditions.Add(condition);
                        if (currentEntryActive)
                        {
                            currentEntryConditions.Add(condition);
                        }
                    }

                    break;
                }
                case "PRKE" when sub.DataLength >= 3:
                    FinalizeCurrentEntry();
                    // Start new perk entry
                    currentEntryType = subData[0];
                    currentEntryRank = subData[1];
                    currentEntryPriority = subData[2];
                    currentEntryActive = true;
                    break;
                case "PRKC" when currentEntryActive && sub.DataLength >= 1:
                    currentConditionTabCount = subData[0];
                    break;
                case "EPFT" when sub.DataLength >= 1:
                    currentFunctionType = subData[0];
                    break;
                case "EPFD" when currentEntryActive:
                    currentRawFunctionData = subData.ToArray();
                    ParsePerkEntryFunctionData(
                        subData,
                        record.IsBigEndian,
                        ref currentEffectValue,
                        ref currentEffectFormId,
                        ref currentEffectData);
                    break;
                case "PRKF":
                    FinalizeCurrentEntry();
                    break;
            }
        }

        FinalizeCurrentEntry();

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
            Conditions = conditions,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static void ParsePerkEntryData(
        ReadOnlySpan<byte> subData,
        bool isBigEndian,
        byte entryType,
        ref uint? abilityFormId,
        ref uint? questFormId,
        ref int? questStage,
        ref byte? entryPoint,
        ref string? effectData)
    {
        effectData = FormatRawBytes(subData);
        switch (entryType)
        {
            case 0 when subData.Length >= 8:
                questFormId = RecordParserContext.ReadFormId(subData, isBigEndian);
                var rawStage = BinaryUtils.ReadUInt32(subData, 4, isBigEndian);
                if ((rawStage & 0x00FFFFFF) == 0x00CDCDCD)
                {
                    effectData = $"Quest 0x{questFormId.Value:X8}, payload 0x{rawStage:X8}";
                }
                else
                {
                    questStage = (int)rawStage;
                    effectData = $"Quest 0x{questFormId.Value:X8}, stage {questStage.Value}";
                }

                break;

            case 1 when subData.Length >= 4:
                abilityFormId = RecordParserContext.ReadFormId(subData, isBigEndian);
                effectData = $"Ability 0x{abilityFormId.Value:X8}";
                break;

            case 2 when subData.Length >= 1:
                entryPoint = subData[0];
                effectData = subData.Length >= 2
                    ? $"Entry Point #{subData[0]}, payload {FormatRawBytes(subData[1..])}"
                    : $"Entry Point #{subData[0]}";
                break;
        }
    }

    private static void ParsePerkEntryFunctionData(
        ReadOnlySpan<byte> subData,
        bool isBigEndian,
        ref float? effectValue,
        ref uint? effectFormId,
        ref string? effectData)
    {
        if (subData.Length == 4)
        {
            var raw = RecordParserContext.ReadFormId(subData, isBigEndian);
            var floatValue = BinaryUtils.ReadFloat(subData, 0, isBigEndian);
            if (float.IsFinite(floatValue) && MathF.Abs(floatValue) < 100000f)
            {
                effectValue = floatValue;
                effectData = floatValue.ToString("G");
                return;
            }

            if (LooksLikeFormId(raw))
            {
                effectFormId = raw;
                effectData = $"0x{raw:X8}";
                return;
            }
        }

        effectData = FormatRawBytes(subData);
    }

    private static PerkCondition? ParsePerkCondition(ReadOnlySpan<byte> subData, bool isBigEndian)
    {
        var type = subData[0];
        var comparisonOperator = (byte)((type >> 5) & 0x7);
        if (comparisonOperator > 5)
        {
            return null;
        }

        var comparisonValue = BinaryUtils.ReadFloat(subData, 4, isBigEndian);
        if (!float.IsFinite(comparisonValue))
        {
            comparisonValue = 0;
        }

        var functionIndex = isBigEndian
            ? BinaryUtils.ReadUInt16BE(subData, 8)
            : BinaryUtils.ReadUInt16LE(subData, 8);
        var parameter1 = RecordParserContext.ReadFormId(subData[12..], isBigEndian);
        var parameter2 = subData.Length >= 20
            ? RecordParserContext.ReadFormId(subData[16..], isBigEndian)
            : 0;

        if (type == 0 && functionIndex == 0 && parameter1 == 0 && parameter2 == 0 &&
            MathF.Abs(comparisonValue) < 0.0001f)
        {
            return null;
        }

        var param1 = PerkConditionParameterResolver.ResolveParameter(functionIndex, 0, parameter1);
        var param2 = PerkConditionParameterResolver.ResolveParameter(functionIndex, 1, parameter2);

        return new PerkCondition
        {
            FunctionIndex = functionIndex,
            FunctionName = PerkConditionParameterResolver.ResolveScriptFunctionName(functionIndex),
            Parameter1 = parameter1,
            Parameter1Display = param1.Display,
            Parameter1FormId = param1.FormId,
            Parameter2 = parameter2,
            Parameter2Display = param2.Display,
            Parameter2FormId = param2.FormId,
            ComparisonOperator = comparisonOperator,
            ComparisonValue = comparisonValue
        };
    }

    private static bool LooksLikeFormId(uint raw)
    {
        return raw is > 0 and < 0xFF000000;
    }

    private static string FormatRawBytes(ReadOnlySpan<byte> data)
    {
        return data.Length == 0 ? "" : Convert.ToHexString(data).ToLowerInvariant();
    }

    #endregion

    #region Spells

    /// <summary>
    ///     Parse all Spell records from the scan result.
    /// </summary>
    internal List<SpellRecord> ParseSpells()
    {
        var spells = ParseRecordList("SPEL", 4096, ParseSpellFromAccessor,
            record => new SpellRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

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
        var effects = new List<EnchantmentEffect>();
        uint currentEffectId = 0;

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
                    if (SubrecordSchemaView.TryRead("SPIT", "SPEL", subData, record.IsBigEndian) is { } v)
                    {
                        type = (SpellType)v.UInt32("Type");
                        cost = v.UInt32("Cost");
                        level = v.UInt32("Level");
                        flags = v.Byte("Flags");
                    }

                    break;
                }
                case "EFID" when sub.DataLength >= 4:
                    currentEffectId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "EFIT" when sub.DataLength >= 12:
                {
                    if (SubrecordSchemaView.TryRead("EFIT", null, subData, record.IsBigEndian) is { } v)
                    {
                        var magnitude = GameStatNormalizer.EffectMagnitude(subData, record.IsBigEndian);
                        var area = v.UInt32("Area");
                        var duration = v.UInt32("Duration");
                        var effectTargetType = v.UInt32("Type");
                        var actorValue = v.Int32("ActorValue", -1);

                        effects.Add(new EnchantmentEffect
                        {
                            EffectFormId = currentEffectId,
                            Magnitude = magnitude,
                            Area = GameStatNormalizer.IsPlausibleEffectArea(area) ? area : 0,
                            Duration = GameStatNormalizer.IsPlausibleEffectDuration(duration) ? duration : 0,
                            Type = GameStatNormalizer.IsPlausibleEffectTarget(effectTargetType) ? effectTargetType : 0,
                            ActorValue = GameStatNormalizer.IsPlausibleActorValue(actorValue) ? actorValue : -1
                        });
                    }

                    break;
                }
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
            Effects = effects,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
