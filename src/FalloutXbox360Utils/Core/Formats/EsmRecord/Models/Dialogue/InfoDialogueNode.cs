namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     An individual INFO response that may link to other topics.
/// </summary>
public record InfoDialogueNode
{
    /// <summary>The reconstructed dialogue (INFO) record.</summary>
    public ReconstructedDialogue Info { get; init; } = null!;

    /// <summary>Topics that this response links to (from TCLT/AddTopics).</summary>
    public List<TopicDialogueNode> LinkedTopics { get; init; } = [];
}
