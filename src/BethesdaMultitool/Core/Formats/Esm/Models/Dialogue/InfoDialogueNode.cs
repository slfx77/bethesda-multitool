using BethesdaMultitool.Core.Formats.Esm.Models.Records.Quest;

namespace BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;

/// <summary>
///     An individual INFO response that may link to other topics.
/// </summary>
public record InfoDialogueNode
{
    /// <summary>The parsed dialogue (INFO) record.</summary>
    public DialogueRecord Info { get; init; } = null!;

    /// <summary>Topics presented as immediate player choices (from TCLT subrecords).</summary>
    public List<TopicDialogueNode> ChoiceTopics { get; init; } = [];

    /// <summary>Topics added to NPC's general menu for future conversations (from NAME/AddTopics subrecords).</summary>
    public List<TopicDialogueNode> AddedTopics { get; init; } = [];

    /// <summary>NPC follow-up INFO responses selected by the runtime conversation system.</summary>
    public List<InfoDialogueNode> FollowUpInfos { get; init; } = [];
}
