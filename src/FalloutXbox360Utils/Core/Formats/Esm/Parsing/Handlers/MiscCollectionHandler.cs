using System.Buffers;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing;

internal sealed class MiscCollectionHandler(RecordParserContext context) : RecordHandlerBase(context)
{

    #region Form Lists

    /// <summary>
    ///     Parse all Form ID List (FLST) records.
    /// </summary>
    internal List<FormListRecord> ParseFormLists()
    {
        var formLists = ParseRecordList("FLST", 8192,
            ParseFormListFromAccessor,
            record => new FormListRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeOverlayRecords(
            formLists,
            [0x55],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeFormList(entry),
            MergeFormList,
            "form lists");

        return formLists;
    }

    private FormListRecord? ParseFormListFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new FormListRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            };
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
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;
                case "LNAM" when sub.DataLength == 4:
                    formIds.Add(RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength),
                        record.IsBigEndian));
                    break;
            }
        }

        return new FormListRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FormIds = formIds,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Leveled Lists

    /// <summary>
    ///     Parse leveled list records (LVLI/LVLN/LVLC).
    /// </summary>
    internal List<LeveledListRecord> ParseLeveledLists()
    {
        var lists = new List<LeveledListRecord>();
        var lvliRecords = Context.GetRecordsByType("LVLI").ToList();
        var lvlnRecords = Context.GetRecordsByType("LVLN").ToList();
        var lvlcRecords = Context.GetRecordsByType("LVLC").ToList();

        // Combine all leveled list records
        var allRecords = lvliRecords
            .Concat(lvlnRecords)
            .Concat(lvlcRecords)
            .ToList();

        if (Context.Accessor == null)
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

        Context.MergeRuntimeOverlayRecords(
            lists,
            [0x2C, 0x2D, 0x34],
            record => record.FormId,
            static (reader, entry) => reader.ReadRuntimeLeveledList(entry),
            MergeLeveledList,
            "leveled lists");

        return lists;
    }

    private LeveledListRecord? ParseLeveledListFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseLeveledListFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
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
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    if (!string.IsNullOrEmpty(editorId))
                    {
                        Context.FormIdToEditorId[record.FormId] = editorId;
                    }

                    break;

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
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
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
            EditorId = Context.GetEditorId(record.FormId),
            ListType = record.RecordType,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static FormListRecord MergeFormList(FormListRecord esm, FormListRecord runtime)
    {
        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            FormIds = esm.FormIds.Count > 0 ? esm.FormIds : runtime.FormIds,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    private static LeveledListRecord MergeLeveledList(LeveledListRecord esm, LeveledListRecord runtime)
    {
        var hasEsmEntries = esm.Entries.Count > 0;

        return esm with
        {
            EditorId = esm.EditorId ?? runtime.EditorId,
            ListType = string.IsNullOrEmpty(esm.ListType) ? runtime.ListType : esm.ListType,
            ChanceNone = hasEsmEntries ? esm.ChanceNone : runtime.ChanceNone,
            Flags = hasEsmEntries ? esm.Flags : runtime.Flags,
            GlobalFormId = esm.GlobalFormId ?? runtime.GlobalFormId,
            Entries = hasEsmEntries ? esm.Entries : runtime.Entries,
            Offset = esm.Offset != 0 ? esm.Offset : runtime.Offset,
            IsBigEndian = esm.IsBigEndian || runtime.IsBigEndian
        };
    }

    #endregion
}
