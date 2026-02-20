namespace FalloutAudioTranscriber.Models;

/// <summary>
///     A single transcription for a voice file, keyed by FormID hex string.
/// </summary>
public class TranscriptionEntry
{
    /// <summary>The transcribed text.</summary>
    public string Text { get; set; } = "";

    /// <summary>How the transcription was produced: "whisper", "manual", or "esm".</summary>
    public string Source { get; set; } = "";

    /// <summary>Voice type folder name at time of transcription.</summary>
    public string? VoiceType { get; set; }

    /// <summary>Speaker NPC name at time of transcription.</summary>
    public string? SpeakerName { get; set; }

    /// <summary>Quest name at time of transcription.</summary>
    public string? QuestName { get; set; }

    /// <summary>When this transcription was created or last edited.</summary>
    public DateTimeOffset TranscribedAt { get; set; }
}
