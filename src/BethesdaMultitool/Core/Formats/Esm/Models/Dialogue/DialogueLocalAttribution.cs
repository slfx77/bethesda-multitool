namespace BethesdaMultitool.Core.Formats.Esm.Models.Dialogue;

/// <summary>Record-local fields before topic, sibling or quest enrichment. Retained only for FO3/FNV plugins.</summary>
public sealed record DialogueLocalAttribution
{
    public string? FullName { get; init; }
    public string? DummyPrompt { get; init; }
    public uint? TopicFormId { get; init; }
    public uint? QuestFormId { get; init; }
    public uint? SpeakerFormId { get; init; }
    public uint? SpeakerFactionFormId { get; init; }
    public uint? SpeakerRaceFormId { get; init; }
    public uint? SpeakerVoiceTypeFormId { get; init; }
}
