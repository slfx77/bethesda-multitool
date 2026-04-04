using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Parsing.Handlers;

internal sealed class TextRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
    #region Terminals

    /// <summary>
    ///     Parse all Terminal records from the scan result.
    /// </summary>
    internal List<TerminalRecord> ParseTerminals()
    {
        var terminals = ParseRecordList<TerminalRecord>("TERM", 256,
            (record, _) => new TerminalRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            },
            record => new TerminalRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(terminals, 0x17, t => t.FormId,
            (reader, entry) => reader.ReadRuntimeTerminal(entry), "terminals");

        return terminals;
    }

    #endregion

    #region Books

    /// <summary>
    ///     Parse all Book records from the scan result.
    /// </summary>
    internal List<BookRecord> ParseBooks()
    {
        var books = ParseRecordList("BOOK", 16384, ParseBookFromAccessor,
            record => new BookRecord
            {
                FormId = record.FormId,
                EditorId = Context.GetEditorId(record.FormId),
                FullName = Context.FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });

        Context.MergeRuntimeRecords(books, 0x19, b => b.FormId,
            (reader, entry) => reader.ReadRuntimeBook(entry), "books");

        return books;
    }

    #endregion

    private BookRecord? ParseBookFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new BookRecord
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
        string? text = null;
        string? modelPath = null;
        ObjectBounds? bounds = null;
        byte flags = 0;
        byte skillTaught = 0;
        var value = 0;
        float weight = 0;

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
                    text = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "DATA" when sub.DataLength >= 10:
                {
                    // BOOK DATA: Flags(1) + SkillTaught(1) + Value(int32) + Weight(float)
                    flags = subData[0];
                    skillTaught = subData[1];
                    value = record.IsBigEndian
                        ? BinaryPrimitives.ReadInt32BigEndian(subData[2..])
                        : BinaryPrimitives.ReadInt32LittleEndian(subData[2..]);
                    weight = record.IsBigEndian
                        ? BinaryPrimitives.ReadSingleBigEndian(subData[6..])
                        : BinaryPrimitives.ReadSingleLittleEndian(subData[6..]);
                    break;
                }
            }
        }

        return new BookRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            Text = text,
            ModelPath = modelPath,
            Bounds = bounds,
            Flags = flags,
            SkillTaught = skillTaught,
            Value = value,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #region Messages

    /// <summary>
    ///     Parse all Message (MESG) records.
    /// </summary>
    internal List<MessageRecord> ParseMessages()
    {
        var messages = ParseAccessorOnly("MESG", 2048, ParseMessageFromAccessor);

        Context.MergeRuntimeRecords(messages, 0x62, m => m.FormId,
            (reader, entry) => reader.ReadRuntimeMessage(entry), "messages");

        return messages;
    }

    private MessageRecord? ParseMessageFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return null;
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null, fullName = null, description = null, icon = null;
        uint questFormId = 0, flags = 0, displayTime = 0;
        var buttons = new List<string>();

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
                case "QNAM" when sub.DataLength >= 4:
                    questFormId = RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength),
                        record.IsBigEndian);
                    break;
                case "DNAM" when sub.DataLength >= 4:
                    flags = RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength),
                        record.IsBigEndian);
                    break;
                case "TNAM" when sub.DataLength >= 4:
                    displayTime = RecordParserContext.ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength),
                        record.IsBigEndian);
                    break;
                case "ITXT":
                {
                    var btnText =
                        EsmStringUtils.ReadNullTermString(data.AsSpan(sub.DataOffset, sub.DataLength));
                    if (!string.IsNullOrEmpty(btnText))
                    {
                        buttons.Add(btnText);
                    }

                    break;
                }
            }
        }

        return new MessageRecord
        {
            FormId = record.FormId,
            EditorId = editorId,
            FullName = fullName,
            Description = description,
            Icon = icon,
            QuestFormId = questFormId,
            Flags = flags,
            DisplayTime = displayTime,
            Buttons = buttons,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

    #region Notes

    /// <summary>
    ///     Parse all Note records from the scan result.
    /// </summary>
    internal List<NoteRecord> ParseNotes()
    {
        var notes = ParseRecordList("NOTE", 8192, ParseNoteFromAccessor, ParseNoteFromScanResult);

        Context.MergeRuntimeRecords(notes, 0x31, n => n.FormId,
            (reader, entry) => reader.ReadRuntimeNote(entry), "notes");

        return notes;
    }

    private NoteRecord? ParseNoteFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ParseNoteFromScanResult(record);
        }

        var (data, dataSize) = recordData.Value;

        string? editorId = null;
        string? fullName = null;
        string? text = null;
        byte noteType = 0;

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
                case "DATA" when sub.DataLength >= 1:
                    noteType = subData[0];
                    break;
                case "TNAM":
                    text = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DESC": // Fallback for text content
                    if (string.IsNullOrEmpty(text))
                    {
                        text = EsmStringUtils.ReadNullTermString(subData);
                    }

                    break;
            }
        }

        return new NoteRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            FullName = fullName,
            NoteType = noteType,
            Text = text,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private NoteRecord? ParseNoteFromScanResult(DetectedMainRecord record)
    {
        return new NoteRecord
        {
            FormId = record.FormId,
            EditorId = Context.GetEditorId(record.FormId),
            FullName = Context.FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
