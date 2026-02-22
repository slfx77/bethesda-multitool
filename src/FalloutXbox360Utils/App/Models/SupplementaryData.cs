using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Subtitles;

namespace FalloutXbox360Utils;

/// <summary>
///     Optional supplementary data loaded alongside the primary file
///     to provide cross-reference enrichment (FormID names, subtitles, etc.).
/// </summary>
internal sealed class SupplementaryData : IDisposable
{
    /// <summary>Path to the supplementary ESM/DMP file, if loaded.</summary>
    public string? EsmFilePath { get; set; }

    /// <summary>Path to the subtitles CSV file, if loaded.</summary>
    public string? SubtitleCsvPath { get; set; }

    /// <summary>FormID resolver built from supplementary ESM/DMP records.</summary>
    public FormIdResolver? EsmResolver { get; set; }

    /// <summary>Full RecordCollection from supplementary ESM/DMP (needed for world map).</summary>
    public RecordCollection? EsmRecords { get; set; }

    /// <summary>Subtitle index built from CSV (FormID -> subtitle text, speaker, quest).</summary>
    public SubtitleIndex? Subtitles { get; set; }

    /// <summary>True if any supplementary data has been loaded.</summary>
    public bool HasData => EsmResolver != null || Subtitles != null;

    public void Dispose()
    {
        EsmRecords = null;
        EsmResolver = null;
        Subtitles = null;
        EsmFilePath = null;
        SubtitleCsvPath = null;
    }
}
