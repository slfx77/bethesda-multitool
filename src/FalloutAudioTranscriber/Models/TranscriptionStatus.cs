namespace FalloutAudioTranscriber.Models;

/// <summary>
///     The transcription state of a voice file entry.
/// </summary>
public enum TranscriptionStatus
{
    /// <summary>Has ESM subtitle text (from the game's master file).</summary>
    EsmSubtitle,

    /// <summary>No transcription at all.</summary>
    Untranscribed,

    /// <summary>Whisper processed but no speech detected (combat grunts, ambient sounds, etc.).</summary>
    NoSpeech,

    /// <summary>Whisper-transcribed but not yet reviewed/approved by user.</summary>
    Automatic,

    /// <summary>User has reviewed and approved the transcription.</summary>
    Accepted
}
