using BethesdaMultitool.Core.Formats.Bsa.Index;

namespace BethesdaAudioTranscriber.Models;

/// <summary>
///     Result of loading a build directory.
///     Separates XAML-bindable VoiceFileEntry from the archive-internal entry record.
/// </summary>
public class BuildLoadResult
{
    /// <summary>All parsed voice file entries (XAML-safe, no archive record references).</summary>
    public List<VoiceFileEntry> Entries { get; init; } = [];

    /// <summary>
    ///     Lookup from extraction key (BsaFilePath|BsaPath) to the backing archive entry (BSA or BA2).
    ///     Used by AudioPlaybackService for on-demand extraction.
    /// </summary>
    public Dictionary<string, ArchiveReader.ArchiveEntry> FileRecords { get; init; } = new();

    // ESM enrichment heuristics

    /// <summary>"auto-discovered", "user-provided", or null if no ESM.</summary>
    public string? EsmSourceDescription { get; set; }

    public int EsmInfoCount { get; set; }
    public int EsmNpcCount { get; set; }
    public int EsmQuestCount { get; set; }
    public int EsmTopicCount { get; set; }
    public int EnrichedSubtitleCount { get; set; }
    public int EnrichedSpeakerCount { get; set; }
    public int EnrichedQuestCount { get; set; }
}
