namespace FalloutAudioTranscriber.Models;

public enum BatchItemStatus
{
    Success,
    Empty,
    Error,
    Skipped
}

public class BatchProgressItem
{
    public string DisplayName { get; init; } = "";
    public string VoiceType { get; init; } = "";
    public string? TranscriptionPreview { get; init; }
    public BatchItemStatus ItemStatus { get; init; }
}
