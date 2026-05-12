using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers;

/// <summary>
///     Reader for dialogue, quest, terminal, and note runtime structs from Xbox 360 memory dumps.
///     Extracts topic metadata, INFO records, quest data, terminal menus, and note content.
/// </summary>
internal sealed class RuntimeDialogueReader(
    RuntimeMemoryContext context,
    RuntimeTerminalLayoutProbeResult? terminalLayoutProbe = null)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly RuntimeTerminalLayoutProbeResult? _terminalLayoutProbe = terminalLayoutProbe;

    private readonly InfoOffsets _info = InfoLayout;

    // Delegate condition reading and list walking to the extracted helper class.
    private RuntimeDialogueConditionReader? _conditionReader;

    // Delegate quest/terminal/note reading to the extracted helper class.
    private RuntimeQuestTerminalReader? _questTerminalReader;

    private RuntimeQuestTerminalReader QuestTerminal =>
        _questTerminalReader ??= new RuntimeQuestTerminalReader(_context, _terminalLayoutProbe);

    private RuntimeDialogueConditionReader ConditionReader =>
        _conditionReader ??= new RuntimeDialogueConditionReader(_context);

    /// <summary>Accumulated diagnostics for TESConversationData link list population.</summary>
    internal ConversationDataDiagnostics ConversationDiagnostics { get; } = new();

    /// <summary>
    ///     Read extended topic data from a runtime TESTopic struct.
    ///     Returns topic metadata, journal index, and response count, or null if validation fails.
    /// </summary>
    public RuntimeDialogTopicInfo? ReadRuntimeDialogTopic(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + RuntimeDialogueLayouts.DialStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[RuntimeDialogueLayouts.DialStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, RuntimeDialogueLayouts.DialStructSize);
        }
        catch
        {
            return null;
        }

        // Validate: FormID at offset 12 should match
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read topic type and flags
        var topicType = buffer[RuntimeDialogueLayouts.DialDataTypeOffset];
        var flags = buffer[RuntimeDialogueLayouts.DialDataFlagsOffset];

        // Validate topic type (0-7)
        if (topicType > 7)
        {
            return null;
        }

        // Read priority
        var priority = BinaryUtils.ReadFloatBE(buffer, RuntimeDialogueLayouts.DialPriorityOffset);
        if (!RuntimeMemoryContext.IsNormalFloat(priority) || priority < 0 || priority > 200)
        {
            priority = 0;
        }

        // Read journal index (typically non-negative, but some builds use -1 as "unset")
        var journalIndex = BinaryUtils.ReadInt32BE(buffer, RuntimeDialogueLayouts.DialJournalIndexOffset);
        if (journalIndex < -1 || journalIndex > 100000)
        {
            journalIndex = 0;
        }

        // Read topic count
        var topicCount = BinaryUtils.ReadUInt32BE(buffer, RuntimeDialogueLayouts.DialTopicCountOffset);
        if (topicCount > 10000)
        {
            topicCount = 0;
        }

        // Read FullName via BSStringT
        var fullName = entry.DisplayName ?? _context.ReadBsStringT(offset, RuntimeDialogueLayouts.DialFullNameOffset);

        // Read DummyPrompt via BSStringT
        var dummyPrompt = _context.ReadBsStringT(offset, RuntimeDialogueLayouts.DialDummyPromptOffset);

        return new RuntimeDialogTopicInfo
        {
            FormId = formId,
            TopicType = topicType,
            Flags = flags,
            Priority = priority,
            TopicCount = topicCount,
            JournalIndex = journalIndex,
            FullName = fullName,
            DummyPrompt = dummyPrompt
        };
    }

    /// <summary>
    ///     Read extended dialogue info data from a runtime TESTopicInfo struct.
    ///     Extracts speaker, quest, flags, difficulty, and info index.
    ///     Returns null if the struct cannot be read or validation fails.
    /// </summary>
    public RuntimeDialogueInfo? ReadRuntimeDialogueInfo(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + _info.StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[_info.StructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, _info.StructSize);
        }
        catch
        {
            return null;
        }

        // Validate: FormID at offset 12 should match the hash table entry
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var infoFields = ReadInfoFields(buffer, offset);

        return new RuntimeDialogueInfo
        {
            FormId = formId,
            InfoIndex = infoFields.InfoIndex,
            TopicType = infoFields.DataType,
            NextSpeaker = infoFields.DataNextSpeaker,
            InfoFlags = infoFields.DataFlags,
            InfoFlagsExt = infoFields.DataFlagsExt,
            SpeakerFormId = infoFields.SpeakerFormId,
            PerkSkillStatFormId = infoFields.PerkSkillStatFormId,
            Difficulty = infoFields.Difficulty,
            QuestFormId = infoFields.QuestFormId,
            ConditionSpeakerFormId = infoFields.ConditionData.ConditionSpeakerFormId,
            SpeakerFactionFormId = infoFields.ConditionData.SpeakerFactionFormId,
            SpeakerRaceFormId = infoFields.ConditionData.SpeakerRaceFormId,
            SpeakerVoiceTypeFormId = infoFields.ConditionData.SpeakerVoiceTypeFormId,
            PromptText = entry.DialogueLine ?? _context.ReadBsStringT(offset, _info.PromptOffset),
            DumpOffset = offset,
            SaidOnce = infoFields.SaidOnce,
            TesFileOffset = infoFields.TesFileOffset,
            AddTopicFormIds = infoFields.AddTopicFormIds,
            LinkFromTopicFormIds = infoFields.ConversationData.LinkFromTopicFormIds,
            LinkToTopicFormIds = infoFields.ConversationData.LinkToTopicFormIds,
            FollowUpInfoFormIds = infoFields.ConversationData.FollowUpInfoFormIds,
            ConditionFunctions = infoFields.ConditionData.Functions,
            Conditions = infoFields.ConditionData.Conditions,
            FormEditorId = infoFields.FormEditorId
        };
    }

    /// <summary>
    ///     Read a TESTopicInfo struct from a virtual address (found via topic walk).
    ///     Similar to ReadRuntimeDialogueInfo but starts from a VA instead of a hash table entry.
    /// </summary>
    public RuntimeDialogueInfo? ReadRuntimeDialogueInfoFromVA(uint va)
    {
        var fileOffset = _context.VaToFileOffset(va);
        if (fileOffset == null || fileOffset.Value + _info.StructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[_info.StructSize];
        try
        {
            _context.Accessor.ReadArray(fileOffset.Value, buffer, 0, _info.StructSize);
        }
        catch
        {
            return null;
        }

        // Read FormID
        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId == 0 || formId == 0xFFFFFFFF)
        {
            return null;
        }

        var infoFields = ReadInfoFields(buffer, fileOffset.Value);

        // Read cPrompt BSStringT
        var promptText = _context.ReadBsStringT(fileOffset.Value, _info.PromptOffset);

        return new RuntimeDialogueInfo
        {
            FormId = formId,
            InfoIndex = infoFields.InfoIndex,
            TopicType = infoFields.DataType,
            NextSpeaker = infoFields.DataNextSpeaker,
            InfoFlags = infoFields.DataFlags,
            InfoFlagsExt = infoFields.DataFlagsExt,
            SpeakerFormId = infoFields.SpeakerFormId,
            PerkSkillStatFormId = infoFields.PerkSkillStatFormId,
            Difficulty = infoFields.Difficulty,
            QuestFormId = infoFields.QuestFormId,
            ConditionSpeakerFormId = infoFields.ConditionData.ConditionSpeakerFormId,
            SpeakerFactionFormId = infoFields.ConditionData.SpeakerFactionFormId,
            SpeakerRaceFormId = infoFields.ConditionData.SpeakerRaceFormId,
            SpeakerVoiceTypeFormId = infoFields.ConditionData.SpeakerVoiceTypeFormId,
            PromptText = promptText,
            DumpOffset = fileOffset.Value,
            SaidOnce = infoFields.SaidOnce,
            TesFileOffset = infoFields.TesFileOffset,
            AddTopicFormIds = infoFields.AddTopicFormIds,
            LinkFromTopicFormIds = infoFields.ConversationData.LinkFromTopicFormIds,
            LinkToTopicFormIds = infoFields.ConversationData.LinkToTopicFormIds,
            FollowUpInfoFormIds = infoFields.ConversationData.FollowUpInfoFormIds,
            ConditionFunctions = infoFields.ConditionData.Functions,
            Conditions = infoFields.ConditionData.Conditions,
            FormEditorId = infoFields.FormEditorId
        };
    }

    /// <summary>
    ///     Read extended quest data from a runtime TESQuest struct.
    ///     Delegates to <see cref="RuntimeQuestTerminalReader" />.
    /// </summary>
    public QuestRecord? ReadRuntimeQuest(RuntimeEditorIdEntry entry)
    {
        return QuestTerminal.ReadRuntimeQuest(entry);
    }

    /// <summary>
    ///     Read extended terminal data from a runtime BGSTerminal struct.
    ///     Delegates to <see cref="RuntimeQuestTerminalReader" />.
    /// </summary>
    public TerminalRecord? ReadRuntimeTerminal(RuntimeEditorIdEntry entry)
    {
        return QuestTerminal.ReadRuntimeTerminal(entry);
    }

    /// <summary>
    ///     Read extended note data from a runtime BGSNote struct.
    ///     Delegates to <see cref="RuntimeQuestTerminalReader" />.
    /// </summary>
    public NoteRecord? ReadRuntimeNote(RuntimeEditorIdEntry entry)
    {
        return QuestTerminal.ReadRuntimeNote(entry);
    }

    /// <summary>
    ///     Walk the m_listQuestInfo BSSimpleList on a TESTopic struct to extract
    ///     Quest to INFO mappings. Each list node points to a QUEST_INFO struct
    ///     (52 bytes) containing pQuest and infoLinkArray.
    ///     Returns a list of (QuestFormId, [InfoFormIds]) pairs.
    /// </summary>
    public List<TopicQuestLink> WalkTopicQuestInfoList(RuntimeEditorIdEntry entry)
    {
        var results = new List<TopicQuestLink>();

        if (entry.TesFormOffset == null)
        {
            return results;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + RuntimeDialogueLayouts.DialStructSize > _context.FileSize)
        {
            return results;
        }

        // Read the BSSimpleList inline node (8 bytes: m_item + m_pkNext)
        var listOffset = offset + RuntimeDialogueLayouts.DialQuestInfoListOffset;
        var listBuf = _context.ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(listBuf); // QUEST_INFO* pointer
        var firstNext = BinaryUtils.ReadUInt32BE(listBuf, 4); // _Node* pointer

        // Process inline first item
        var firstLink = ReadQuestInfo(firstItem);
        if (firstLink != null)
        {
            results.Add(firstLink);
        }

        // Follow BSSimpleList chain (same pattern as NPC inventory traversal)
        var nextVA = firstNext;
        var visited = new HashSet<uint>();
        while (nextVA != 0 && results.Count < RuntimeMemoryContext.MaxListItems && !visited.Contains(nextVA))
        {
            visited.Add(nextVA);
            var nodeFileOffset = _context.VaToFileOffset(nextVA);
            if (nodeFileOffset == null)
            {
                break;
            }

            var nodeBuf = _context.ReadBytes(nodeFileOffset.Value, 8);
            if (nodeBuf == null)
            {
                break;
            }

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf); // QUEST_INFO*
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4); // _Node*

            var link = ReadQuestInfo(dataPtr);
            if (link != null)
            {
                results.Add(link);
            }

            nextVA = nextPtr;
        }

        return results;
    }

    /// <summary>
    ///     Shared extraction of TESTopicInfo fields from a raw buffer at a known file offset.
    /// </summary>
    private InfoFieldsResult ReadInfoFields(byte[] buffer, long offset)
    {
        // Read iInfoIndex (uint16 BE)
        var infoIndex = BinaryUtils.ReadUInt16BE(buffer, _info.IndexOffset);

        // Read TOPIC_INFO_DATA (4 bytes): type, nextSpeaker, flags, flagsExt
        byte dataType = 0;
        byte dataNextSpeaker = 0;
        byte dataFlags = 0;
        byte dataFlagsExt = 0;
        if (_info.DataOffset + 4 <= buffer.Length)
        {
            dataType = buffer[_info.DataOffset];
            dataNextSpeaker = buffer[_info.DataOffset + 1];
            dataFlags = buffer[_info.DataOffset + 2];
            dataFlagsExt = buffer[_info.DataOffset + 3];
        }

        // Validate TOPIC_INFO_DATA
        if (dataNextSpeaker > 2 || dataType > 7)
        {
            dataType = 0;
            dataNextSpeaker = 0;
            dataFlags = 0;
            dataFlagsExt = 0;
        }

        // Follow pSpeaker pointer
        var speakerFormId = _context.FollowPointerToFormId(buffer, _info.SpeakerPtrOffset, 0x2A)
                            ?? _context.FollowPointerToFormId(buffer, _info.SpeakerPtrOffset, 0x2B);

        var perkSkillStatFormId = _context.FollowPointerToFormId(buffer, InfoPerkSkillStatPtrOffset);

        var difficulty = BinaryUtils.ReadUInt32BE(buffer, _info.DifficultyOffset);
        if (difficulty > 10)
        {
            difficulty = 0;
        }

        var questFormId = _context.FollowPointerToFormId(buffer, _info.QuestPtrOffset);

        var saidOnce = buffer[InfoSaidOnceOffset] != 0;
        var tesFileOffset = BinaryUtils.ReadUInt32BE(buffer, InfoFileOffsetOffset);

        var conditionData = ConditionReader.ReadConditions(offset, _info.ConditionsOffset);
        var addTopicFormIds = ConditionReader.WalkAddTopicsList(offset, InfoAddTopicsOffset);
        var conversationData = ReadConversationData(buffer);
        var formEditorId = _context.ReadBsStringT(offset, FormEditorIdOffset);

        return new InfoFieldsResult
        {
            InfoIndex = infoIndex,
            DataType = dataType,
            DataNextSpeaker = dataNextSpeaker,
            DataFlags = dataFlags,
            DataFlagsExt = dataFlagsExt,
            SpeakerFormId = speakerFormId,
            PerkSkillStatFormId = perkSkillStatFormId,
            Difficulty = difficulty,
            QuestFormId = questFormId,
            SaidOnce = saidOnce,
            TesFileOffset = tesFileOffset,
            AddTopicFormIds = addTopicFormIds,
            ConditionData = conditionData,
            ConversationData = conversationData,
            FormEditorId = formEditorId
        };
    }

    /// <summary>
    ///     Read a QUEST_INFO struct (52 bytes) to extract Quest FormID and INFO FormIDs.
    /// </summary>
    private TopicQuestLink? ReadQuestInfo(uint questInfoVA)
    {
        if (questInfoVA == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(questInfoVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, 52);
        if (buf == null)
        {
            return null;
        }

        // Follow pQuest pointer at +0
        var pQuest = BinaryUtils.ReadUInt32BE(buf);
        var questFormId = _context.FollowPointerVaToFormId(pQuest);

        if (questFormId == null)
        {
            return null;
        }

        // Read infoArray (NiTLargeArray<TESTopicInfo*>) at +4
        var pBase = BinaryUtils.ReadUInt32BE(buf, 8);
        var arraySize = BinaryUtils.ReadUInt32BE(buf, 16);

        var infoEntries = new List<InfoPointerEntry>();

        if (pBase != 0 && arraySize > 0 && arraySize <= 2000)
        {
            var baseFileOffset = _context.VaToFileOffset(pBase);
            if (baseFileOffset != null)
            {
                var elementCount = (int)Math.Min(arraySize, 1024);
                var elementBytes = _context.ReadBytes(baseFileOffset.Value, elementCount * 4);
                if (elementBytes != null)
                {
                    for (var i = 0; i < elementCount; i++)
                    {
                        var pInfo = BinaryUtils.ReadUInt32BE(elementBytes, i * 4);
                        var infoFormId = _context.FollowPointerVaToFormId(pInfo);
                        if (infoFormId != null)
                        {
                            infoEntries.Add(new InfoPointerEntry(infoFormId.Value, pInfo));
                        }
                    }
                }
            }
        }

        return new TopicQuestLink(questFormId.Value, infoEntries);
    }

    /// <summary>
    ///     Read TESTopicInfo.m_pConversationData (TESConversationData) and project its link lists.
    /// </summary>
    private RuntimeConversationData ReadConversationData(byte[] infoBuffer)
    {
        var results = new RuntimeConversationData();
        ConversationDiagnostics.ConversationDataReads++;

        if (_info.ConversationDataPtrOffset + 4 > infoBuffer.Length)
        {
            return results;
        }

        var conversationVa = BinaryUtils.ReadUInt32BE(infoBuffer, _info.ConversationDataPtrOffset);
        if (!_context.IsValidPointer(conversationVa))
        {
            return results;
        }

        var conversationBuf = _context.ReadBytesAtVa(conversationVa, ConversationDataSize);
        if (conversationBuf == null)
        {
            return results;
        }

        ConversationDiagnostics.ValidPointerCount++;

        var linkFromHead = BinaryUtils.ReadUInt32BE(conversationBuf);
        var linkToHead = BinaryUtils.ReadUInt32BE(conversationBuf, ConversationDataLinkToOffset);
        var followUpHead = BinaryUtils.ReadUInt32BE(conversationBuf, ConversationDataFollowUpInfosOffset);

        if (linkFromHead != 0) ConversationDiagnostics.LinkFromNonZeroHead++;
        if (linkToHead != 0) ConversationDiagnostics.LinkToNonZeroHead++;
        if (followUpHead != 0) ConversationDiagnostics.FollowUpNonZeroHead++;

        results.LinkFromTopicFormIds =
            ConditionReader.WalkFormIdSimpleList(conversationBuf, ConversationDataLinkFromOffset);
        results.LinkToTopicFormIds =
            ConditionReader.WalkFormIdSimpleList(conversationBuf, ConversationDataLinkToOffset);
        results.FollowUpInfoFormIds =
            ConditionReader.WalkFormIdSimpleList(conversationBuf, ConversationDataFollowUpInfosOffset);

        if (results.LinkFromTopicFormIds.Count > 0) ConversationDiagnostics.LinkFromPositiveDecodes++;
        if (results.LinkToTopicFormIds.Count > 0) ConversationDiagnostics.LinkToPositiveDecodes++;
        if (results.FollowUpInfoFormIds.Count > 0) ConversationDiagnostics.FollowUpPositiveDecodes++;

        return results;
    }

    private sealed class InfoFieldsResult
    {
        public List<uint> AddTopicFormIds = [];
        public RuntimeDialogueConditionReader.RuntimeConditionData ConditionData = new();
        public RuntimeConversationData ConversationData = new();
        public byte DataFlags;
        public byte DataFlagsExt;
        public byte DataNextSpeaker;
        public byte DataType;
        public uint Difficulty;
        public string? FormEditorId;
        public ushort InfoIndex;
        public uint? PerkSkillStatFormId;
        public uint? QuestFormId;
        public bool SaidOnce;
        public uint? SpeakerFormId;
        public uint TesFileOffset;
    }

    private sealed class RuntimeConversationData
    {
        public List<uint> FollowUpInfoFormIds { get; set; } = [];
        public List<uint> LinkFromTopicFormIds { get; set; } = [];
        public List<uint> LinkToTopicFormIds { get; set; } = [];
    }

    /// <summary>
    ///     Diagnostic counters for TESConversationData link list population.
    /// </summary>
    internal sealed class ConversationDataDiagnostics
    {
        public int ConversationDataReads { get; set; }
        public int ValidPointerCount { get; set; }
        public int LinkFromNonZeroHead { get; set; }
        public int LinkFromPositiveDecodes { get; set; }
        public int LinkToNonZeroHead { get; set; }
        public int LinkToPositiveDecodes { get; set; }
        public int FollowUpNonZeroHead { get; set; }
        public int FollowUpPositiveDecodes { get; set; }
    }

    #region Struct Layouts

    private sealed record InfoOffsets(
        int StructSize,
        int ConditionsOffset,
        int IndexOffset,
        int DataOffset,
        int PromptOffset,
        int ConversationDataPtrOffset,
        int SpeakerPtrOffset,
        int DifficultyOffset,
        int QuestPtrOffset);

    private static readonly InfoOffsets InfoLayout = new(96, 40, 48, 51, 56, 72, 76, 84, 88);

    private const int FormEditorIdOffset = 16;
    private const int InfoSaidOnceOffset = 50;
    private const int InfoAddTopicsOffset = 64;
    private const int InfoFileOffsetOffset = 92;
    private const int InfoPerkSkillStatPtrOffset = 80;
    private const int ConversationDataSize = 24;
    private const int ConversationDataLinkFromOffset = 0;
    private const int ConversationDataLinkToOffset = 8;
    private const int ConversationDataFollowUpInfosOffset = 16;

    #endregion

    // Note, Quest, and Terminal struct layout constants are provided by QuestTerminal.
}
