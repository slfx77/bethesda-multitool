using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscRecordHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Generic Records

    /// <summary>
    ///     Parse records of the given type into GenericEsmRecord instances.
    ///     Captures EDID, FULL, MODL, OBND as named properties, and all other
    ///     subrecords into the Fields dictionary using schema-based parsing when available.
    /// </summary>
    internal List<GenericEsmRecord> ParseGenericRecords(string recordType)
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

    #region Game Settings

    /// <summary>
    ///     Parse all Game Setting (GMST) records from the scan result.
    /// </summary>
    internal List<GameSettingRecord> ParseGameSettings()
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
                var setting = ParseGameSettingFromAccessor(record, buffer);
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

    private GameSettingRecord? ParseGameSettingFromAccessor(DetectedMainRecord record, byte[] buffer)
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
}
