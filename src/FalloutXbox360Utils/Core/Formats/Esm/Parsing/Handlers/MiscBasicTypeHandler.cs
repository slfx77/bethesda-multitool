using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscBasicTypeHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Globals

    /// <summary>
    ///     Parse all Global Variable (GLOB) records.
    /// </summary>
    internal List<GlobalRecord> ParseGlobals()
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
    ///     Parse all Class (CLAS) records.
    /// </summary>
    internal List<ClassRecord> ParseClasses()
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
    ///     Parse all Challenge (CHAL) records.
    /// </summary>
    internal List<ChallengeRecord> ParseChallenges()
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
    ///     Parse all Reputation (REPU) records.
    /// </summary>
    internal List<ReputationRecord> ParseReputations()
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
}
