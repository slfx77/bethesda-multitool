namespace FalloutXbox360Utils.Core.VersionTracking.Models;

/// <summary>
///     Lightweight dialogue (INFO record) snapshot for version tracking.
/// </summary>
public record TrackedDialogue
{
    public uint FormId { get; init; }
    public string? EditorId { get; init; }
    public uint? TopicFormId { get; init; }
    public uint? QuestFormId { get; init; }
    public uint? SpeakerFormId { get; init; }
    public List<string> ResponseTexts { get; init; } = [];
    public byte InfoFlags { get; init; }
    public string? PromptText { get; init; }
}
