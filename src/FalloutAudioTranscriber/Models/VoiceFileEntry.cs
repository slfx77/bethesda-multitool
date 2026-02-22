namespace FalloutAudioTranscriber.Models;

/// <summary>
///     A voice file from a BSA archive, enriched with ESM metadata.
///     Note: BSA extraction details (FileRecord) are stored in AudioPlaybackService's
///     internal lookup to avoid XAML type info generation issues with required-member records.
/// </summary>
public class VoiceFileEntry
{
    /// <summary>FormID parsed from the filename (8 hex chars).</summary>
    public uint FormId { get; set; }

    /// <summary>Response index parsed from the filename.</summary>
    public int ResponseIndex { get; set; }

    /// <summary>Voice type folder name (e.g., "maleadult01default").</summary>
    public string VoiceType { get; set; } = "";

    /// <summary>Topic editor ID fragment from filename.</summary>
    public string TopicEditorId { get; set; } = "";

    /// <summary>File extension (xma, wav, lip).</summary>
    public string Extension { get; set; } = "";

    /// <summary>Full path within the BSA (e.g., "sound\voice\falloutnv.esm\maleadult01\topic_00123456_1.xma").</summary>
    public string BsaPath { get; set; } = "";

    /// <summary>Which BSA archive this file comes from.</summary>
    public string BsaFilePath { get; set; } = "";

    // ESM cross-reference data (populated after ESM index is built)

    /// <summary>NAM1 subtitle text from ESM (null if not found — untranscribed).</summary>
    public string? SubtitleText { get; set; }

    /// <summary>Original ESM subtitle text, preserved even when overridden by Whisper.</summary>
    public string? EsmSubtitleText { get; set; }

    /// <summary>Speaker NPC name or EditorID from ESM.</summary>
    public string? SpeakerName { get; set; }

    /// <summary>Quest name or EditorID from ESM.</summary>
    public string? QuestName { get; set; }

    /// <summary>How the subtitle/transcription was obtained: "esm", "whisper", "manual", or null.</summary>
    public string? TranscriptionSource { get; set; }

    /// <summary>Whether this file has subtitle text from any source (ESM, Whisper, manual).</summary>
    public bool HasSubtitle => SubtitleText != null;

    /// <summary>Computed transcription workflow status for UI display and grouping.</summary>
    public TranscriptionStatus Status => TranscriptionSource switch
    {
        "esm" => TranscriptionStatus.EsmSubtitle,
        "accepted" or "manual" => TranscriptionStatus.Accepted,
        "whisper" => TranscriptionStatus.Automatic,
        "whisper-empty" => TranscriptionStatus.NoSpeech,
        _ => TranscriptionStatus.Untranscribed
    };

    /// <summary>Human-readable status label for list display.</summary>
    public string StatusLabel => Status switch
    {
        TranscriptionStatus.EsmSubtitle => "ESM",
        TranscriptionStatus.Automatic => "Auto",
        TranscriptionStatus.NoSpeech => "No Speech",
        TranscriptionStatus.Accepted => "Accepted",
        _ => ""
    };

    /// <summary>Display name for the list (topic + FormID).</summary>
    public string DisplayName => $"{TopicEditorId} [{FormId:X8}]_{ResponseIndex}";

    /// <summary>Unique key for BSA extraction lookup (BsaFilePath|BsaPath).</summary>
    public string ExtractionKey => $"{BsaFilePath}|{BsaPath}";
}
