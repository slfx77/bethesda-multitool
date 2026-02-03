namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     Result of building the full dialogue tree hierarchy from reconstructed data.
///     Organizes all dialogue into Quest → Topic → INFO chains.
/// </summary>
public record DialogueTreeResult
{
    /// <summary>Quest-level dialogue trees, keyed by quest FormID.</summary>
    public Dictionary<uint, QuestDialogueNode> QuestTrees { get; init; } = new();

    /// <summary>Topics with no identified quest parent.</summary>
    public List<TopicDialogueNode> OrphanTopics { get; init; } = [];
}