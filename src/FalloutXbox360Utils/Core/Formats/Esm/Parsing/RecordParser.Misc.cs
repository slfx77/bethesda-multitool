using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class RecordParser
{
    #region Globals

    /// <summary>
    ///     Reconstruct all Global Variable (GLOB) records.
    /// </summary>
    public List<GlobalRecord> ReconstructGlobals()
    {
        var globals = new List<GlobalRecord>();

        if (_accessor == null)
        {
            return globals;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in GetRecordsByType("GLOB"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                var valueType = 'f';
                float value = 0;

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
                        case "FNAM" when sub.DataLength >= 1:
                            valueType = (char)data[sub.DataOffset];
                            break;
                        case "FLTV" when sub.DataLength >= 4:
                            value = record.IsBigEndian
                                ? BinaryPrimitives.ReadSingleBigEndian(data.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadSingleLittleEndian(data.AsSpan(sub.DataOffset));
                            break;
                    }
                }

                globals.Add(new GlobalRecord
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
    public List<ClassRecord> ReconstructClasses()
    {
        var classes = new List<ClassRecord>();

        if (_accessor == null)
        {
            return classes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in GetRecordsByType("CLAS"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, description = null, icon = null;
                var tagSkills = new List<int>();
                uint classFlags = 0, barterFlags = 0;
                byte trainingSkill = 0, trainingLevel = 0;
                var attributeWeights = Array.Empty<byte>();

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
                        case "DATA" when sub.DataLength >= 28:
                            {
                                var fields = SubrecordDataReader.ReadFields("DATA", "CLAS",
                                    data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                                if (fields.Count > 0)
                                {
                                    var skill1 = SubrecordDataReader.GetInt32(fields, "TagSkill1");
                                    var skill2 = SubrecordDataReader.GetInt32(fields, "TagSkill2");
                                    var skill3 = SubrecordDataReader.GetInt32(fields, "TagSkill3");
                                    var skill4 = SubrecordDataReader.GetInt32(fields, "TagSkill4");
                                    if (skill1 >= 0) tagSkills.Add(skill1);
                                    if (skill2 >= 0) tagSkills.Add(skill2);
                                    if (skill3 >= 0) tagSkills.Add(skill3);
                                    if (skill4 >= 0) tagSkills.Add(skill4);

                                    classFlags = SubrecordDataReader.GetUInt32(fields, "Flags");
                                    barterFlags = SubrecordDataReader.GetUInt32(fields, "BuysServices");
                                    trainingSkill = (byte)SubrecordDataReader.GetSByte(fields, "Teaches");
                                    trainingLevel = SubrecordDataReader.GetByte(fields, "MaxTrainingLevel");
                                }

                                break;
                            }
                        case "ATTR" when sub.DataLength >= 7:
                            {
                                attributeWeights = new byte[7];
                                Array.Copy(data, sub.DataOffset, attributeWeights, 0, 7);
                                break;
                            }
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _formIdToFullName.TryAdd(record.FormId, fullName);
                }

                classes.Add(new ClassRecord
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
    public List<ChallengeRecord> ReconstructChallenges()
    {
        var challenges = new List<ChallengeRecord>();

        if (_accessor == null)
        {
            return challenges;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("CHAL"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, description = null, icon = null;
                uint challengeType = 0, threshold = 0, flags = 0, interval = 0;
                uint value1 = 0, value2 = 0, value3 = 0, script = 0;

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
                        case "SCRI" when sub.DataLength >= 4:
                            script = ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            break;
                        case "DATA" when sub.DataLength >= 24:
                            {
                                var fields = SubrecordDataReader.ReadFields("DATA", "CHAL",
                                    data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                                if (fields.Count > 0)
                                {
                                    challengeType = SubrecordDataReader.GetUInt32(fields, "Type");
                                    threshold = SubrecordDataReader.GetUInt32(fields, "Threshold");
                                    flags = SubrecordDataReader.GetUInt16(fields, "Flags");
                                    interval = SubrecordDataReader.GetUInt16(fields, "Interval");
                                    value1 = SubrecordDataReader.GetUInt32(fields, "Value1");
                                    value2 = SubrecordDataReader.GetUInt16(fields, "Value2");
                                    value3 = SubrecordDataReader.GetUInt16(fields, "Value3");
                                }

                                break;
                            }
                    }
                }

                challenges.Add(new ChallengeRecord
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
    public List<ReputationRecord> ReconstructReputations()
    {
        var reputations = new List<ReputationRecord>();

        if (_accessor == null)
        {
            return reputations;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in GetRecordsByType("REPU"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null;
                float positiveValue = 0, negativeValue = 0;

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
                        case "DATA" when sub.DataLength >= 8:
                            {
                                var fields = SubrecordDataReader.ReadFields("DATA", "REPU",
                                    data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                                if (fields.Count > 0)
                                {
                                    positiveValue = SubrecordDataReader.GetFloat(fields, "PositiveValue");
                                    negativeValue = SubrecordDataReader.GetFloat(fields, "NegativeValue");
                                }

                                break;
                            }
                    }
                }

                reputations.Add(new ReputationRecord
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
    public List<WeaponModRecord> ReconstructWeaponMods()
    {
        var mods = new List<WeaponModRecord>();

        if (_accessor == null)
        {
            return mods;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in GetRecordsByType("IMOD"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, description = null;
                string? modelPath = null, icon = null;
                var value = 0;
                float weight = 0;

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
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset,
                                sub.DataLength));
                            break;
                        case "ICON":
                            icon = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                            break;
                        case "DATA" when sub.DataLength >= 8:
                            {
                                var fields = SubrecordDataReader.ReadFields("DATA", "IMOD",
                                    data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                                if (fields.Count > 0)
                                {
                                    value = (int)SubrecordDataReader.GetUInt32(fields, "Value");
                                    weight = SubrecordDataReader.GetFloat(fields, "Weight");
                                }

                                break;
                            }
                    }
                }

                mods.Add(new WeaponModRecord
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
    public List<RecipeRecord> ReconstructRecipes()
    {
        var recipes = new List<RecipeRecord>();

        if (_accessor == null)
        {
            return recipes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("RCPE"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null;
                int requiredSkill = -1, requiredSkillLevel = 0;
                uint categoryFormId = 0, subcategoryFormId = 0;
                var ingredients = new List<RecipeIngredient>();
                var outputs = new List<RecipeOutput>();

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
                        case "DATA" when sub.DataLength >= 16:
                            {
                                var fields = SubrecordDataReader.ReadFields("DATA", "RCPE",
                                    data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                                if (fields.Count > 0)
                                {
                                    requiredSkill = SubrecordDataReader.GetInt32(fields, "Skill");
                                    requiredSkillLevel = (int)SubrecordDataReader.GetUInt32(fields, "Level");
                                    categoryFormId = SubrecordDataReader.GetUInt32(fields, "Category");
                                    subcategoryFormId = SubrecordDataReader.GetUInt32(fields, "SubCategory");
                                }

                                break;
                            }
                        case "RCIL" when sub.DataLength >= 8:
                            {
                                var fields = SubrecordDataReader.ReadFields("RCIL", null,
                                    data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                                if (fields.Count > 0)
                                {
                                    var itemId = SubrecordDataReader.GetUInt32(fields, "Item");
                                    var count = SubrecordDataReader.GetUInt32(fields, "Count");
                                    ingredients.Add(new RecipeIngredient { ItemFormId = itemId, Count = count });
                                }

                                break;
                            }
                        case "RCOD" when sub.DataLength >= 8:
                            {
                                var fields = SubrecordDataReader.ReadFields("RCOD", null,
                                    data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                                if (fields.Count > 0)
                                {
                                    var itemId = SubrecordDataReader.GetUInt32(fields, "Item");
                                    var count = SubrecordDataReader.GetUInt32(fields, "Count");
                                    outputs.Add(new RecipeOutput { ItemFormId = itemId, Count = count });
                                }

                                break;
                            }
                    }
                }

                recipes.Add(new RecipeRecord
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
    public List<GameSettingRecord> ReconstructGameSettings()
    {
        var settings = new List<GameSettingRecord>();
        var gmstRecords = GetRecordsByType("GMST").ToList();

        if (_accessor == null)
        {
            // Without accessor, just return basic info
            foreach (var record in gmstRecords)
            {
                settings.Add(new GameSettingRecord
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

    private GameSettingRecord? ReconstructGameSettingFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new GameSettingRecord
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        byte[]? dataValue = null;

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
                case "DATA":
                    dataValue = new byte[sub.DataLength];
                    Array.Copy(data, sub.DataOffset, dataValue, 0, sub.DataLength);
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

        return new GameSettingRecord
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

    #region Form Lists

    /// <summary>
    ///     Reconstruct all Form ID List (FLST) records.
    /// </summary>
    public List<FormListRecord> ReconstructFormLists()
    {
        var formLists = new List<FormListRecord>();

        if (_accessor == null)
        {
            foreach (var record in GetRecordsByType("FLST"))
            {
                formLists.Add(new FormListRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return formLists;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            foreach (var record in GetRecordsByType("FLST"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    formLists.Add(new FormListRecord
                    {
                        FormId = record.FormId,
                        EditorId = GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                var formIds = new List<uint>();

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
                        case "LNAM" when sub.DataLength == 4:
                            formIds.Add(ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian));
                            break;
                    }
                }

                formLists.Add(new FormListRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? GetEditorId(record.FormId),
                    FormIds = formIds,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return formLists;
    }

    #endregion

    #region Activators

    /// <summary>
    ///     Reconstruct all Activator (ACTI) records.
    /// </summary>
    public List<ActivatorRecord> ReconstructActivators()
    {
        var activators = new List<ActivatorRecord>();

        if (_accessor == null)
        {
            foreach (var record in GetRecordsByType("ACTI"))
            {
                activators.Add(new ActivatorRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return activators;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in GetRecordsByType("ACTI"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    activators.Add(new ActivatorRecord
                    {
                        FormId = record.FormId,
                        EditorId = GetEditorId(record.FormId),
                        FullName = FindFullNameNear(record.Offset),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? fullName = null;
                string? modelPath = null;
                ObjectBounds? bounds = null;
                uint? script = null;
                uint? activationSound = null;
                uint? radioStation = null;
                uint? waterType = null;

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
                            bounds = ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "SCRI" when sub.DataLength == 4:
                            script = ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "SNAM" when sub.DataLength == 4:
                            activationSound = ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "RNAM" when sub.DataLength == 4:
                            radioStation = ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "WNAM" when sub.DataLength == 4:
                            waterType = ReadFormId(subData, record.IsBigEndian);
                            break;
                    }
                }

                activators.Add(new ActivatorRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? GetEditorId(record.FormId),
                    FullName = fullName,
                    ModelPath = modelPath,
                    Bounds = bounds,
                    Script = script != 0 ? script : null,
                    ActivationSoundFormId = activationSound != 0 ? activationSound : null,
                    RadioStationFormId = radioStation != 0 ? radioStation : null,
                    WaterTypeFormId = waterType != 0 ? waterType : null,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return activators;
    }

    #endregion

    #region Leveled Lists

    /// <summary>
    ///     Reconstruct leveled list records (LVLI/LVLN/LVLC).
    /// </summary>
    public List<LeveledListRecord> ReconstructLeveledLists()
    {
        var lists = new List<LeveledListRecord>();
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

    private LeveledListRecord? ReconstructLeveledListFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructLeveledListFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        byte chanceNone = 0;
        byte flags = 0;
        uint? globalFormId = null;
        var entries = new List<LeveledEntry>();

        // Parse subrecords
        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

            switch (sub.Signature)
            {
                case "LVLD" when sub.DataLength == 1:
                    chanceNone = subData[0];
                    break;

                case "LVLF" when sub.DataLength == 1:
                    flags = subData[0];
                    break;

                case "LVLG" when sub.DataLength == 4:
                    globalFormId = ReadFormId(subData, record.IsBigEndian);
                    break;

                case "LVLO" when sub.DataLength == 12:
                    {
                        var fields = SubrecordDataReader.ReadFields("LVLO", null, subData, record.IsBigEndian);
                        if (fields.Count > 0)
                        {
                            var level = SubrecordDataReader.GetUInt16(fields, "Level");
                            var formId = SubrecordDataReader.GetUInt32(fields, "Entry");
                            var count = SubrecordDataReader.GetUInt16(fields, "Count");
                            entries.Add(new LeveledEntry(level, formId, count));
                        }
                    }

                    break;
            }
        }

        return new LeveledListRecord
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

    private LeveledListRecord? ReconstructLeveledListFromScanResult(DetectedMainRecord record)
    {
        return new LeveledListRecord
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            ListType = record.RecordType,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Lights

    /// <summary>
    ///     Reconstruct all Light (LIGH) records.
    /// </summary>
    public List<LightRecord> ReconstructLights()
    {
        var lights = new List<LightRecord>();

        if (_accessor == null)
        {
            foreach (var record in GetRecordsByType("LIGH"))
            {
                lights.Add(new LightRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return lights;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("LIGH"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    lights.Add(new LightRecord
                    {
                        FormId = record.FormId,
                        EditorId = GetEditorId(record.FormId),
                        FullName = FindFullNameNear(record.Offset),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? fullName = null;
                string? modelPath = null;
                ObjectBounds? bounds = null;
                int duration = 0;
                uint radius = 0, color = 0, flags = 0;
                float falloffExponent = 0, fov = 0, weight = 0;
                var value = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "DATA" when sub.DataLength >= 32:
                        {
                            // LIGH DATA: Duration(int32) + Radius(uint32) + Color(RGBA uint32) +
                            // Flags(uint32) + FalloffExponent(float) + FOV(float) + Value(int32) + Weight(float)
                            if (record.IsBigEndian)
                            {
                                duration = BinaryPrimitives.ReadInt32BigEndian(subData);
                                radius = BinaryPrimitives.ReadUInt32BigEndian(subData[4..]);
                                color = BinaryPrimitives.ReadUInt32BigEndian(subData[8..]);
                                flags = BinaryPrimitives.ReadUInt32BigEndian(subData[12..]);
                                falloffExponent = BinaryPrimitives.ReadSingleBigEndian(subData[16..]);
                                fov = BinaryPrimitives.ReadSingleBigEndian(subData[20..]);
                                value = BinaryPrimitives.ReadInt32BigEndian(subData[24..]);
                                weight = BinaryPrimitives.ReadSingleBigEndian(subData[28..]);
                            }
                            else
                            {
                                duration = BinaryPrimitives.ReadInt32LittleEndian(subData);
                                radius = BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                                color = BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                                flags = BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                                falloffExponent = BinaryPrimitives.ReadSingleLittleEndian(subData[16..]);
                                fov = BinaryPrimitives.ReadSingleLittleEndian(subData[20..]);
                                value = BinaryPrimitives.ReadInt32LittleEndian(subData[24..]);
                                weight = BinaryPrimitives.ReadSingleLittleEndian(subData[28..]);
                            }

                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _formIdToFullName.TryAdd(record.FormId, fullName);
                }

                lights.Add(new LightRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? GetEditorId(record.FormId),
                    FullName = fullName,
                    ModelPath = modelPath,
                    Bounds = bounds,
                    Duration = duration,
                    Radius = radius,
                    Color = color,
                    Flags = flags,
                    FalloffExponent = falloffExponent,
                    FOV = fov,
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

        return lights;
    }

    #endregion

    #region Doors

    /// <summary>
    ///     Reconstruct all Door (DOOR) records.
    /// </summary>
    public List<DoorRecord> ReconstructDoors()
    {
        var doors = new List<DoorRecord>();

        if (_accessor == null)
        {
            foreach (var record in GetRecordsByType("DOOR"))
            {
                doors.Add(new DoorRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return doors;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("DOOR"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    doors.Add(new DoorRecord
                    {
                        FormId = record.FormId,
                        EditorId = GetEditorId(record.FormId),
                        FullName = FindFullNameNear(record.Offset),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? fullName = null;
                string? modelPath = null;
                ObjectBounds? bounds = null;
                uint? script = null;
                uint? openSound = null;
                uint? closeSound = null;
                uint? loopSound = null;
                byte flags = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "SCRI" when sub.DataLength == 4:
                            script = ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "SNAM" when sub.DataLength == 4:
                            openSound = ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "ANAM" when sub.DataLength == 4:
                            closeSound = ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "BNAM" when sub.DataLength == 4:
                            loopSound = ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "FNAM" when sub.DataLength == 1:
                            flags = subData[0];
                            break;
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _formIdToFullName.TryAdd(record.FormId, fullName);
                }

                doors.Add(new DoorRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? GetEditorId(record.FormId),
                    FullName = fullName,
                    ModelPath = modelPath,
                    Bounds = bounds,
                    Script = script != 0 ? script : null,
                    OpenSoundFormId = openSound != 0 ? openSound : null,
                    CloseSoundFormId = closeSound != 0 ? closeSound : null,
                    LoopSoundFormId = loopSound != 0 ? loopSound : null,
                    Flags = flags,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return doors;
    }

    #endregion

    #region Statics

    /// <summary>
    ///     Reconstruct all Static (STAT) records.
    /// </summary>
    public List<StaticRecord> ReconstructStatics()
    {
        var statics = new List<StaticRecord>();

        if (_accessor == null)
        {
            foreach (var record in GetRecordsByType("STAT"))
            {
                statics.Add(new StaticRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return statics;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("STAT"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    statics.Add(new StaticRecord
                    {
                        FormId = record.FormId,
                        EditorId = GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? modelPath = null;
                ObjectBounds? bounds = null;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                    }
                }

                statics.Add(new StaticRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? GetEditorId(record.FormId),
                    ModelPath = modelPath,
                    Bounds = bounds,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return statics;
    }

    #endregion

    #region Furniture

    /// <summary>
    ///     Reconstruct all Furniture (FURN) records.
    /// </summary>
    public List<FurnitureRecord> ReconstructFurniture()
    {
        var furniture = new List<FurnitureRecord>();

        if (_accessor == null)
        {
            foreach (var record in GetRecordsByType("FURN"))
            {
                furniture.Add(new FurnitureRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return furniture;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("FURN"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    furniture.Add(new FurnitureRecord
                    {
                        FormId = record.FormId,
                        EditorId = GetEditorId(record.FormId),
                        FullName = FindFullNameNear(record.Offset),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? fullName = null;
                string? modelPath = null;
                ObjectBounds? bounds = null;
                uint? script = null;
                uint markerFlags = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _formIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "SCRI" when sub.DataLength == 4:
                            script = ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "MNAM" when sub.DataLength == 4:
                            markerFlags = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                                : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                            break;
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _formIdToFullName.TryAdd(record.FormId, fullName);
                }

                furniture.Add(new FurnitureRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? GetEditorId(record.FormId),
                    FullName = fullName,
                    ModelPath = modelPath,
                    Bounds = bounds,
                    Script = script != 0 ? script : null,
                    MarkerFlags = markerFlags,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return furniture;
    }

    #endregion
}
