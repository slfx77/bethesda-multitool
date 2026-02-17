namespace FalloutXbox360Utils.Core.Formats.Esm.Models;

/// <summary>
///     An individual INFO response that may link to other topics.
/// </summary>
public record InfoDialogueNode
{
    /// <summary>The reconstructed dialogue (INFO) record.</summary>
    public DialogueRecord Info { get; init; } = null!;

    /// <summary>Topics presented as immediate player choices (from TCLT subrecords).</summary>
    public List<TopicDialogueNode> ChoiceTopics { get; init; } = [];

    /// <summary>Topics added to NPC's general menu for future conversations (from NAME/AddTopics subrecords).</summary>
    public List<TopicDialogueNode> AddedTopics { get; init; } = [];
}
