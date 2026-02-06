namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     A quest node containing all its dialogue topics.
/// </summary>
public record QuestDialogueNode
{
    /// <summary>Quest FormID.</summary>
    public uint QuestFormId { get; init; }

    /// <summary>Quest name (from EditorID or FullName lookup).</summary>
    public string? QuestName { get; init; }

    /// <summary>All dialogue topics belonging to this quest.</summary>
    public List<TopicDialogueNode> Topics { get; init; } = [];
}
