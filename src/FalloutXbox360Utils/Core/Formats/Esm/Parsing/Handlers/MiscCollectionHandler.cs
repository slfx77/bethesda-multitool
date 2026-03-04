using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscCollectionHandler(RecordParserContext context)
{
    private readonly RecordParserContext _context = context;

    #region Form Lists

    /// <summary>
    ///     Parse all Form ID List (FLST) records.
    /// </summary>
    internal List<FormListRecord> ParseFormLists()
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
                            formIds.Add(RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength),
                                record.IsBigEndian));
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

    #region Leveled Lists

    /// <summary>
    ///     Parse leveled list records (LVLI/LVLN/LVLC).
    /// </summary>
    internal List<LeveledListRecord> ParseLeveledLists()
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
                var list = ParseLeveledListFromScanResult(record);
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
                    var list = ParseLeveledListFromAccessor(record, buffer);
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

    private LeveledListRecord? ParseLeveledListFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = _context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseLeveledListFromScanResult(record);
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

    private LeveledListRecord? ParseLeveledListFromScanResult(DetectedMainRecord record)
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
}
