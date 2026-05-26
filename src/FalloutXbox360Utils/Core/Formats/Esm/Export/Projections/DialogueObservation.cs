namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Per-dialogue metadata extracted at projection time. Drives Aggregate's quest/speaker
///     group label resolution and the per-record metadata table (<c>questFormId</c>,
///     <c>topicFormId</c>, etc.). The first-prompt/first-response text is included so the
///     "search text" fallback works after the dialogue record itself is released.
/// </summary>
internal sealed record DialogueObservation
{
    public required uint FormId { get; init; }
    public uint? QuestFormId { get; init; }
    public uint? SpeakerFormId { get; init; }
    public uint? TopicFormId { get; init; }
    public string? FirstPromptText { get; init; }
    public string? FirstResponseText { get; init; }
}
