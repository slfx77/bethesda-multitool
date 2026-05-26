namespace FalloutXbox360Utils.Core.Formats.Esm.Export.Projections;

/// <summary>
///     Per-DialogTopic metadata extracted at projection time. <c>FullName</c> and
///     <c>DummyPrompt</c> let Aggregate populate a dialogue's <c>topicName</c> metadata when
///     the dialogue record itself doesn't carry one. <c>SearchText</c> is the concatenated
///     prompt + response text that <c>BuildDialogTopicSearchTextLookup</c> currently produces
///     from <c>RecordCollection.Dialogues</c>.
/// </summary>
internal sealed record DialogTopicObservation
{
    public required uint FormId { get; init; }
    public string? FullName { get; init; }
    public string? DummyPrompt { get; init; }
    public string? SearchText { get; init; }
}
