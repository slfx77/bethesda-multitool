using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Item;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Script;
using BethesdaMultitool.Core.Utils;

namespace BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;

internal sealed class TextRecordHandler(RecordParserContext context) : RecordHandlerBase(context)
{
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
        string? iconPath = null;
        string? messageIconPath = null;
        byte[]? textureHashData = null;
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
                    fullName = Context.ReadFullName(subData);
                    break;
                case "DESC":
                    text = Context.ReadDescription(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ICON":
                    iconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MICO":
                    messageIconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MODT" when sub.DataLength > 0:
                    textureHashData = subData.ToArray();
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
            IconPath = iconPath,
            MessageIconPath = messageIconPath,
            TextureHashData = textureHashData,
            Bounds = bounds,
            Flags = flags,
            SkillTaught = skillTaught,
            Value = value,
            Weight = weight,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #region Terminals

    /// <summary>
    ///     Parse all Terminal records from the scan result.
    /// </summary>
    internal List<TerminalRecord> ParseTerminals()
    {
        var terminals = ParseRecordList<TerminalRecord>("TERM", 256,
            ParseTerminalFromAccessor,
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

    /// <summary>
    ///     Parse a TERM record's subrecord stream. Reads EDID/FULL/DESC/DNAM at the record
    ///     level, then collects menu items by tracking the per-item subrecord cycle
    ///     (ITXT → RNAM → ANAM → INAM?/TNAM? → SCHR+SCDA?+SCTX?+SCRO*+SCRV*).
    ///     FNV has no required NEXT separator. Embedded result
    ///     scripts are stored on the menu item via CompiledData/SourceText/ReferencedObjects.
    /// </summary>
    private TerminalRecord? ParseTerminalFromAccessor(DetectedMainRecord record, byte[] buffer)
    {
        var recordData = Context.ReadRecordData(record, buffer);
        if (recordData == null)
        {
            return new TerminalRecord
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
        string? modelPath = null;
        string? headerText = null;
        ObjectBounds? bounds = null;
        uint? scriptFormId = null;
        uint? soundLoopFormId = null;
        uint? passwordNoteFormId = null;
        byte difficulty = 0;
        byte flags = 0;
        byte serverType = 0;
        var menuItems = new List<TerminalMenuItem>();

        // Active menu-item accumulator. Reset on each NEXT separator (or on a new ITXT if a
        // record skips NEXT). Null until the first ITXT is seen.
        string? curText = null;
        string? curResultText = null;
        uint? curDisplayNoteFormId = null;
        uint? curSubTerminal = null;
        byte? curActionType = null;
        byte[]? curCompiledData = null;
        string? curSourceText = null;
        var curVariables = new List<ScriptVariableInfo>();
        var curReferencedObjects = new List<uint>();
        var curConditions = new List<DialogueCondition>();
        var conditionStrings = new ConditionStringSiblingBinder();
        SerializedScriptLocalTableParser? curSerializedLocals = null;
        var curHasMenuItem = false;
        var curHasSerializedHeader = false;
        var curHasMalformedSerializedHeader = false;
        uint curExpectedCompiledSize = 0;
        uint curExpectedVariableCount = 0;
        uint curExpectedReferenceCount = 0;
        var curSeenSourceText = false;
        var curSeenCompiledData = false;
        var curScriptBundleAmbiguous = false;

        void FlushMenuItem()
        {
            if (!curHasMenuItem) return;
            curSerializedLocals!.Complete();
            var isBigEndianBytecode = curCompiledData is { Length: > 0 }
                                      && CapturedScriptEmissionContract.InferBytecodeEndian(
                                          curCompiledData,
                                          curVariables,
                                          curReferencedObjects,
                                          fallbackIsBigEndian: record.IsBigEndian);
            var decompiledText = CapturedScriptEmissionContract.DecompileInline(
                curCompiledData,
                curVariables,
                curReferencedObjects,
                isBigEndianBytecode,
                !string.IsNullOrWhiteSpace(editorId)
                    ? $"{editorId}_Menu_{menuItems.Count + 1}"
                    : $"TERM_{record.FormId:X8}_Menu_{menuItems.Count + 1}",
                Context.ResolveFormName,
                ScriptFunctionTables.For(Context.Game));
            var isDmpDerived = Context.MinidumpInfo is not null;
            var sourceOrigin = isDmpDerived && !string.IsNullOrEmpty(curSourceText)
                ? ScriptSourceTextOrigin.DmpFragment
                : ScriptSourceTextOrigin.None;
            var sourceDecision = CapturedScriptEmissionContract.EvaluateInline(
                isDmpDerived,
                sourceOrigin,
                curCompiledData,
                curSourceText,
                decompiledText,
                curVariables,
                curReferencedObjects,
                isBigEndianBytecode);
            var hasInconsistentBundle = curScriptBundleAmbiguous
                                        || curSerializedLocals.IsMalformed
                                        || curHasMalformedSerializedHeader
                                        || curCompiledData is { Length: > 0 } compiled
                                        && (!curHasSerializedHeader
                                            || curExpectedCompiledSize != (uint)compiled.Length
                                            || curExpectedVariableCount != (uint)curVariables.Count
                                            || curExpectedReferenceCount !=
                                            (uint)curReferencedObjects.Count)
                                        || curCompiledData is not { Length: > 0 }
                                        && (curVariables.Count != 0
                                            || curReferencedObjects.Count != 0
                                            || curExpectedCompiledSize != 0
                                            || curExpectedVariableCount != 0
                                            || curExpectedReferenceCount != 0);
            menuItems.Add(new TerminalMenuItem
            {
                Text = curText,
                ResultText = curResultText,
                DisplayNoteFormId = curDisplayNoteFormId,
                SubTerminal = curSubTerminal,
                ActionType = curActionType,
                Conditions = curConditions.Count > 0 ? [..curConditions] : [],
                CompiledData = curCompiledData,
                SourceText = sourceDecision.SourceText,
                DecompiledText = decompiledText,
                SourceTextOrigin = sourceDecision.SourceText is null
                    ? ScriptSourceTextOrigin.None
                    : sourceOrigin,
                IsDmpDerived = isDmpDerived,
                Variables = curVariables.Count > 0 ? [..curVariables] : [],
                ReferencedObjects = curReferencedObjects.Count > 0 ? [..curReferencedObjects] : [],
                IsBigEndianBytecode = isBigEndianBytecode,
                IsIncompleteExecutableBundle = hasInconsistentBundle
                                               || !sourceDecision.ExecutableBundleSafe,
            });
            curText = null;
            curResultText = null;
            curDisplayNoteFormId = null;
            curSubTerminal = null;
            curActionType = null;
            curCompiledData = null;
            curSourceText = null;
            curVariables.Clear();
            curSerializedLocals = null;
            curReferencedObjects.Clear();
            curConditions.Clear();
            curHasMenuItem = false;
            curHasSerializedHeader = false;
            curHasMalformedSerializedHeader = false;
            curExpectedCompiledSize = 0;
            curExpectedVariableCount = 0;
            curExpectedReferenceCount = 0;
            curSeenSourceText = false;
            curSeenCompiledData = false;
            curScriptBundleAmbiguous = false;
        }

        foreach (var sub in EsmSubrecordUtils.IterateSubrecords(data, dataSize, record.IsBigEndian))
        {
            var subData = data.AsSpan(sub.DataOffset, sub.DataLength);
            if (conditionStrings.TryConsume(sub.Signature, subData))
            {
                continue;
            }

            if (curHasMenuItem)
            {
                curSerializedLocals!.ObserveSubrecord(
                    sub.Signature, subData, record.IsBigEndian);
            }

            switch (sub.Signature)
            {
                case "EDID":
                    editorId = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "OBND" when sub.DataLength == 12:
                    bounds = RecordParserContext.ReadObjectBounds(subData, record.IsBigEndian);
                    break;
                case "FULL":
                    fullName = Context.ReadFullName(subData);
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "DESC":
                    headerText = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "SCRI" when sub.DataLength == 4:
                    scriptFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SNAM" when sub.DataLength == 4:
                    soundLoopFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "PNAM" when sub.DataLength == 4:
                    passwordNoteFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "DNAM" when sub.DataLength >= 2:
                    // TERMINAL_DATA: byte Difficulty(0) + byte Flags(1) + byte ServerType(2) + byte Unused(3).
                    difficulty = subData[0];
                    flags = subData[1];
                    if (sub.DataLength >= 3)
                    {
                        serverType = subData[2];
                    }

                    break;
                case "ITXT":
                    // Beginning of a new menu item — flush any pending item (no NEXT before this).
                    FlushMenuItem();
                    curText = EsmStringUtils.ReadNullTermString(subData);
                    curHasMenuItem = true;
                    curSerializedLocals = new SerializedScriptLocalTableParser(curVariables);
                    break;
                case "ANAM" when sub.DataLength >= 1 && curHasMenuItem:
                    curActionType = subData[0];
                    break;
                case "CTDA" when curHasMenuItem:
                    if (CtdaParser.TryDecode(subData, record.IsBigEndian, out var terminalCondition, out _))
                    {
                        curConditions.Add(terminalCondition);
                        conditionStrings.Begin(curConditions);
                    }

                    break;
                case "RNAM" when curHasMenuItem:
                    // FNV RNAM is always the menu item's result text, including when its
                    // encoded byte length happens to be four. It is not a FormID field.
                    curResultText = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "INAM" when sub.DataLength == 4 && curHasMenuItem:
                    curDisplayNoteFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TNAM" when sub.DataLength == 4 && curHasMenuItem:
                    curSubTerminal = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "SCHR" when curHasMenuItem:
                    if (curHasSerializedHeader || curHasMalformedSerializedHeader)
                    {
                        curScriptBundleAmbiguous = true;
                        break;
                    }

                    if (sub.DataLength < 20)
                    {
                        curHasMalformedSerializedHeader = true;
                        break;
                    }

                    curHasSerializedHeader = true;
                    curExpectedReferenceCount = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[4..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[4..]);
                    curExpectedCompiledSize = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[8..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[8..]);
                    curExpectedVariableCount = record.IsBigEndian
                        ? BinaryPrimitives.ReadUInt32BigEndian(subData[12..])
                        : BinaryPrimitives.ReadUInt32LittleEndian(subData[12..]);
                    break;
                case "SCDA" when curHasMenuItem:
                    if (curSeenCompiledData)
                    {
                        curScriptBundleAmbiguous = true;
                    }
                    else
                    {
                        curSeenCompiledData = true;
                        curCompiledData = subData.ToArray();
                    }

                    break;
                case "SCTX" when curHasMenuItem:
                    if (curSeenSourceText)
                    {
                        curScriptBundleAmbiguous = true;
                    }
                    else
                    {
                        curSeenSourceText = true;
                        curSourceText = EsmStringUtils.ReadNullTermString(subData);
                    }

                    break;
                case "SLSD" when curHasMenuItem:
                    break;
                case "SCVR" when curHasMenuItem:
                    break;
                case "SCRO" when curHasMenuItem:
                    if (sub.DataLength < 4)
                    {
                        curScriptBundleAmbiguous = true;
                    }
                    else
                    {
                        curReferencedObjects.Add(
                            RecordParserContext.ReadFormId(subData, record.IsBigEndian));
                    }

                    break;
                case "SCRV" when curHasMenuItem:
                {
                    if (sub.DataLength < 4)
                    {
                        curScriptBundleAmbiguous = true;
                    }
                    else
                    {
                        var varIdx = record.IsBigEndian
                            ? BinaryPrimitives.ReadUInt32BigEndian(subData)
                            : BinaryPrimitives.ReadUInt32LittleEndian(subData);
                        curReferencedObjects.Add(0x80000000u | (varIdx & 0x7FFFFFFFu));
                    }

                    break;
                }
                case "NEXT":
                    FlushMenuItem();
                    break;
            }
        }

        // Final flush in case the record ends without a trailing NEXT.
        FlushMenuItem();

        return new TerminalRecord
        {
            FormId = record.FormId,
            EditorId = editorId ?? Context.GetEditorId(record.FormId),
            Bounds = bounds,
            FullName = fullName,
            ModelPath = modelPath,
            HeaderText = headerText,
            ScriptFormId = scriptFormId,
            SoundLoopFormId = soundLoopFormId,
            PasswordNoteFormId = passwordNoteFormId,
            Difficulty = difficulty,
            Flags = flags,
            ServerType = serverType,
            MenuItems = menuItems,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    #endregion

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
                    fullName = Context.ReadFullName(data.AsSpan(sub.DataOffset, sub.DataLength));
                    break;
                case "DESC":
                    description =
                        Context.ReadDescription(data.AsSpan(sub.DataOffset, sub.DataLength));
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
        string? modelPath = null;
        string? iconPath = null;
        string? texturePath = null;
        uint? soundFormId = null;
        uint? objectFormId = null;
        uint? topicFormId = null;
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
                    fullName = Context.ReadFullName(subData);
                    break;
                case "DATA" when sub.DataLength >= 1:
                    noteType = subData[0];
                    break;
                case "MODL":
                    modelPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "ICON":
                    iconPath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "MICO":
                case "XNAM":
                    texturePath = EsmStringUtils.ReadNullTermString(subData);
                    break;
                case "SNAM" when sub.DataLength >= 4:
                    soundFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "ONAM" when sub.DataLength >= 4:
                    objectFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    break;
                case "TNAM":
                    if (sub.DataLength == 4 && !LooksLikeInlineString(subData))
                    {
                        topicFormId = RecordParserContext.ReadFormId(subData, record.IsBigEndian);
                    }
                    else
                    {
                        text = EsmStringUtils.ReadNullTermString(subData);
                    }

                    break;
                case "DESC": // Fallback for text content
                    if (string.IsNullOrEmpty(text))
                    {
                        text = Context.ReadDescription(subData);
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
            ModelPath = modelPath,
            IconPath = iconPath,
            TexturePath = texturePath,
            SoundFormId = soundFormId,
            ObjectFormId = objectFormId,
            TopicFormId = topicFormId,
            Offset = record.Offset,
            IsBigEndian = record.IsBigEndian
        };
    }

    private static bool LooksLikeInlineString(ReadOnlySpan<byte> data)
    {
        var terminator = data.IndexOf((byte)0);
        if (terminator < 0)
        {
            return false;
        }

        for (var i = 0; i < terminator; i++)
        {
            if (data[i] is < 0x20 or > 0x7E)
            {
                return false;
            }
        }

        return terminator > 0;
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
