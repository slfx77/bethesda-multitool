using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Dialogue;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Item;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.Quest;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Minidump;
using FalloutXbox360Utils.Core.Utils;
using static FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Layouts.RuntimeDialogueLayouts;

namespace FalloutXbox360Utils.Core.Formats.Esm.Runtime.Readers.Specialized;

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

    // RuntimeTerminalLayoutProbe was deleted in Phase 1B.6. Across 32 sampled dumps the
    // data-shift result was never high-confidence (so the consumer always took the 0
    // fallback) and the menu-list shift was uniformly +4. The +4 is now baked into
    // TermMenuItemListOffset below; the data-shift fallback (0) is the identity.
    //
    // The TermDifficulty/TermFlags fields are known to be reading from PDB +132 instead
    // of +180 (PDB's BGSTerminal.Difficulty location) — that is a separate latent
    // investigation item documented in the plan ("Package + QuestTerminal cleanup"),
    // not introduced by removing the probe.

    /// <summary>
    ///     Read extended quest data from a runtime TESQuest struct.
    ///     Returns a QuestRecord with flags, delay, script, stages, and objectives, or null if validation fails.
    ///     Runtime stage traversal is conservative: it projects stage index and flags when a valid
    ///     TESQuestStageItem is available, but does not guess stage log text. PDB evidence only shows
    ///     TESQuestStageItem.GetLogEntry(TESForm*) plus m_fileOffset/m_bHasLogEntry; there is no proven
    ///     inline runtime text field to project directly from dump memory. Save/load decompilation also
    ///     indicates quest stage persistence keeps indices, flags, and note/reference metadata rather
    ///     than serializing the display text itself.
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
        var questDelay = BinaryUtils.ReadFloatBE(buffer, QustDelayOffset);
        if (!RuntimeMemoryContext.IsNormalFloat(questDelay))
        {
            questDelay = 0;
        }

        // Try to read quest display name from BSStringT, with fallback to hash table DisplayName
        var fullName = entry.DisplayName ?? _context.ReadBsStringT(offset, QustFullNameOffset);

        // Follow pFormScript pointer → Script* → get Script FormID (0x11 = SCPT).
        // Brute-force fallback when the canonical slot is null: scan the whole TESQuest
        // struct for any 4-byte pointer that resolves to a Script form. Proto builds may
        // attach scripts via a different field path (e.g. an aggregated form-list pointer
        // we haven't reverse-engineered), and a missing SCRI on the quest cascades into
        // empty CTDA / AddTopic / dialogue-tree state. Same rationale as the NPC fallback
        // in RuntimeActorReader.BruteForceScanForScriptPointer.
        var scriptFormId = _context.FollowPointerToFormId(buffer, QustScriptOffset, 0x11);
        if (scriptFormId is null or 0)
        {
            for (var probe = 4; probe + 4 <= buffer.Length; probe += 4)
            {
                if (probe == QustScriptOffset) continue;
                var candidate = _context.FollowPointerToFormId(buffer, probe, 0x11);
                if (candidate is > 0)
                {
                    scriptFormId = candidate;
                    break;
                }
            }
        }

        var stages = WalkQuestStageList(offset, entry.FormId);
        var objectives = WalkQuestObjectiveList(offset, entry.FormId);

        return new QuestRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            Flags = flags,
            Priority = priority,
            QuestDelay = questDelay,
            Script = scriptFormId,
            Stages = stages,
            Objectives = objectives,
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

        // Read difficulty and flags from TERMINAL_DATA at PDB +180 (byte 0 = Difficulty,
        // byte 1 = Flags). Per Phase 1B.6 follow-up, the previous offsets (+132/+133)
        // were reading the high bytes of pSoundLoop and always produced 0x65/0x03 in
        // sampled dumps — clamped to 0 by the > 4 check, so every terminal appeared as
        // "Very Easy". 0xFF here is heap-fill (uninitialized DATA), which the clamp also
        // turns into 0; preserving that for back-compat.
        var difficulty = buffer[TermDifficultyOffset];
        var flags = buffer[TermFlagsOffset];

        // Validate difficulty (0-4 range)
        if (difficulty > 4)
        {
            difficulty = 0; // Default to very easy if invalid
        }

        // Password reading deferred: pPassword at PDB +176 is a TESPassword* pointer,
        // not the inline BSStringT the previous code assumed (which is why we used to
        // read at a totally different — and also wrong — offset). Proper recovery
        // requires following the pointer to a TESPassword struct and reading its inner
        // BSStringT, which has no PDB layout entry yet. Leave as null for now; tracked
        // in the plan file as a Tier-3 follow-up.
        string? password = null;

        // Parse menu items from BSSimpleList at the PDB-aligned offset.
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

        // BGSNote has TESModel at +64, TESFullName at +88 (reversed vs MISC/KEYM).
        // TESTexture.TextureName at +104 holds the icon path that maps to the ESM
        // ICON subrecord on disk. Text content (TNAM/DESC) is not exposed as a
        // BGSNote struct field — it lives only in the original ESM bytes.
        var fullName = entry.DisplayName ?? _context.ReadBsStringT(offset, NoteFullNameOffset);
        var modelPath = _context.ReadBsStringT(offset, NoteModelPathOffset);
        var iconPath = _context.ReadBsStringT(offset, NoteIconPathOffset);

        return new NoteRecord
        {
            FormId = entry.FormId,
            EditorId = entry.EditorId,
            FullName = fullName,
            ModelPath = modelPath,
            IconPath = iconPath,
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
        var text = _context.ReadBsStringT(fileOffset.Value, MenuItemResponseTextOffset);

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
    ///     Walk the BSSimpleList of TESQuestStage pointers on TESQuest.
    ///     Runtime stage projection keeps only stage index and flags; log text remains null until a
    ///     directly provable runtime source is mapped.
    /// </summary>
    private List<QuestStage> WalkQuestStageList(long questOffset, uint questFormId)
    {
        var results = new List<QuestStage>();

        var listOffset = questOffset + QustStageListOffset;
        var listBuf = _context.ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        ReadQuestStage(BinaryUtils.ReadUInt32BE(listBuf), questFormId, results);

        var nextVA = BinaryUtils.ReadUInt32BE(listBuf, 4);
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

            ReadQuestStage(BinaryUtils.ReadUInt32BE(nodeBuf), questFormId, results);
            nextVA = BinaryUtils.ReadUInt32BE(nodeBuf, 4);
        }

        return results
            .GroupBy(stage => stage.Index)
            .Select(group => group
                .OrderByDescending(stage => stage.Flags != 0 ? 1 : 0)
                .First())
            .OrderBy(stage => stage.Index)
            .ToList();
    }

    private void ReadQuestStage(uint stageVa, uint questFormId, List<QuestStage> results)
    {
        var stage = ReadQuestStage(stageVa, questFormId);
        if (stage != null)
        {
            results.Add(stage);
        }
    }

    private QuestStage? ReadQuestStage(uint stageVa, uint questFormId)
    {
        if (stageVa == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(stageVa);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, QuestStageStructSize);
        if (buf == null)
        {
            return null;
        }

        var index = buf[QuestStageIndexOffset];
        var flags = WalkQuestStageItemList(fileOffset.Value, questFormId);

        return new QuestStage
        {
            Index = index,
            Flags = flags ?? 0
        };
    }

    /// <summary>
    ///     Walk the BSSimpleList of TESQuestStageItem pointers on TESQuestStage and return the
    ///     first valid stage-item flags byte. Validation is conservative: if the owner quest is
    ///     readable and does not match, the stage item is rejected.
    /// </summary>
    private byte? WalkQuestStageItemList(long stageOffset, uint questFormId)
    {
        var listBuf = _context.ReadBytes(stageOffset + QuestStageItemListOffset, 8);
        if (listBuf == null)
        {
            return null;
        }

        var firstFlags = ReadQuestStageItemFlags(BinaryUtils.ReadUInt32BE(listBuf), questFormId);
        if (firstFlags.HasValue)
        {
            return firstFlags.Value;
        }

        var nextVA = BinaryUtils.ReadUInt32BE(listBuf, 4);
        var visited = new HashSet<uint>();
        while (nextVA != 0 && visited.Count < RuntimeMemoryContext.MaxListItems && !visited.Contains(nextVA))
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

            var flags = ReadQuestStageItemFlags(BinaryUtils.ReadUInt32BE(nodeBuf), questFormId);
            if (flags.HasValue)
            {
                return flags.Value;
            }

            nextVA = BinaryUtils.ReadUInt32BE(nodeBuf, 4);
        }

        return null;
    }

    private byte? ReadQuestStageItemFlags(uint stageItemVa, uint questFormId)
    {
        if (stageItemVa == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(stageItemVa);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, QuestStageItemStructSize);
        if (buf == null)
        {
            return null;
        }

        var ownerQuestFormId =
            _context.FollowPointerVaToFormId(BinaryUtils.ReadUInt32BE(buf, QuestStageItemOwnerQuestPtrOffset));
        if (ownerQuestFormId.HasValue && ownerQuestFormId.Value != questFormId)
        {
            return null;
        }

        return buf[QuestStageItemFlagsOffset];
    }

    /// <summary>
    ///     Walk the BSSimpleList of BGSQuestObjective pointers on TESQuest.
    ///     Each objective stores index, display text, owner quest, and runtime state.
    ///     Only index/text are projected into the semantic model for now.
    /// </summary>
    private List<QuestObjective> WalkQuestObjectiveList(long questOffset, uint questFormId)
    {
        var results = new List<QuestObjective>();

        var listOffset = questOffset + QustObjectiveListOffset;
        var listBuf = _context.ReadBytes(listOffset, 8);
        if (listBuf == null)
        {
            return results;
        }

        ReadQuestObjective(BinaryUtils.ReadUInt32BE(listBuf), questFormId, results);

        var nextVA = BinaryUtils.ReadUInt32BE(listBuf, 4);
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

            ReadQuestObjective(BinaryUtils.ReadUInt32BE(nodeBuf), questFormId, results);
            nextVA = BinaryUtils.ReadUInt32BE(nodeBuf, 4);
        }

        return results
            .GroupBy(objective => objective.Index)
            .Select(group => group
                .OrderByDescending(objective => objective.DisplayText?.Length ?? 0)
                .First())
            .OrderBy(objective => objective.Index)
            .ToList();
    }

    private void ReadQuestObjective(uint objectiveVa, uint questFormId, List<QuestObjective> results)
    {
        var objective = ReadQuestObjective(objectiveVa, questFormId);
        if (objective != null)
        {
            results.Add(objective);
        }
    }

    private QuestObjective? ReadQuestObjective(uint objectiveVa, uint questFormId)
    {
        if (objectiveVa == 0)
        {
            return null;
        }

        var fileOffset = _context.VaToFileOffset(objectiveVa);
        if (fileOffset == null)
        {
            return null;
        }

        var buf = _context.ReadBytes(fileOffset.Value, QuestObjectiveStructSize);
        if (buf == null)
        {
            return null;
        }

        var index = unchecked((int)BinaryUtils.ReadUInt32BE(buf, QuestObjectiveIndexOffset));
        if (index < 0 || index > 4096)
        {
            return null;
        }

        var ownerQuestFormId =
            _context.FollowPointerVaToFormId(BinaryUtils.ReadUInt32BE(buf, QuestObjectiveOwnerQuestPtrOffset));
        if (ownerQuestFormId.HasValue && ownerQuestFormId.Value != questFormId)
        {
            return null;
        }

        var initialized = buf[QuestObjectiveInitializedOffset] != 0;
        var displayText = _context.ReadBsStringT(fileOffset.Value, QuestObjectiveDisplayTextOffset);
        var state = BinaryUtils.ReadUInt32BE(buf, QuestObjectiveStateOffset);
        if (!initialized && string.IsNullOrEmpty(displayText))
        {
            return null;
        }

        // Objective state is retained only for sanity filtering for now; the public semantic model
        // still only carries the ESM-parity fields (index/text/target stage).
        if (state > 8)
        {
            return null;
        }

        return new QuestObjective
        {
            Index = index,
            DisplayText = displayText
        };
    }

    #region Quest Struct Layout (Proto Debug PDB base + _s)

    // TESQuest: PDB size 108, Debug dump 112, Release dump 124
    private int QustStructSize => 108 + _s;
    private int QustScriptOffset => 28 + _s; // pFormScript (Script*) at MemDebug PDB offset 44
    private int QustFullNameOffset => 52 + _s;
    private int QustFlagsOffset => 60 + _s;
    private int QustPriorityOffset => 61 + _s;
    private int QustDelayOffset => 64 + _s;
    private int QustStageListOffset => 68 + _s;
    private int QustObjectiveListOffset => 76 + _s;

    private const int QuestStageStructSize = 12;
    private const int QuestStageIndexOffset = 0;
    private const int QuestStageItemListOffset = 4;

    private const int QuestStageItemStructSize = 132;
    private const int QuestStageItemFlagsOffset = 0;
    private const int QuestStageItemOwnerQuestPtrOffset = 124;

    private const int QuestObjectiveStructSize = 36;
    private const int QuestObjectiveIndexOffset = 4;
    private const int QuestObjectiveDisplayTextOffset = 8;
    private const int QuestObjectiveOwnerQuestPtrOffset = 16;
    private const int QuestObjectiveInitializedOffset = 28;
    private const int QuestObjectiveStateOffset = 32;

    #endregion

    #region Note Struct Layout (Proto Debug PDB base + _s)

    // BGSNote: PDB size 128, Debug dump 132, Release dump 144
    private int NoteStructSize => 128 + _s;
    private int NoteTypeOffset => 124 + _s;
    private int NoteModelPathOffset => 52 + _s;
    private int NoteFullNameOffset => 76 + _s;

    // TESTexture.TextureName (BSStringT) inside BGSNote at PDB +104 — the
    // texture/icon path slot that mirrors the ESM ICON subrecord on disk.
    private int NoteIconPathOffset => 88 + _s;

    #endregion

    #region Terminal Struct Layout (Proto Debug PDB base + _s)

    // BGSTerminal: PDB structSize 184. All constants below are `pdb_offset - _s` so
    // adding the build shift yields the PDB-reported runtime offset. The previous
    // constants (116/117/120/140) were stale by 48 / 48 / 40 / 12 bytes respectively
    // and produced garbage values — see PackageTerminalOffsetInvestigationTests for the
    // ground-truth evidence (16-of-16 sampled TERMs showed 0x65/0x03 at the old
    // Difficulty/Flags offsets, vs 0x00 or 0xFF at the PDB +180/+181 location).
    private int TermStructSize => 168 + _s;          // PDB structSize 184
    private int TermDifficultyOffset => 164 + _s;    // TERMINAL_DATA byte 0 (PDB +180)
    private int TermFlagsOffset => 165 + _s;         // TERMINAL_DATA byte 1 (PDB +181)
    private int TermMenuItemListOffset => 152 + _s;  // BSSimpleList<TERMINAL_MENU_ITEM*> head (PDB +168)

    #endregion
}
