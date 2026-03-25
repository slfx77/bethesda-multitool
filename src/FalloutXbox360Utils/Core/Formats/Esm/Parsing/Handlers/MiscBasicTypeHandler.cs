using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscBasicTypeHandler(RecordParserContext context) : RecordHandlerBase(context)
{

    #region Globals

    /// <summary>
    ///     Parse all Global Variable (GLOB) records.
    /// </summary>
    internal List<GlobalRecord> ParseGlobals()
    {
        var globals = ParseAccessorOnly("GLOB", 256, ParseGlobalFromAccessor);

        Context.MergeRuntimeRecords(globals, 0x06, g => g.FormId,
            (reader, entry) => reader.ReadRuntimeGlobal(entry), "globals");

        return globals;
    }

    private GlobalRecord? ParseGlobalFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
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
                        Context.FormIdToEditorId[record.FormId] = editorId;
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

        return new GlobalRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            ValueType = valueType,
            Value = value,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Classes

    /// <summary>
    ///     Parse all Class (CLAS) records.
    /// </summary>
    internal List<ClassRecord> ParseClasses()
    {
        var classes = ParseAccessorOnly("CLAS", 1024, ParseClassFromAccessor);

        Context.MergeRuntimeRecords(classes, 0x07, c => c.FormId,
            (reader, entry) => reader.ReadRuntimeClass(entry), "classes");

        return classes;
    }

    private ClassRecord? ParseClassFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
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

        return new ClassRecord
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
        };
    }

    #endregion

    #region Eyes

    /// <summary>
    ///     Parse all Eyes (EYES) records.
    /// </summary>
    internal List<EyesRecord> ParseEyes()
    {
        var eyes = ParseAccessorOnly("EYES", 512, ParseEyesFromAccessor);

        Context.MergeRuntimeRecords(eyes, 0x0B, e => e.FormId,
            (reader, entry) => reader.ReadRuntimeEyes(entry), "eyes");

        return eyes;
    }

    private EyesRecord? ParseEyesFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, texturePath = null;
        byte flags = 0;

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
                case "ICON":
                    texturePath =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "DATA" when sub.DataLength >= 1:
                    flags = data[sub.DataOffset];
                    break;
            }
        }

        return new EyesRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            TexturePath = texturePath,
            Flags = flags,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Hair

    /// <summary>
    ///     Parse all Hair (HAIR) records.
    /// </summary>
    internal List<HairRecord> ParseHair()
    {
        var hair = ParseAccessorOnly("HAIR", 512, ParseHairFromAccessor);

        Context.MergeRuntimeRecords(hair, 0x0A, h => h.FormId,
            (reader, entry) => reader.ReadRuntimeHair(entry), "hair");

        return hair;
    }

    private HairRecord? ParseHairFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, modelPath = null, texturePath = null;
        byte flags = 0;

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
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "ICON":
                    texturePath =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "DATA" when sub.DataLength >= 1:
                    flags = data[sub.DataOffset];
                    break;
            }
        }

        return new HairRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            ModelPath = modelPath,
            TexturePath = texturePath,
            Flags = flags,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Challenges

    /// <summary>
    ///     Parse all Challenge (CHAL) records.
    /// </summary>
    internal List<ChallengeRecord> ParseChallenges()
    {
        var challenges = ParseAccessorOnly("CHAL", 2048, ParseChallengeFromAccessor);

        Context.MergeRuntimeRecords(challenges, 0x71, c => c.FormId,
            (reader, entry) => reader.ReadRuntimeChallenge(entry), "challenges");

        return challenges;
    }

    private ChallengeRecord? ParseChallengeFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
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
                case "SCRI" when sub.DataLength >= 4:
                    script = RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength),
                        record.IsBigEndian);
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

        return new ChallengeRecord
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
        };
    }

    #endregion

    #region Reputations

    /// <summary>
    ///     Parse all Reputation (REPU) records.
    /// </summary>
    internal List<ReputationRecord> ParseReputations()
    {
        var reputations = ParseAccessorOnly("REPU", 256, ParseReputationFromAccessor);

        Context.MergeRuntimeRecords(reputations, 0x68, r => r.FormId,
            (reader, entry) => reader.ReadRuntimeReputation(entry), "reputations");

        return reputations;
    }

    private ReputationRecord? ParseReputationFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
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
                        Context.FormIdToEditorId[record.FormId] = editorId;
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

        return new ReputationRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            PositiveValue = positiveValue,
            NegativeValue = negativeValue,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
