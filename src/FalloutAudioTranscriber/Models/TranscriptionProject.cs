namespace FalloutAudioTranscriber.Models;

/// <summary>
///     Root model for a .fnvtranscript.json file. Contains all transcriptions
///     for a build directory, keyed by FormID hex string (e.g., "00123456").
/// </summary>
public class TranscriptionProject
{
    /// <summary>Game identifier.</summary>
    public string GameName { get; set; } = "FalloutNV";

    /// <summary>Absolute path to the Data directory this project was created from.</summary>
    public string DataDirectory { get; set; } = "";

    /// <summary>When this project was first created.</summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When this project was last modified.</summary>
    public DateTimeOffset ModifiedAt { get; set; }

    /// <summary>Transcription entries keyed by FormID hex string (uppercase, 8 chars).</summary>
    public Dictionary<string, TranscriptionEntry> Entries { get; set; } = new();
}
