using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutAudioTranscriber.Models;

/// <summary>
///     Result of loading a build directory.
///     Separates XAML-bindable VoiceFileEntry from BSA-internal BsaFileRecord.
/// </summary>
public class BuildLoadResult
{
    /// <summary>All parsed voice file entries (XAML-safe, no BSA record references).</summary>
    public List<VoiceFileEntry> Entries { get; init; } = [];

    /// <summary>
    ///     Lookup from extraction key (BsaFilePath|BsaPath) to BsaFileRecord.
    ///     Used by AudioPlaybackService for on-demand extraction.
    /// </summary>
    public Dictionary<string, BsaFileRecord> FileRecords { get; init; } = new();

    // ESM enrichment heuristics
    public int EsmInfoCount { get; set; }
    public int EsmNpcCount { get; set; }
    public int EsmQuestCount { get; set; }
    public int EsmTopicCount { get; set; }
    public int EnrichedSubtitleCount { get; set; }
    public int EnrichedSpeakerCount { get; set; }
    public int EnrichedQuestCount { get; set; }
}
