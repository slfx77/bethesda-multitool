namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     An INFO record found via pointer following from a TESTopic's QUEST_INFO.infoArray.
///     Contains the FormID and the virtual address of the TESTopicInfo struct.
/// </summary>
public record InfoPointerEntry(uint FormId, uint VirtualAddress);