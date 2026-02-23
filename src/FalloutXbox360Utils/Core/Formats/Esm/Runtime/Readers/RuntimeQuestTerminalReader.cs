using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using static FalloutXbox360Utils.Core.Formats.Esm.RuntimeDialogueLayouts;

namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Reader for quest, terminal, and note runtime structs from Xbox 360 memory dumps.
///     Extracts quest data, terminal menus with menu items, and note content.
/// </summary>
internal sealed class RuntimeQuestTerminalReader(RuntimeMemoryContext context)
{
    private readonly RuntimeMemoryContext _context = context;

    // Build-specific offset shift for Note/Quest/Terminal structs.
    private readonly int _s = RuntimeBuildOffsets.GetPdbShift(
        MinidumpAnalyzer.DetectBuildType(context.MinidumpInfo));

    /// <summary>
    ///     Read extended quest data from a runtime TESQuest struct.
    ///     Returns a QuestRecord with Flags/Priority, or null if validation fails.
    ///     Note: Stage and Objective lists require BSSimpleList traversal (Phase 5D).
    /// </summary>
    internal QuestRecord? ReadRuntimeQuest(RuntimeEditorIdEntry entry)
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
    internal TerminalRecord? ReadRuntimeTerminal(RuntimeEditorIdEntry entry)
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
    internal NoteRecord? ReadRuntimeNote(RuntimeEditorIdEntry entry)
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

    #region Quest Struct Layout (Proto Debug PDB base + _s)

    // TESQuest: PDB size 108, Debug dump 112, Release dump 124
    private int QustStructSize => 108 + _s;
    private int QustFlagsOffset => 60 + _s;
    private int QustPriorityOffset => 61 + _s;
    private int QustFullNameOffset => 52 + _s;

    #endregion

    #region Note Struct Layout (Proto Debug PDB base + _s)

    // BGSNote: PDB size 128, Debug dump 132, Release dump 144
    private int NoteStructSize => 128 + _s;
    private int NoteTypeOffset => 124 + _s;
    private int NoteModelPathOffset => 52 + _s;
    private int NoteFullNameOffset => 76 + _s;

    #endregion

    #region Terminal Struct Layout (Proto Debug PDB base + _s)

    // BGSTerminal: PDB size 168, Debug dump 172, Release dump 184
    private int TermStructSize => 168 + _s;
    private int TermDifficultyOffset => 116 + _s;
    private int TermFlagsOffset => 117 + _s;
    private int TermPasswordOffset => 120 + _s;

    private int TermMenuItemListOffset => 136 + _s;

    #endregion
}
