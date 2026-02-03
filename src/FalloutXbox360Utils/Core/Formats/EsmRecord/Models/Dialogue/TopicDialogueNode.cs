namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Models;

/// <summary>
///     A dialogue topic containing an ordered chain of INFO responses.
/// </summary>
public record TopicDialogueNode
{
    /// <summary>The reconstructed topic record (may be null for runtime-only topics).</summary>
    public ReconstructedDialogTopic? Topic { get; init; }

    /// <summary>Topic FormID (always present even if Topic record is null).</summary>
    public uint TopicFormId { get; init; }

    /// <summary>Topic display name or EditorID.</summary>
    public string? TopicName { get; init; }

    /// <summary>Ordered chain of INFO responses within this topic.</summary>
    public List<InfoDialogueNode> InfoChain { get; init; } = [];
}