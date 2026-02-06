using System.Buffers;
using System.Buffers.Binary;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

public sealed partial class SemanticReconstructor
{
    #region Books

    /// <summary>
    ///     Reconstruct all Book records from the scan result.
    /// </summary>
    public List<ReconstructedBook> ReconstructBooks()
    {
        var books = new List<ReconstructedBook>();
        var bookRecords = GetRecordsByType("BOOK").ToList();

        foreach (var record in bookRecords)
        {
            books.Add(new ReconstructedBook
            {
                FormId = record.FormId,
                EditorId = GetEditorId(record.FormId),
                FullName = FindFullNameNear(record.Offset),
                Offset = record.Offset,
                IsBigEndian = record.IsBigEndian
            });
        }

        return books;
    }

    #endregion

    #region Terminals

    /// <summary>
    ///     Reconstruct all Terminal records from the scan result.
    /// </summary>
    public List<ReconstructedTerminal> ReconstructTerminals()
    {
        var terminals = new List<ReconstructedTerminal>();
        var terminalRecords = GetRecordsByType("TERM").ToList();

        foreach (var record in terminalRecords)
        {
            terminals.Add(new ReconstructedTerminal
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
    public List<ReconstructedMessage> ReconstructMessages()
    {
        var messages = new List<ReconstructedMessage>();

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
                            questFormId = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sub.DataOffset));
                            break;
                        case "DNAM" when sub.DataLength >= 4:
                            flags = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sub.DataOffset));
                            break;
                        case "TNAM" when sub.DataLength >= 4:
                            displayTime = record.IsBigEndian
                                ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(sub.DataOffset))
                                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(sub.DataOffset));
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

                messages.Add(new ReconstructedMessage
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
    public List<ReconstructedNote> ReconstructNotes()
    {
        var notes = new List<ReconstructedNote>();
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

    private ReconstructedNote? ReconstructNoteFromAccessor(DetectedMainRecord record, byte[] buffer)
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

        return new ReconstructedNote
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

    private ReconstructedNote? ReconstructNoteFromScanResult(DetectedMainRecord record)
    {
        return new ReconstructedNote
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
