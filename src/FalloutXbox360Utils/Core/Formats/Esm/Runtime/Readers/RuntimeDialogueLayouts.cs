namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Shared struct layout definitions for dialogue, quest, terminal, and note runtime readers.
///     Contains offset constants and record types describing Xbox 360 runtime struct layouts.
/// </summary>
internal static class RuntimeDialogueLayouts
{
    // TESForm field present in Release builds (not in Proto Debug PDB):
    // cFormEditorID BSStringT at offset 16 (same in both PDB and runtime — within TESForm base).
    internal const int FormEditorIdOffset = 16;

    // TESTopicInfo Release Beta / Final layout:
    // bSaidOnce at +50, m_listAddTopics at +64.
    internal const int InfoSaidOnceOffset = 50;
    internal const int InfoAddTopicsOffset = 64;

    // TESTopic layout — consistent across validated dump families (Debug, Release Beta variants,
    // MemDebug). Dump size is 88 bytes: FullName=+44, DataType=+52, Flags=+53, Priority=+56,
    // QuestInfoList=+60, DummyPrompt=+68, JournalIndex=+76, TopicCount=+84.
    internal const int DialStructSize = 88;
    internal const int DialFullNameOffset = 44;
    internal const int DialDataTypeOffset = 52;
    internal const int DialDataFlagsOffset = 53;
    internal const int DialPriorityOffset = 56;
    internal const int DialQuestInfoListOffset = 60;
    internal const int DialDummyPromptOffset = 68;
    internal const int DialJournalIndexOffset = 76;
    internal const int DialTopicCountOffset = 84;

    // TERMINAL_MENU_ITEM offsets — fixed within the menu item struct, not TESForm-derived
    internal const int MenuItemSize = 120;
    internal const int MenuItemResponseTextOffset = 0;
    internal const int MenuItemResultScriptOffset = 16;
    internal const int MenuItemSubMenuOffset = 112;

    // TESTopicInfo: Release Beta / Final PDB = runtime layout directly.
    // Field offsets: iInfoIndex=48, m_Data=51, cPrompt=56, pSpeaker=76, eDifficulty=84, pOwnerQuest=88.
    internal static readonly InfoOffsets InfoLayout = new(96, 48, 51, 56, 76, 84, 88);

    /// <summary>
    ///     TESTopicInfo struct layout offsets from the Release Beta / Final PDB-backed runtime path.
    ///     TESTopicInfo inherits directly from TESForm and the validated dump families match these
    ///     offsets directly for the fields currently consumed by the runtime dialogue readers.
    /// </summary>
    internal sealed record InfoOffsets(
        int StructSize,
        int IndexOffset,
        int DataOffset,
        int PromptOffset,
        int SpeakerPtrOffset,
        int DifficultyOffset,
        int QuestPtrOffset);
}
