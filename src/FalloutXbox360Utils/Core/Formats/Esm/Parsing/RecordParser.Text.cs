using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Conversion;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class RecordParser
{
    #region Books

    /// <summary>
    ///     Reconstruct all Book records from the scan result.
    /// </summary>
    public List<BookRecord> ReconstructBooks()
    {
        var books = new List<BookRecord>();
        var bookRecords = GetRecordsByType("BOOK").ToList();

        if (_accessor == null)
        {
            foreach (var record in bookRecords)
            {
                books.Add(new BookRecord
                {
                    FormId = record.FormId,
                    EditorId = GetEditorId(record.FormId),
                    FullName = FindFullNameNear(record.Offset),
                    Offset = record.Offset,
                    IsBigEndian = record.IsBigEndian
                });
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(16384);
            try
            {
                foreach (var record in bookRecords)
                {
                    var book = ReconstructBookFromAccessor(record, buffer);
                    if (book != null)
                    {
                        books.Add(book);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return books;
    }

    private BookRecord? ReconstructBookFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new BookRecord
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
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
                    bounds = ReadObjectBounds(subData, record.IsBigEndian);
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
            EditorId = editorId ?? GetEditorId(record.FormId),
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

    #endregion

    #region Terminals

    /// <summary>
    ///     Reconstruct all Terminal records from the scan result.
    /// </summary>
    public List<TerminalRecord> ReconstructTerminals()
    {
        var terminals = new List<TerminalRecord>();
        var terminalRecords = GetRecordsByType("TERM").ToList();

        foreach (var record in terminalRecords)
        {
            terminals.Add(new TerminalRecord
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        // Merge terminals from runtime struct reading
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(terminals.Select(t => t.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x17 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var terminal = _runtimeReader.ReadRuntimeTerminal(entry);
                if (terminal != null)
                {
                    terminals.Add(terminal);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} terminals from runtime struct reading " +
                    $"(total: {terminals.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return terminals;
    }

    #endregion

    #region Messages

    /// <summary>
    ///     Reconstruct all Message (MESG) records.
    /// </summary>
    public List<MessageRecord> ReconstructMessages()
    {
        var messages = new List<MessageRecord>();

        if (_accessor == null)
        {
            return messages;
        }

        var buffer = ArrayPool<byte>.Shared.Rent(2048);
        try
        {
            foreach (var record in GetRecordsByType("MESG"))
            {
                var recordData = ReadRecordData(record, buffer);
                if (recordData == null) { continue; }
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
                        case "QNAM" when sub.DataLength >= 4:
                            questFormId = ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            break;
                        case "DNAM" when sub.DataLength >= 4:
                            flags = ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
                            break;
                        case "TNAM" when sub.DataLength >= 4:
                            displayTime = ReadFormId(data.AsSpan(sub.DataOffset, sub.DataLength), record.IsBigEndian);
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

                messages.Add(new MessageRecord
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
                });
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return messages;
    }

    #endregion

    #region Notes

    /// <summary>
    ///     Reconstruct all Note records from the scan result.
    /// </summary>
    public List<NoteRecord> ReconstructNotes()
    {
        var notes = new List<NoteRecord>();
        var noteRecords = GetRecordsByType("NOTE").ToList();

        if (_accessor == null)
        {
            foreach (var record in noteRecords)
            {
                var note = ReconstructNoteFromScanResult(record);
                if (note != null)
                {
                    notes.Add(note);
                }
            }
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(8192);
            try
            {
                foreach (var record in noteRecords)
                {
                    var note = ReconstructNoteFromAccessor(record, buffer);
                    if (note != null)
                    {
                        notes.Add(note);
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        // Merge notes from runtime struct reading (hash table entries not found as ESM records)
        if (_runtimeReader != null)
        {
            var esmFormIds = new HashSet<uint>(notes.Select(n => n.FormId));
            var runtimeCount = 0;
            foreach (var entry in _scanResult.RuntimeEditorIds)
            {
                if (entry.FormType != 0x31 || esmFormIds.Contains(entry.FormId))
                {
                    continue;
                }

                var note = _runtimeReader.ReadRuntimeNote(entry);
                if (note != null)
                {
                    notes.Add(note);
                    runtimeCount++;
                }
            }

            if (runtimeCount > 0)
            {
                Logger.Instance.Debug(
                    $"  [Semantic] Added {runtimeCount} notes from runtime struct reading " +
                    $"(total: {notes.Count}, ESM: {esmFormIds.Count})");
            }
        }

        return notes;
    }

    private NoteRecord? ReconstructNoteFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return ReconstructNoteFromScanResult(record);
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
            EditorId = editorId ?? GetEditorId(record.FormId),
            FullName = fullName,
            NoteType = noteType,
            Text = text,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private NoteRecord? ReconstructNoteFromScanResult(DetectedMainRecord record)
    {
        return new NoteRecord
        {
            FormId = record.FormId,
            EditorId = GetEditorId(record.FormId),
            FullName = FindFullNameNear(record.Offset),
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion
}
