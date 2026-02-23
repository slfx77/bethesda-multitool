namespace FalloutXbox360Utils.Core.Formats.Esm;

/// <summary>
///     Shared struct layout definitions for dialogue, quest, terminal, and note runtime readers.
///     Contains offset constants and record types describing Xbox 360 runtime struct layouts.
/// </summary>
internal static class RuntimeDialogueLayouts
{
    /// <summary>
    ///     TESTopicInfo struct layout offsets.
    ///     TESTopicInfo inherits directly from TESForm (not TESBoundObject), so fields after
    ///     TESForm base (+24) are shifted by only +4 in the dump, not +16.
    ///     PDB size = 80 bytes, dump size = 84 bytes (+4 shift extends last field to +84).
    /// </summary>
    internal sealed record InfoOffsets(
        int StructSize,
        int IndexOffset,
        int DataOffset,
        int PromptOffset,
        int SpeakerPtrOffset,
        int DifficultyOffset,
        int QuestPtrOffset);

    // TESTopicInfo: Proto Debug PDB = 80 bytes, dump = 84 bytes (PDB + 4 shift after TESForm base).
    // PDB offsets → dump offsets: 32→36, 35→39, 40→44, 60→64, 68→72, 72→76.
    internal static readonly InfoOffsets InfoLayout = new(84, 36, 39, 44, 64, 72, 76);

    // TESForm field present in Release builds (not in Proto Debug PDB):
    // cFormEditorID BSStringT at offset 16 (same in both PDB and runtime — within TESForm base).
    internal const int FormEditorIdOffset = 16;

    // Additional TESTopicInfo offsets (adjusted = PDB + 4 shift):
    // bSaidOnce at PDB+34 → dump +38, m_listAddTopics at PDB+48 → dump +52.
    internal const int InfoSaidOnceOffset = 38;
    internal const int InfoAddTopicsOffset = 52;

    // TESTopic layout — consistent across all known builds (Final Debug / Release PDB, 80 bytes).
    // FullName=+44, DataType=+52, Flags=+53, Priority=+56, QuestInfoList=+60, DummyPrompt=+68.
    internal const int DialStructSize = 80;
    internal const int DialFullNameOffset = 44;
    internal const int DialDataTypeOffset = 52;
    internal const int DialDataFlagsOffset = 53;
    internal const int DialPriorityOffset = 56;
    internal const int DialQuestInfoListOffset = 60;
    internal const int DialDummyPromptOffset = 68;

    // TERMINAL_MENU_ITEM offsets — fixed within the menu item struct, not TESForm-derived
    internal const int MenuItemSize = 120;
    internal const int MenuItemResponseTextOffset = 0;
    internal const int MenuItemResultScriptOffset = 16;
    internal const int MenuItemSubMenuOffset = 112;
}
