using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Enums;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord;

public sealed partial class SemanticReconstructor
{
    #region Globals

    /// <summary>
    ///     Reconstruct all Global Variable (GLOB) records.
    /// </summary>
    public List<ReconstructedGlobal> ReconstructGlobals()
    {
        var globals = new List<ReconstructedGlobal>();

        if (_accessor == null)
        {
            return globals;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in GetRecordsByType("GLOB"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null;
                var valueType = 'f';
                float value = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FNAM" when sub.DataLength >= 1:
                            valueType = (char)buffer[sub.DataOffset];
                            break;
                        case "FLTV" when sub.DataLength >= 4:
                            value = record.IsBigEndian
                                ? BinaryPrimitives.ReadSingleBigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadSingleLittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                    }
                }

                globals.Add(new ReconstructedGlobal
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    ValueType = valueType,
                    Value = value,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return globals;
    }

    #endregion

    #region Classes

    /// <summary>
    ///     Reconstruct all Class (CLAS) records.
    /// </summary>
    public List<ReconstructedClass> ReconstructClasses()
    {
        var classes = new List<ReconstructedClass>();

        if (_accessor == null)
        {
            return classes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in GetRecordsByType("CLAS"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null, icon = null;
                var tagSkills = new List<int>();
                uint classFlags = 0, barterFlags = 0;
                byte trainingSkill = 0, trainingLevel = 0;
                var attributeWeights = Array.Empty<byte>();

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description =
                                EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 20:
                            {
                                var span = buffer.AsSpan(sub.DataOffset);
                                // DATA: 4 tag skill indices (int32 each) + flags (uint32) + barter flags (uint32)
                                for (var i = 0; i < 4 && i * 4 + 4 <= sub.DataLength - 8; i++)
                                {
                                    var skill = record.IsBigEndian
                                        ? BinaryPrimitives.ReadInt32BigEndian(span[(i * 4)..])
                                        : BinaryPrimitives.ReadInt32LittleEndian(span[(i * 4)..]);
                                    if (skill >= 0)
                                    {
                                        tagSkills.Add(skill);
                                    }
                                }

                                var flagsOffset = sub.DataLength - 8;
                                if (record.IsBigEndian)
                                {
                                    classFlags = BinaryPrimitives.ReadUInt32BigEndian(span[flagsOffset..]);
                                    barterFlags = BinaryPrimitives.ReadUInt32BigEndian(span[(flagsOffset + 4)..]);
                                }
                                else
                                {
                                    classFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[flagsOffset..]);
                                    barterFlags = BinaryPrimitives.ReadUInt32LittleEndian(span[(flagsOffset + 4)..]);
                                }

                                break;
                            }
                        case "ATTR" when sub.DataLength >= 2:
                            {
                                trainingSkill = buffer[sub.DataOffset];
                                trainingLevel = buffer[sub.DataOffset + 1];
                                if (sub.DataLength >= 9)
                                {
                                    attributeWeights = new byte[7];
                                    Array.Copy(buffer, sub.DataOffset + 2, attributeWeights, 0, 7);
                                }

                                break;
                            }
                    }
                }

                classes.Add(new ReconstructedClass
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    TagSkills = tagSkills.ToArray(),
                    Flags = classFlags,
                    BarterFlags = barterFlags,
                    TrainingSkill = trainingSkill,
                    TrainingLevel = trainingLevel,
                    AttributeWeights = attributeWeights,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return classes;
    }

    #endregion

    #region Challenges

    /// <summary>
    ///     Reconstruct all Challenge (CHAL) records.
    /// </summary>
    public List<ReconstructedChallenge> ReconstructChallenges()
    {
        var challenges = new List<ReconstructedChallenge>();

        if (_accessor == null)
        {
            return challenges;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("CHAL"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null, icon = null;
                uint challengeType = 0, threshold = 0, flags = 0, interval = 0;
                uint value1 = 0, value2 = 0, value3 = 0, script = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description =
                                EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "ICON":
                            icon = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "SCRI" when sub.DataLength >= 4:
                            script = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(sub.DataOffset));
                            break;
                        case "DATA" when sub.DataLength >= 20:
                            {
                                var span = buffer.AsSpan(sub.DataOffset);
                                if (record.IsBigEndian)
                                {
                                    challengeType = BinaryPrimitives.ReadUInt32BigEndian(span);
                                    threshold = BinaryPrimitives.ReadUInt32BigEndian(span[4..]);
                                    flags = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                    interval = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                                    value1 = sub.DataLength >= 24 ? BinaryPrimitives.ReadUInt32BigEndian(span[16..]) : 0;
                                    value2 = sub.DataLength >= 28 ? BinaryPrimitives.ReadUInt32BigEndian(span[20..]) : 0;
                                    value3 = sub.DataLength >= 32 ? BinaryPrimitives.ReadUInt32BigEndian(span[24..]) : 0;
                                }
                                else
                                {
                                    challengeType = BinaryPrimitives.ReadUInt32LittleEndian(span);
                                    threshold = BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                    flags = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                    interval = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                                    value1 = sub.DataLength >= 24 ? BinaryPrimitives.ReadUInt32LittleEndian(span[16..]) : 0;
                                    value2 = sub.DataLength >= 28 ? BinaryPrimitives.ReadUInt32LittleEndian(span[20..]) : 0;
                                    value3 = sub.DataLength >= 32 ? BinaryPrimitives.ReadUInt32LittleEndian(span[24..]) : 0;
                                }

                                break;
                            }
                    }
                }

                challenges.Add(new ReconstructedChallenge
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    ChallengeType = challengeType,
                    Threshold = threshold,
                    Flags = flags,
                    Interval = interval,
                    Value1 = value1,
                    Value2 = value2,
                    Value3 = value3,
                    Script = script,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return challenges;
    }

    #endregion

    #region Reputations

    /// <summary>
    ///     Reconstruct all Reputation (REPU) records.
    /// </summary>
    public List<ReconstructedReputation> ReconstructReputations()
    {
        var reputations = new List<ReconstructedReputation>();

        if (_accessor == null)
        {
            return reputations;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in GetRecordsByType("REPU"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null;
                float positiveValue = 0, negativeValue = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 8:
                            {
                                var span = buffer.AsSpan(sub.DataOffset);
                                if (record.IsBigEndian)
                                {
                                    positiveValue = BinaryPrimitives.ReadSingleBigEndian(span);
                                    negativeValue = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                }
                                else
                                {
                                    positiveValue = BinaryPrimitives.ReadSingleLittleEndian(span);
                                    negativeValue = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                }

                                break;
                            }
                    }
                }

                reputations.Add(new ReconstructedReputation
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    PositiveValue = positiveValue,
                    NegativeValue = negativeValue,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return reputations;
    }

    #endregion

    #region Weapon Mods

    /// <summary>
    ///     Reconstruct all Weapon Mod (IMOD) records.
    /// </summary>
    public List<ReconstructedWeaponMod> ReconstructWeaponMods()
    {
        var mods = new List<ReconstructedWeaponMod>();

        if (_accessor == null)
        {
            return mods;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in GetRecordsByType("IMOD"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null, description = null;
                string? modelPath = null, icon = null;
                var value = 0;
                float weight = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DESC":
                            description =
                                EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset,
                                sub.DataLength));
                            break;
                        case "ICON":
                            icon = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 8:
                            {
                                var span = buffer.AsSpan(sub.DataOffset);
                                if (record.IsBigEndian)
                                {
                                    value = BinaryPrimitives.ReadInt32BigEndian(span);
                                    weight = BinaryPrimitives.ReadSingleBigEndian(span[4..]);
                                }
                                else
                                {
                                    value = BinaryPrimitives.ReadInt32LittleEndian(span);
                                    weight = BinaryPrimitives.ReadSingleLittleEndian(span[4..]);
                                }

                                break;
                            }
                    }
                }

                mods.Add(new ReconstructedWeaponMod
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    Description = description,
                    ModelPath = modelPath,
                    Icon = icon,
                    Value = value,
                    Weight = weight,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return mods;
    }

    #endregion

    #region Recipes

    /// <summary>
    ///     Reconstruct all Recipe (RCPE) records.
    /// </summary>
    public List<ReconstructedRecipe> ReconstructRecipes()
    {
        var recipes = new List<ReconstructedRecipe>();

        if (_accessor == null)
        {
            return recipes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("RCPE"))
            {
                var dataStart = record.Offset + 24;
                var dataSize = (int)Math.Min(record.DataSize, buffer.Length);
                if (dataStart + dataSize > _fileSize)
                {
                    continue;
                }

                Array.Clear(buffer, 0, dataSize);
                _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

                string? editorId = null, fullName = null;
                int requiredSkill = -1, requiredSkillLevel = 0;
                uint categoryFormId = 0, subcategoryFormId = 0;
                var ingredients = new List<RecipeIngredient>();
                var outputs = new List<RecipeOutput>();

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
                {
                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 16:
                            {
                                var span = buffer.AsSpan(sub.DataOffset);
                                if (record.IsBigEndian)
                                {
                                    requiredSkill = BinaryPrimitives.ReadInt32BigEndian(span);
                                    requiredSkillLevel = BinaryPrimitives.ReadInt32BigEndian(span[4..]);
                                    categoryFormId = BinaryPrimitives.ReadUInt32BigEndian(span[8..]);
                                    subcategoryFormId = BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                                }
                                else
                                {
                                    requiredSkill = BinaryPrimitives.ReadInt32LittleEndian(span);
                                    requiredSkillLevel = BinaryPrimitives.ReadInt32LittleEndian(span[4..]);
                                    categoryFormId = BinaryPrimitives.ReadUInt32LittleEndian(span[8..]);
                                    subcategoryFormId = BinaryPrimitives.ReadUInt32LittleEndian(span[12..]);
                                }

                                break;
                            }
                        case "RCIL" when sub.DataLength >= 8:
                            {
                                var span = buffer.AsSpan(sub.DataOffset);
                                var itemId = record.IsBigEndian
                                    ? BinaryPrimitives.ReadUInt32BigEndian(span)
                                    : BinaryPrimitives.ReadUInt32LittleEndian(span);
                                var count = record.IsBigEndian
                                    ? BinaryPrimitives.ReadUInt32BigEndian(span[4..])
                                    : BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                ingredients.Add(new RecipeIngredient { ItemFormId = itemId, Count = count });
                                break;
                            }
                        case "RCOD" when sub.DataLength >= 8:
                            {
                                var span = buffer.AsSpan(sub.DataOffset);
                                var itemId = record.IsBigEndian
                                    ? BinaryPrimitives.ReadUInt32BigEndian(span)
                                    : BinaryPrimitives.ReadUInt32LittleEndian(span);
                                var count = record.IsBigEndian
                                    ? BinaryPrimitives.ReadUInt32BigEndian(span[4..])
                                    : BinaryPrimitives.ReadUInt32LittleEndian(span[4..]);
                                outputs.Add(new RecipeOutput { ItemFormId = itemId, Count = count });
                                break;
                            }
                    }
                }

                recipes.Add(new ReconstructedRecipe
                {
                    FormId = record.FormId,
                    EditorId = editorId,
                    FullName = fullName,
                    RequiredSkill = requiredSkill,
                    RequiredSkillLevel = requiredSkillLevel,
                    CategoryFormId = categoryFormId,
                    SubcategoryFormId = subcategoryFormId,
                    Ingredients = ingredients,
                    Outputs = outputs,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return recipes;
    }

    #endregion

    #region Game Settings

    /// <summary>
    ///     Reconstruct all Game Setting (GMST) records from the scan result.
    /// </summary>
    public List<ReconstructedGameSetting> ReconstructGameSettings()
    {
        var settings = new List<ReconstructedGameSetting>();
        var gmstRecords = GetRecordsByType("GMST").ToList();

        if (_accessor == null)
        {
            // Without accessor, just return basic info
            foreach (var record in gmstRecords)
            {
                settings.Add(new ReconstructedGameSetting
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return settings;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(512);
        try
        {
            foreach (var record in gmstRecords)
            {
                var setting = ReconstructGameSettingFromAccessor(record, buffer);
                if (setting != null)
                {
                    settings.Add(setting);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return settings;
    }

    private ReconstructedGameSetting? ReconstructGameSettingFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return new ReconstructedGameSetting
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        Array.Clear(buffer, 0, dataSize);
        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        string? editorId = null;
        byte[]? dataValue = null;

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(buffer.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        _formIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "DATA":
                    dataValue = new byte[sub.DataLength];
                    Array.Copy(buffer, sub.DataOffset, dataValue, 0, sub.DataLength);
                    break;
            }
        }

        // Determine type from first letter of EditorId
        var valueType = GameSettingType.Integer;
        float? floatValue = null;
        int? intValue = null;
        string? stringValue = null;

        if (!string.IsNullOrEmpty(editorId) && dataValue != null)
        {
            var typeChar = char.ToLowerInvariant(editorId[0]);
            switch (typeChar)
            {
                case 'f' when dataValue.Length >= 4:
                    valueType = GameSettingType.Float;
                    floatValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(dataValue)
                        : BinaryPrimitives.ReadSingleLittleEndian(dataValue);
                    break;
                case 'i' when dataValue.Length >= 4:
                    valueType = GameSettingType.Integer;
                    intValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(dataValue)
                        : BinaryPrimitives.ReadInt32LittleEndian(dataValue);
                    break;
                case 'b' when dataValue.Length >= 4:
                    valueType = GameSettingType.Boolean;
                    intValue = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(dataValue)
                        : BinaryPrimitives.ReadInt32LittleEndian(dataValue);
                    break;
                case 's':
                    valueType = GameSettingType.String;
                    stringValue = EsmStringUtils.ReadNullTermString(dataValue);
                    break;
            }
        }

        return new ReconstructedGameSetting
        {
            FormId = record.FormId,
            EditorId = editorId,
            ValueType = valueType,
            FloatValue = floatValue,
            IntValue = intValue,
            StringValue = stringValue,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Leveled Lists

    /// <summary>
    ///     Reconstruct leveled list records (LVLI/LVLN/LVLC).
    /// </summary>
    public List<ReconstructedLeveledList> ReconstructLeveledLists()
    {
        var lists = new List<ReconstructedLeveledList>();
        var lvliRecords = GetRecordsByType("LVLI").ToList();
        var lvlnRecords = GetRecordsByType("LVLN").ToList();
        var lvlcRecords = GetRecordsByType("LVLC").ToList();

        // Combine all leveled list records
        var allRecords = lvliRecords
            .Concat(lvlnRecords)
            .Concat(lvlcRecords)
            .ToList();

        if (_accessor == null)
        {
            foreach (var record in allRecords)
            {
                var list = ReconstructLeveledListFromScanResult(record);
                if (list != null)
                {
                    lists.Add(list);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in allRecords)
                {
                    var list = ReconstructLeveledListFromAccessor(record, buffer);
                    if (list != null)
                    {
                        lists.Add(list);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return lists;
    }

    private ReconstructedLeveledList? ReconstructLeveledListFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var dataStart = record.Offset + 24;
        var dataSize = (int)Math.Min(record.DataSize, buffer.Length);

        if (dataStart + dataSize > _fileSize)
        {
            return ReconstructLeveledListFromScanResult(record);
        }

        _accessor!.ReadArray(dataStart, buffer, 0, dataSize);

        byte chanceNone = 0;
        byte flags = 0;
        uint? globalFormId = null;
        var entries = new List<LeveledEntry>();

        // Parse subrecords
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(buffer, dataSize, record.IsBigEndian))
        {
            var subData = buffer.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "LVLD" when sub.DataLength == 1:
                    chanceNone = subData[0];
                    break;

                case "LVLF" when sub.DataLength == 1:
                    flags = subData[0];
                    break;

                case "LVLG" when sub.DataLength == 4:
                    globalFormId = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                    break;

                case "LVLO" when sub.DataLength == 12:
                    // LVLO: level (u16) + pad (u16) + FormID (u32) + count (u16) + pad (u16)
                    var level = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                    var formId = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    var count = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt16BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt16LittleEndian(subData[8..]);

                    entries.Add(new LeveledEntry(level, formId, count));
                    break;
            }
        }

        return new ReconstructedLeveledList
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            ListType = record.RecordType,
            ChanceNone = chanceNone,
            Flags = flags,
            GlobalFormId = globalFormId,
            Entries = entries,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private ReconstructedLeveledList? ReconstructLeveledListFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedLeveledList
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            ListType = record.RecordType,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
