using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for dialogue, quest, terminal, and note runtime structs from Xbox 360 memory dumps.
///     Extracts topic metadata, INFO records, quest data, terminal menus, and note content.
/// </summary>
internal sealed class RuntimeDialogueReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;
    private readonly InfoOffsets _info = InfoLayout;

    #region Struct Layouts

    /// <summary>
    ///     TESTopicInfo struct layout offsets.
    ///     All known dumps use Release/Final Debug PDB sizes (TESTopicInfo = 96 bytes).
    /// </summary>
    private sealed record InfoOffsets(
        int StructSize,
        int IndexOffset,
        int DataOffset,
        int PromptOffset,
        int SpeakerPtrOffset,
        int DifficultyOffset,
        int QuestPtrOffset);

    // TESTopicInfo: Proto Debug PDB = 80 bytes, all known dumps = 96 bytes (PDB + 16 shift).
    private static readonly InfoOffsets InfoLayout = new(96, 48, 51, 56, 76, 84, 88);

    // TESTopic layout — consistent across all known builds (Final Debug / Release PDB, 80 bytes).
    // FullName=+44, DataType=+52, Flags=+53, Priority=+56, QuestInfoList=+60, DummyPrompt=+68.
    private const int DialStructSize = 80;
    private const int DialFullNameOffset = 44;
    private const int DialDataTypeOffset = 52;
    private const int DialDataFlagsOffset = 53;
    private const int DialPriorityOffset = 56;
    private const int DialQuestInfoListOffset = 60;
    private const int DialDummyPromptOffset = 68;

    #endregion

    // Build-specific offset shift for Note/Quest/Terminal structs.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    #region Note Struct Layout (Proto Debug PDB base + _s)

    // BGSNote: PDB size 128, Debug dump 132, Release dump 144
    private int NoteStructSize => 128 + _s;
    private int NoteTypeOffset => 124 + _s;
    private int NoteModelPathOffset => 52 + _s;
    private int NoteFullNameOffset => 76 + _s;

    #endregion

    #region Quest Struct Layout (Proto Debug PDB base + _s)

    // TESQuest: PDB size 108, Debug dump 112, Release dump 124
    private int QustStructSize => 108 + _s;
    private int QustFlagsOffset => 60 + _s;
    private int QustPriorityOffset => 61 + _s;
    private int QustFullNameOffset => 52 + _s;

    #endregion

    #region Terminal Struct Layout (Proto Debug PDB base + _s)

    // BGSTerminal: PDB size 168, Debug dump 172, Release dump 184
    private int TermStructSize => 168 + _s;
    private int TermDifficultyOffset => 116 + _s;
    private int TermFlagsOffset => 117 + _s;
    private int TermPasswordOffset => 120 + _s;
    private int TermMenuItemListOffset => 136 + _s;
    // TERMINAL_MENU_ITEM offsets — fixed within the menu item struct, not TESForm-derived
    private const int MenuItemSize = 120;
    private const int MenuItemResponseTextOffset = 0;
    private const int MenuItemResultScriptOffset = 16;
    private const int MenuItemSubMenuOffset = 112;

    #endregion

    /// <summary>
    ///     Read extended topic data from a runtime TESTopic struct.
    ///     Returns topic metadata, or null if validation fails.
    /// </summary>
    public RuntimeDialogTopicInfo? ReadRuntimeDialogTopic(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + DialStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[DialStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, DialStructSize);
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
        var topicType = buffer[DialDataTypeOffset];
        var flags = buffer[DialDataFlagsOffset];

        // Validate topic type (0-7)
        if (topicType > 7)
        {
            return null;
        }

        // Read priority
        var priority = BinaryUtils.ReadFloatBE(buffer, DialPriorityOffset);
        if (!RuntimeMemoryContext.IsNormalFloat(priority) || priority < 0 || priority > 200)
        {
            priority = 0;
        }

        // Read topic count (may be beyond struct boundary for some builds)
        uint topicCount = 0;

        // Read FullName via BSStringT
        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, DialFullNameOffset);

        // Read DummyPrompt via BSStringT
        var dummyPrompt = _context.ReadBSStringT(offset, DialDummyPromptOffset);

        return new RuntimeDialogTopicInfo
        {
            FormId = formId,
            TopicType = topicType,
            Flags = flags,
            Priority = priority,
            TopicCount = topicCount,
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

        // Validate TOPIC_INFO_DATA — crash dumps often contain uninitialized template values
        // (e.g., nextSpeaker=0x82, flags=0x04 from Xbox 360 heap fill patterns).
        // Valid: type 0-7, nextSpeaker 0-2. If invalid, zero out all fields.
        if (dataNextSpeaker > 2 || dataType > 7)
        {
            dataType = 0;
            dataNextSpeaker = 0;
            dataFlags = 0;
            dataFlagsExt = 0;
        }

        // Follow pSpeaker pointer → TESActorBase* → get NPC FormID (0x2A) or Creature (0x2B)
        var speakerFormId = _context.FollowPointerToFormId(buffer, _info.SpeakerPtrOffset, 0x2A)
                            ?? _context.FollowPointerToFormId(buffer, _info.SpeakerPtrOffset, 0x2B);

        // Read eDifficulty (uint32 BE)
        var difficulty = BinaryUtils.ReadUInt32BE(buffer, _info.DifficultyOffset);
        if (difficulty > 10)
        {
            difficulty = 0; // Sanity check: difficulty should be 0-5
        }

        // Follow pOwnerQuest pointer → TESQuest* (0x47) → get Quest FormID
        var questFormId = _context.FollowPointerToFormId(buffer, _info.QuestPtrOffset, 0x47);

        return new RuntimeDialogueInfo
        {
            FormId = formId,
            InfoIndex = infoIndex,
            TopicType = dataType,
            NextSpeaker = dataNextSpeaker,
            InfoFlags = dataFlags,
            InfoFlagsExt = dataFlagsExt,
            SpeakerFormId = speakerFormId,
            Difficulty = difficulty,
            QuestFormId = questFormId,
            PromptText = entry.DialogueLine
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

        // Read iInfoIndex
        var infoIndex = BinaryUtils.ReadUInt16BE(buffer, _info.IndexOffset);

        // Read TOPIC_INFO_DATA
        byte dataType = 0, dataNextSpeaker = 0, dataFlags = 0, dataFlagsExt = 0;
        if (_info.DataOffset + 4 <= buffer.Length)
        {
            dataType = buffer[_info.DataOffset];
            dataNextSpeaker = buffer[_info.DataOffset + 1];
            dataFlags = buffer[_info.DataOffset + 2];
            dataFlagsExt = buffer[_info.DataOffset + 3];
        }

        // Validate TOPIC_INFO_DATA — crash dumps often contain uninitialized template values
        if (dataNextSpeaker > 2 || dataType > 7)
        {
            dataType = 0;
            dataNextSpeaker = 0;
            dataFlags = 0;
            dataFlagsExt = 0;
        }

        // Follow pSpeaker pointer → TESActorBase* → get NPC FormID (0x2A) or Creature (0x2B)
        var speakerFormId = _context.FollowPointerToFormId(buffer, _info.SpeakerPtrOffset, 0x2A)
                            ?? _context.FollowPointerToFormId(buffer, _info.SpeakerPtrOffset, 0x2B);

        // Read eDifficulty
        var difficulty = BinaryUtils.ReadUInt32BE(buffer, _info.DifficultyOffset);
        if (difficulty > 10)
        {
            difficulty = 0;
        }

        // Follow pOwnerQuest pointer → TESQuest* (0x47)
        var questFormId = _context.FollowPointerToFormId(buffer, _info.QuestPtrOffset, 0x47);

        // Read cPrompt BSStringT
        var promptText = _context.ReadBSStringT(fileOffset.Value, _info.PromptOffset);

        return new RuntimeDialogueInfo
        {
            FormId = formId,
            InfoIndex = infoIndex,
            TopicType = dataType,
            NextSpeaker = dataNextSpeaker,
            InfoFlags = dataFlags,
            InfoFlagsExt = dataFlagsExt,
            SpeakerFormId = speakerFormId,
            Difficulty = difficulty,
            QuestFormId = questFormId,
            PromptText = promptText,
            DumpOffset = fileOffset.Value
        };
    }

    /// <summary>
    ///     Read extended quest data from a runtime TESQuest struct.
    ///     Returns a QuestRecord with Flags/Priority, or null if validation fails.
    ///     Note: Stage and Objective lists require BSSimpleList traversal (Phase 5D).
    /// </summary>
    public QuestRecord? ReadRuntimeQuest(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x47)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + QustStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[QustStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, QustStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var flags = buffer[QustFlagsOffset];
        var priority = buffer[QustPriorityOffset];

        // Try to read quest display name from BSStringT at +68
        var fullName = _context.ReadBSStringT(offset, QustFullNameOffset);

        return new QuestRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            Priority = priority,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended terminal data from a runtime BGSTerminal struct.
    ///     Returns a TerminalRecord with difficulty, flags, and password.
    /// </summary>
    public TerminalRecord? ReadRuntimeTerminal(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x17)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + TermStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[TermStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, TermStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        // Read difficulty and flags
        var difficulty = buffer[TermDifficultyOffset];
        var flags = buffer[TermFlagsOffset];

        // Validate difficulty (0-4 range)
        if (difficulty > 4)
        {
            difficulty = 0; // Default to very easy if invalid
        }

        // Read password (optional)
        var password = _context.ReadBSStringT(offset, TermPasswordOffset);

        // Parse menu items from BSSimpleList at +152
        var menuItems = WalkTerminalMenuItemList(offset);

        return new TerminalRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = entry.DisplayName,
            Difficulty = difficulty,
            Flags = flags,
            Password = password,
            MenuItems = menuItems,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read extended note data from a runtime BGSNote struct.
    ///     Returns a NoteRecord with NoteType and FullName, or null if validation fails.
    /// </summary>
    public NoteRecord? ReadRuntimeNote(RuntimeEditorIdEntry entry)
    {
        if (entry.TesFormOffset == null || entry.FormType != 0x31)
        {
            return null;
        }

        var offset = entry.TesFormOffset.Value;
        if (offset + NoteStructSize > _context.FileSize)
        {
            return null;
        }

        var buffer = new byte[NoteStructSize];
        try
        {
            _context.Accessor.ReadArray(offset, buffer, 0, NoteStructSize);
        }
        catch
        {
            return null;
        }

        var formId = BinaryUtils.ReadUInt32BE(buffer, 12);
        if (formId != entry.FormId)
        {
            return null;
        }

        var noteType = buffer[NoteTypeOffset];
        if (noteType > 3)
        {
            noteType = 0; // invalid, default to Sound
        }

        // BGSNote has TESModel at +64, TESFullName at +88 (reversed vs MISC/KEYM)
        var fullName = entry.DisplayName ?? _context.ReadBSStringT(offset, NoteFullNameOffset);
        var modelPath = _context.ReadBSStringT(offset, NoteModelPathOffset);

        return new NoteRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            NoteType = noteType,
            Offset = offset,
            IsBigEndian = true
        };
    }

    /// <summary>
    ///     Read a QUEST_INFO struct (52 bytes) to extract Quest FormID and INFO FormIDs.
    ///     QUEST_INFO layout:
    ///     +0  pQuest (TESQuest*, 4 bytes)
    ///     +4  infoArray (TopicInfoArray/NiTLargeArray, 24 bytes) — not used, parallel array
    ///     +28 infoLinkArray (BSSimpleArray&lt;INFO_LINK_ELEMENT,1024&gt;, 16 bytes)
    ///     +44 pRemovedQuest (TESQuest*, 4 bytes)
    ///     +48 bInitialized (bool, 1 byte)
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

        // Follow pQuest pointer at +0 → TESQuest FormID
        var pQuest = BinaryUtils.ReadUInt32BE(buf);
        var questFormId = _context.FollowPointerVaToFormId(pQuest);

        if (questFormId == null)
        {
            return null;
        }

        // Read infoArray (NiTLargeArray<TESTopicInfo*>) at +4:
        //   +4:  vtable (4)
        //   +8:  m_pBase (4) — pointer to TESTopicInfo*[]
        //   +12: m_uiMaxSize (4)
        //   +16: m_uiSize (4) — actual number of elements
        //   +20: m_uiESize (4)
        //   +24: m_uiGrowBy (4)
        var pBase = BinaryUtils.ReadUInt32BE(buf, 8);
        var arraySize = BinaryUtils.ReadUInt32BE(buf, 16);

        var infoEntries = new List<InfoPointerEntry>();

        if (pBase != 0 && arraySize > 0 && arraySize <= 2000)
        {
            var baseFileOffset = _context.VaToFileOffset(pBase);
            if (baseFileOffset != null)
            {
                // Each element is a TESTopicInfo* pointer (4 bytes)
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
    ///     Walk the MenuItemList BSSimpleList on a BGSTerminal struct to extract
    ///     terminal menu items. Each list node points to a TERMINAL_MENU_ITEM struct
    ///     (120 bytes) containing ResponseText, ResultScript, and pSubMenu.
    /// </summary>
    private List<TerminalMenuItem> WalkTerminalMenuItemList(long terminalOffset)
    {
        var results = new List<TerminalMenuItem>();

        // Read the BSSimpleList inline node (8 bytes: m_item + m_pkNext) at +152
        var listOffset = terminalOffset + TermMenuItemListOffset;
        var listBuf = _context.ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        var firstItem = BinaryUtils.ReadUInt32BE(listBuf); // TERMINAL_MENU_ITEM* pointer
        var firstNext = BinaryUtils.ReadUInt32BE(listBuf, 4); // _Node* pointer

        // Process inline first item
        var firstMenuItem = ReadTerminalMenuItem(firstItem);
        if (firstMenuItem != null)
        {
            results.Add(firstMenuItem);
        }

        // Follow BSSimpleList chain
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

            var dataPtr = BinaryUtils.ReadUInt32BE(nodeBuf); // TERMINAL_MENU_ITEM*
            var nextPtr = BinaryUtils.ReadUInt32BE(nodeBuf, 4); // _Node*

            var menuItem = ReadTerminalMenuItem(dataPtr);
            if (menuItem != null)
            {
                results.Add(menuItem);
            }

            nextVA = nextPtr;
        }

        return results;
    }

    /// <summary>
    ///     Read a single TERMINAL_MENU_ITEM struct (120 bytes) from a virtual address.
    ///     Extracts ResponseText (+0), ResultScript (+16), and pSubMenu (+112).
    /// </summary>
    private TerminalMenuItem? ReadTerminalMenuItem(uint menuItemVA)
    {
        if (menuItemVA == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(menuItemVA);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, MenuItemSize);
        if (buf == null)
        {
            return null;
        }

        // Read ResponseText at +0 (BSStringT: 4-byte length pointer + 4-byte data pointer)
        var text = _context.ReadBSStringT(fileOffset.Value, MenuItemResponseTextOffset);

        // Read ResultScript FormID at +16
        var scriptPtr = BinaryUtils.ReadUInt32BE(buf, MenuItemResultScriptOffset);
        var resultScript = _context.FollowPointerVaToFormId(scriptPtr);

        // Read pSubMenu pointer at +112 → follow to BGSTerminal → get FormID
        var subMenuPtr = BinaryUtils.ReadUInt32BE(buf, MenuItemSubMenuOffset);
        var subTerminal = _context.FollowPointerVaToFormId(subMenuPtr);

        // Only return if we got at least some data
        if (text == null && resultScript == null && subTerminal == null)
        {
            return null;
        }

        return new TerminalMenuItem
        {
            Text = text,
            ResultScript = resultScript,
            SubTerminal = subTerminal
        };
    }

    /// <summary>
    ///     Walk the m_listQuestInfo BSSimpleList on a TESTopic struct to extract
    ///     Quest → INFO mappings. Each list node points to a QUEST_INFO struct
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
        if (offset + DialStructSize > _context.FileSize)
        {
            return results;
        }

        // Read the BSSimpleList inline node (8 bytes: m_item + m_pkNext)
        var listOffset = offset + DialQuestInfoListOffset;
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
}
