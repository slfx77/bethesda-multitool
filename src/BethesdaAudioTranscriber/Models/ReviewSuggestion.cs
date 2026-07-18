namespace BethesdaAudioTranscriber.Models;

/// <summary>
///     A suspected-typo flag for one voice file, produced by an external checker
///     (tools/scripts/transcript_typo_check.py). Keyed the same way as
///     <see cref="TranscriptionEntry" />: "voicetype|FORMID_ResponseIndex".
/// </summary>
public class ReviewSuggestion
{
    /// <summary>Comma-separated check identifiers (e.g., "mismatch-vs-retail, double-space-mid").</summary>
    public string Checks { get; set; } = "";

    /// <summary>Overall confidence: "high", "medium", or "low".</summary>
    public string Confidence { get; set; } = "";

    /// <summary>Human-readable notes describing each finding (newline-separated).</summary>
    public string Detail { get; set; } = "";

    /// <summary>Proposed replacement text (retail line or mechanically cleaned transcript), if any.</summary>
    public string? SuggestedText { get; set; }

    /// <summary>The transcript text the finding was computed against (staleness detection).</summary>
    public string? FlaggedText { get; set; }

    /// <summary>Set once the user has approved, rejected, or dismissed this flag.</summary>
    public bool Resolved { get; set; }
}
