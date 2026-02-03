namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Mapping from a TESTopic's QUEST_INFO entry: one quest and its linked INFO records.
///     Extracted by walking TESTopic.m_listQuestInfo → QUEST_INFO → infoArray.
/// </summary>
public record TopicQuestLink(uint QuestFormId, List<InfoPointerEntry> InfoEntries);