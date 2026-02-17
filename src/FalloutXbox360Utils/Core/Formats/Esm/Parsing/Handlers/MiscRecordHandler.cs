using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Globals

    /// <summary>
    ///     Reconstruct all Global Variable (GLOB) records.
    /// </summary>
    internal List<GlobalRecord> ReconstructGlobals()
    {
        var globals = new List<GlobalRecord>();

        if (_context.Accessor == null)
        {
            return globals;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in _context.GetRecordsByType("GLOB"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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
    internal List<ClassRecord> ReconstructClasses()
    {
        var classes = new List<ClassRecord>();

        if (_context.Accessor == null)
        {
            return classes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in _context.GetRecordsByType("CLAS"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
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
    internal List<ChallengeRecord> ReconstructChallenges()
    {
        var challenges = new List<ChallengeRecord>();

        if (_context.Accessor == null)
        {
            return challenges;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("CHAL"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                        case "SCRI" when sub.DataLength >= 4:
                            script = RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
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
    internal List<ReputationRecord> ReconstructReputations()
    {
        var reputations = new List<ReputationRecord>();

        if (_context.Accessor == null)
        {
            return reputations;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(256);
        try
        {
            foreach (var record in _context.GetRecordsByType("REPU"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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
    internal List<WeaponModRecord> ReconstructWeaponMods()
    {
        var mods = new List<WeaponModRecord>();

        if (_context.Accessor == null)
        {
            return mods;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in _context.GetRecordsByType("IMOD"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
    internal List<RecipeRecord> ReconstructRecipes()
    {
        var recipes = new List<RecipeRecord>();

        if (_context.Accessor == null)
        {
            return recipes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("RCPE"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
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

    #region Form Lists

    /// <summary>
    ///     Reconstruct all Form ID List (FLST) records.
    /// </summary>
    internal List<FormListRecord> ReconstructFormLists()
    {
        var formLists = new List<FormListRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("FLST"))
            {
                formLists.Add(new FormListRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return formLists;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            foreach (var record in _context.GetRecordsByType("FLST"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    formLists.Add(new FormListRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "LNAM" when sub.DataLength == 4:
                            formIds.Add(RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian));
                            break;
                    }
                }

                formLists.Add(new FormListRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
    internal List<ActivatorRecord> ReconstructActivators()
    {
        var activators = new List<ActivatorRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("ACTI"))
            {
                activators.Add(new ActivatorRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return activators;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("ACTI"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    activators.Add(new ActivatorRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FindFullNameNear(record.Offset),
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
                            bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "SCRI" when sub.DataLength == 4:
                            script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "SNAM" when sub.DataLength == 4:
                            activationSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "RNAM" when sub.DataLength == 4:
                            radioStation = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "WNAM" when sub.DataLength == 4:
                            waterType = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                    }
                }

                activators.Add(new ActivatorRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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

    #region Lights

    /// <summary>
    ///     Reconstruct all Light (LIGH) records.
    /// </summary>
    internal List<LightRecord> ReconstructLights()
    {
        var lights = new List<LightRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("LIGH"))
            {
                lights.Add(new LightRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return lights;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("LIGH"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    lights.Add(new LightRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FindFullNameNear(record.Offset),
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
                var duration = 0;
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

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
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
                }

                lights.Add(new LightRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
    internal List<DoorRecord> ReconstructDoors()
    {
        var doors = new List<DoorRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("DOOR"))
            {
                doors.Add(new DoorRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return doors;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("DOOR"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    doors.Add(new DoorRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FindFullNameNear(record.Offset),
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

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
                        case "SCRI" when sub.DataLength == 4:
                            script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "SNAM" when sub.DataLength == 4:
                            openSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "ANAM" when sub.DataLength == 4:
                            closeSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "BNAM" when sub.DataLength == 4:
                            loopSound = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "FNAM" when sub.DataLength == 1:
                            flags = subData[0];
                            break;
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
                }

                doors.Add(new DoorRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
    internal List<StaticRecord> ReconstructStatics()
    {
        var statics = new List<StaticRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("STAT"))
            {
                statics.Add(new StaticRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return statics;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("STAT"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    statics.Add(new StaticRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                    }
                }

                statics.Add(new StaticRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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
    internal List<FurnitureRecord> ReconstructFurniture()
    {
        var furniture = new List<FurnitureRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("FURN"))
            {
                furniture.Add(new FurnitureRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return furniture;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("FURN"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    furniture.Add(new FurnitureRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FindFullNameNear(record.Offset),
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
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

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
                        case "SCRI" when sub.DataLength == 4:
                            script = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
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
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
                }

                furniture.Add(new FurnitureRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
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

    #region Game Settings

    /// <summary>
    ///     Reconstruct all Game Setting (GMST) records from the scan result.
    /// </summary>
    internal List<GameSettingRecord> ReconstructGameSettings()
    {
        var settings = new List<GameSettingRecord>();
        var gmstRecords = _context.GetRecordsByType("GMST").ToList();

        if (_context.Accessor == null)
        {
            // Without accessor, just return basic info
            foreach (var record in gmstRecords)
            {
                settings.Add(new GameSettingRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
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
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new GameSettingRecord
            {
                FormId = record.FormId,
                EditorId = _context.GetEditorId(record.FormId),
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
                        _context.FormIdToEditorId[record.FormId] = editorId;
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

    #region Leveled Lists

    /// <summary>
    ///     Reconstruct leveled list records (LVLI/LVLN/LVLC).
    /// </summary>
    internal List<LeveledListRecord> ReconstructLeveledLists()
    {
        var lists = new List<LeveledListRecord>();
        var lvliRecords = _context.GetRecordsByType("LVLI").ToList();
        var lvlnRecords = _context.GetRecordsByType("LVLN").ToList();
        var lvlcRecords = _context.GetRecordsByType("LVLC").ToList();

        // Combine all leveled list records
        var allRecords = lvliRecords
            .Concat(lvlnRecords)
            .Concat(lvlcRecords)
            .ToList();

        if (_context.Accessor == null)
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
        var recordData = _context.ReadRecordData(record, buffer);
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
                    globalFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
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
            EditorId = _context.GetEditorId(record.FormId),
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
            EditorId = _context.GetEditorId(record.FormId),
            ListType = record.RecordType,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Sounds

    /// <summary>
    ///     Reconstruct all Sound (SOUN) records.
    /// </summary>
    internal List<SoundRecord> ReconstructSounds()
    {
        var sounds = new List<SoundRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("SOUN"))
            {
                sounds.Add(new SoundRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return sounds;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("SOUN"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    sounds.Add(new SoundRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                string? fileName = null;
                ObjectBounds? bounds = null;
                ushort minAtten = 0, maxAtten = 0;
                short staticAtten = 0;
                uint flags = 0;
                byte startTime = 0, endTime = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "FNAM":
                            fileName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "SNDD" when sub.DataLength >= 36:
                        {
                            var fields = SubrecordDataReader.ReadFields("SNDD", "SOUN", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                minAtten = (ushort)SubrecordDataReader.GetByte(fields, "MinAttenuationDistance");
                                maxAtten = (ushort)SubrecordDataReader.GetByte(fields, "MaxAttenuationDistance");
                                staticAtten = SubrecordDataReader.GetInt16(fields, "StaticAttenuation");
                                flags = SubrecordDataReader.GetUInt32(fields, "Flags");
                                startTime = SubrecordDataReader.GetByte(fields, "StartTime");
                                endTime = SubrecordDataReader.GetByte(fields, "EndTime");
                            }

                            break;
                        }
                    }
                }

                sounds.Add(new SoundRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    Bounds = bounds,
                    FileName = fileName,
                    MinAttenuationDistance = minAtten,
                    MaxAttenuationDistance = maxAtten,
                    StaticAttenuation = staticAtten,
                    Flags = flags,
                    StartTime = startTime,
                    EndTime = endTime,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return sounds;
    }

    #endregion

    #region Texture Sets

    /// <summary>
    ///     Reconstruct all Texture Set (TXST) records.
    /// </summary>
    internal List<TextureSetRecord> ReconstructTextureSets()
    {
        var textureSets = new List<TextureSetRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("TXST"))
            {
                textureSets.Add(new TextureSetRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return textureSets;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("TXST"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    textureSets.Add(new TextureSetRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                ObjectBounds? bounds = null;
                string? tx00 = null, tx01 = null, tx02 = null, tx03 = null, tx04 = null, tx05 = null;
                ushort txstFlags = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "TX00":
                            tx00 = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "TX01":
                            tx01 = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "TX02":
                            tx02 = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "TX03":
                            tx03 = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "TX04":
                            tx04 = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "TX05":
                            tx05 = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "DNAM" when sub.DataLength >= 2:
                            txstFlags = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                                : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                            break;
                    }
                }

                textureSets.Add(new TextureSetRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    Bounds = bounds,
                    DiffuseTexture = tx00,
                    NormalTexture = tx01,
                    EnvironmentTexture = tx02,
                    GlowTexture = tx03,
                    ParallaxTexture = tx04,
                    EnvironmentMapTexture = tx05,
                    Flags = txstFlags,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return textureSets;
    }

    #endregion

    #region Armor Addons

    /// <summary>
    ///     Reconstruct all Armor Addon (ARMA) records.
    /// </summary>
    internal List<ArmaRecord> ReconstructArmorAddons()
    {
        var addons = new List<ArmaRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("ARMA"))
            {
                addons.Add(new ArmaRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return addons;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("ARMA"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    addons.Add(new ArmaRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null;
                string? maleModel = null, femaleModel = null, maleFp = null, femaleFp = null;
                ObjectBounds? bounds = null;
                uint bipedFlags = 0, generalFlags = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "OBND" when sub.DataLength == 12:
                            bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                            break;
                        case "MODL":
                            maleModel = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MOD2":
                            femaleModel = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MOD3":
                            maleFp = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "MOD4":
                            femaleFp = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "DATA" when sub.DataLength >= 12:
                        {
                            var fields = SubrecordDataReader.ReadFields("DATA", "ARMA", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                bipedFlags = SubrecordDataReader.GetUInt32(fields, "BipedFlags");
                                generalFlags = SubrecordDataReader.GetUInt32(fields, "GeneralFlags");
                            }

                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
                }

                addons.Add(new ArmaRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    FullName = fullName,
                    Bounds = bounds,
                    MaleModelPath = maleModel,
                    FemaleModelPath = femaleModel,
                    MaleFirstPersonModelPath = maleFp,
                    FemaleFirstPersonModelPath = femaleFp,
                    BipedFlags = bipedFlags,
                    GeneralFlags = generalFlags,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return addons;
    }

    #endregion

    #region Actor Value Infos

    /// <summary>
    ///     Reconstruct all Actor Value Info (AVIF) records.
    /// </summary>
    internal List<ActorValueInfoRecord> ReconstructActorValueInfos()
    {
        var infos = new List<ActorValueInfoRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("AVIF"))
            {
                infos.Add(new ActorValueInfoRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return infos;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("AVIF"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    infos.Add(new ActorValueInfoRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, description = null, icon = null, abbreviation = null;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "DESC":
                            description = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "ICON":
                            icon = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "ANAM":
                            abbreviation = EsmStringUtils.ReadNullTermString(subData);
                            break;
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
                }

                infos.Add(new ActorValueInfoRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    FullName = fullName,
                    Description = description,
                    Icon = icon,
                    Abbreviation = abbreviation,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return infos;
    }

    #endregion

    #region Water

    /// <summary>
    ///     Reconstruct all Water (WATR) records.
    /// </summary>
    internal List<WaterRecord> ReconstructWater()
    {
        var water = new List<WaterRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("WATR"))
            {
                water.Add(new WaterRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return water;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("WATR"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    water.Add(new WaterRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, fullName = null, noiseTexture = null;
                byte opacity = 0;
                byte[]? waterFlags = null;
                uint? soundFormId = null;
                ushort damage = 0;
                Dictionary<string, object?>? visualProps = null;
                Dictionary<string, object?>? relatedWater = null;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "FULL":
                            fullName = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "NNAM":
                            noiseTexture = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "ANAM" when sub.DataLength >= 1:
                            opacity = subData[0];
                            break;
                        case "FNAM":
                        {
                            waterFlags = new byte[sub.DataLength];
                            subData.CopyTo(waterFlags);
                            break;
                        }
                        case "SNAM" when sub.DataLength == 4:
                            soundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "DATA" when sub.DataLength == 2:
                            damage = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt16BigEndian(subData)
                                : BinaryPrimitives.ReadUInt16LittleEndian(subData);
                            break;
                        case "DNAM" when sub.DataLength == 196:
                        {
                            var fields = SubrecordDataReader.ReadFields("DNAM", "WATR", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                visualProps = fields;
                            }

                            break;
                        }
                        case "GNAM" when sub.DataLength == 12:
                        {
                            var fields = SubrecordDataReader.ReadFields("GNAM", "WATR", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                relatedWater = fields;
                            }

                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
                }

                water.Add(new WaterRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    FullName = fullName,
                    NoiseTexture = noiseTexture,
                    Opacity = opacity,
                    WaterFlags = waterFlags,
                    SoundFormId = soundFormId != 0 ? soundFormId : null,
                    Damage = damage,
                    VisualProperties = visualProps,
                    RelatedWater = relatedWater,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return water;
    }

    #endregion

    #region Body Part Data

    /// <summary>
    ///     Reconstruct all Body Part Data (BPTD) records.
    /// </summary>
    internal List<BodyPartDataRecord> ReconstructBodyPartData()
    {
        var parts = new List<BodyPartDataRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("BPTD"))
            {
                parts.Add(new BodyPartDataRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return parts;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("BPTD"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    parts.Add(new BodyPartDataRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null, modelPath = null;
                var partNames = new List<string>();
                var nodeNames = new List<string>();
                uint textureCount = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "MODL":
                            modelPath = EsmStringUtils.ReadNullTermString(subData);
                            break;
                        case "BPTN":
                        {
                            var name = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(name))
                            {
                                partNames.Add(name);
                            }

                            break;
                        }
                        case "BPNN":
                        {
                            var name = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(name))
                            {
                                nodeNames.Add(name);
                            }

                            break;
                        }
                        case "NAM5" when sub.DataLength >= 4:
                        {
                            var fields = SubrecordDataReader.ReadFields("NAM5", "BPTD", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                textureCount = SubrecordDataReader.GetUInt32(fields, "TextureCount");
                            }

                            break;
                        }
                    }
                }

                parts.Add(new BodyPartDataRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    ModelPath = modelPath,
                    PartNames = partNames,
                    NodeNames = nodeNames,
                    TextureCount = textureCount,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return parts;
    }

    #endregion

    #region Combat Styles

    /// <summary>
    ///     Reconstruct all Combat Style (CSTY) records.
    /// </summary>
    internal List<CombatStyleRecord> ReconstructCombatStyles()
    {
        var styles = new List<CombatStyleRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("CSTY"))
            {
                styles.Add(new CombatStyleRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return styles;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in _context.GetRecordsByType("CSTY"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    styles.Add(new CombatStyleRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                Dictionary<string, object?>? styleData = null;
                Dictionary<string, object?>? advancedData = null;
                Dictionary<string, object?>? simpleData = null;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "CSTD":
                        {
                            var fields = SubrecordDataReader.ReadFields("CSTD", "CSTY", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                styleData = fields;
                            }

                            break;
                        }
                        case "CSAD":
                        {
                            var fields = SubrecordDataReader.ReadFields("CSAD", "CSTY", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                advancedData = fields;
                            }

                            break;
                        }
                        case "CSSD":
                        {
                            var fields = SubrecordDataReader.ReadFields("CSSD", "CSTY", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                simpleData = fields;
                            }

                            break;
                        }
                    }
                }

                styles.Add(new CombatStyleRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    StyleData = styleData,
                    AdvancedData = advancedData,
                    SimpleData = simpleData,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return styles;
    }

    #endregion

    #region Lighting Templates

    /// <summary>
    ///     Reconstruct all Lighting Template (LGTM) records.
    /// </summary>
    internal List<LightingTemplateRecord> ReconstructLightingTemplates()
    {
        var templates = new List<LightingTemplateRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("LGTM"))
            {
                templates.Add(new LightingTemplateRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return templates;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(1024);
        try
        {
            foreach (var record in _context.GetRecordsByType("LGTM"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    templates.Add(new LightingTemplateRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                Dictionary<string, object?>? lightingData = null;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "DATA" when sub.DataLength == 40:
                        {
                            var fields = SubrecordDataReader.ReadFields("DATA", "LGTM", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                lightingData = fields;
                            }

                            break;
                        }
                    }
                }

                templates.Add(new LightingTemplateRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    LightingData = lightingData,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return templates;
    }

    #endregion

    #region Navigation Meshes

    /// <summary>
    ///     Reconstruct all Navigation Mesh (NAVM) records.
    /// </summary>
    internal List<NavMeshRecord> ReconstructNavMeshes()
    {
        var meshes = new List<NavMeshRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("NAVM"))
            {
                meshes.Add(new NavMeshRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return meshes;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            foreach (var record in _context.GetRecordsByType("NAVM"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    meshes.Add(new NavMeshRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                uint cellFormId = 0, vertexCount = 0, triangleCount = 0;
                var doorPortalCount = 0;

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "DATA" when sub.DataLength >= 20:
                        {
                            var fields = SubrecordDataReader.ReadFields("DATA", "NAVM", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                cellFormId = SubrecordDataReader.GetUInt32(fields, "Cell");
                                vertexCount = SubrecordDataReader.GetUInt32(fields, "VertexCount");
                                triangleCount = SubrecordDataReader.GetUInt32(fields, "TriangleCount");
                            }

                            break;
                        }
                        case "NVDP":
                            // Each door portal is 8 bytes
                            if (sub.DataLength >= 8)
                            {
                                doorPortalCount = sub.DataLength / 8;
                            }

                            break;
                    }
                }

                meshes.Add(new NavMeshRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    CellFormId = cellFormId,
                    VertexCount = vertexCount,
                    TriangleCount = triangleCount,
                    DoorPortalCount = doorPortalCount,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return meshes;
    }

    #endregion

    #region Weather

    /// <summary>
    ///     Reconstruct all Weather (WTHR) records.
    /// </summary>
    internal List<WeatherRecord> ReconstructWeather()
    {
        var weather = new List<WeatherRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType("WTHR"))
            {
                weather.Add(new WeatherRecord
                {
                    FormId = record.FormId,
                    EditorId = _context.GetEditorId(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return weather;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType("WTHR"))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    weather.Add(new WeatherRecord
                    {
                        FormId = record.FormId,
                        EditorId = _context.GetEditorId(record.FormId),
                        Offset = record.Offset,
                        IsBigEndian = record.IsBigEndian
                    });
                    continue;
                }

                var (data, dataSize) = recordData.Value;

                string? editorId = null;
                uint? imageSpaceMod = null;
                var sounds = new List<WeatherSound>();

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

                            break;
                        case "ONAM" when sub.DataLength == 4:
                            imageSpaceMod = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                            break;
                        case "SNAM" when sub.DataLength == 8:
                        {
                            var fields = SubrecordDataReader.ReadFields("SNAM", "WTHR", subData, record.IsBigEndian);
                            if (fields.Count > 0)
                            {
                                sounds.Add(new WeatherSound
                                {
                                    SoundFormId = SubrecordDataReader.GetUInt32(fields, "Sound"),
                                    Type = SubrecordDataReader.GetUInt32(fields, "Type")
                                });
                            }

                            break;
                        }
                    }
                }

                weather.Add(new WeatherRecord
                {
                    FormId = record.FormId,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    ImageSpaceModifier = imageSpaceMod != 0 ? imageSpaceMod : null,
                    Sounds = sounds,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return weather;
    }

    #endregion

    #region Generic Records

    /// <summary>
    ///     Reconstruct records of the given type into GenericEsmRecord instances.
    ///     Captures EDID, FULL, MODL, OBND as named properties, and all other
    ///     subrecords into the Fields dictionary using schema-based parsing when available.
    /// </summary>
    internal List<GenericEsmRecord> ReconstructGenericRecords(string recordType)
    {
        var records = new List<GenericEsmRecord>();

        if (_context.Accessor == null)
        {
            foreach (var record in _context.GetRecordsByType(recordType))
            {
                records.Add(new GenericEsmRecord
                {
                    FormId = record.FormId,
                    RecordType = recordType,
                    EditorId = _context.GetEditorId(record.FormId),
                    FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }

            return records;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(4096);
        try
        {
            foreach (var record in _context.GetRecordsByType(recordType))
            {
                var recordData = _context.ReadRecordData(record, buffer);
                if (recordData == null)
                {
                    records.Add(new GenericEsmRecord
                    {
                        FormId = record.FormId,
                        RecordType = recordType,
                        EditorId = _context.GetEditorId(record.FormId),
                        FullName = _context.FormIdToFullName.GetValueOrDefault(record.FormId),
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
                var fields = new Dictionary<string, object?>();

                foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
                {
                    var subData = data.AsSpan(sub.DataOffset, sub.DataLength);

                    switch (sub.Signature)
                    {
                        case "EDID":
                            editorId = EsmStringUtils.ReadNullTermString(subData);
                            if (!string.IsNullOrEmpty(editorId))
                            {
                                _context.FormIdToEditorId[record.FormId] = editorId;
                            }

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
                        default:
                        {
                            // Try schema-based parsing first
                            if (SubrecordDataReader.HasSchema(sub.Signature, recordType, sub.DataLength))
                            {
                                var schemaFields = SubrecordDataReader.ReadFields(
                                    sub.Signature, recordType, subData, record.IsBigEndian);
                                if (schemaFields.Count > 0)
                                {
                                    fields[sub.Signature] = schemaFields;
                                    break;
                                }
                            }

                            // String subrecords (common patterns)
                            if (sub.Signature is "ICON" or "ICO2" or "MICO" or "DESC"
                                or "NNAM" or "TX00" or "TX01" or "TX02" or "TX03" or "TX04" or "TX05")
                            {
                                fields[sub.Signature] = EsmStringUtils.ReadNullTermString(subData);
                                break;
                            }

                            // FormID subrecords (4 bytes)
                            if (sub.DataLength == 4 && sub.Signature is "SCRI" or "SNAM"
                                or "VNAM" or "LNAM" or "RNAM" or "WNAM" or "XNAM" or "ONAM"
                                or "INAM" or "TNAM" or "YNAM" or "ZNAM" or "HNAM" or "DNAM"
                                or "NAM1" or "NAM8" or "NAM9" or "NAM0")
                            {
                                var formId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                                if (formId != 0)
                                {
                                    fields[sub.Signature] = formId;
                                }

                                break;
                            }

                            // Store raw bytes for unrecognized subrecords
                            if (sub.DataLength > 0)
                            {
                                var raw = new byte[sub.DataLength];
                                subData.CopyTo(raw);
                                fields[sub.Signature] = raw;
                            }

                            break;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(fullName))
                {
                    _context.FormIdToFullName.TryAdd(record.FormId, fullName);
                }

                records.Add(new GenericEsmRecord
                {
                    FormId = record.FormId,
                    RecordType = recordType,
                    EditorId = editorId ?? _context.GetEditorId(record.FormId),
                    FullName = fullName,
                    ModelPath = modelPath,
                    Bounds = bounds,
                    Fields = fields,
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return records;
    }

    #endregion
}
