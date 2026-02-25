using System.Collections.ObjectModel;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Subtitles;

namespace FalloutXbox360Utils;

/// <summary>
///     Manages the ordered collection of supplementary load order entries.
///     Provides methods for merging resolvers and finding the best RecordCollection
///     for world map terrain.
/// </summary>
internal sealed class LoadOrder : IDisposable
{
    public ObservableCollection<LoadOrderEntry> Entries { get; } = [];

    /// <summary>Optional subtitles CSV path, loaded separately from the file-based load order.</summary>
    public string? SubtitleCsvPath { get; set; }

    /// <summary>Subtitle index loaded from CSV, if any.</summary>
    public SubtitleIndex? Subtitles { get; set; }

    public bool HasData => Entries.Count > 0 || Subtitles != null;

    /// <summary>
    ///     Builds a merged resolver from all loaded entries, folded in load order.
    ///     Later entries override earlier ones (higher index wins).
    /// </summary>
    public FormIdResolver? BuildMergedResolver()
    {
        FormIdResolver? merged = null;
        foreach (var entry in Entries)
        {
            if (entry.Resolver == null) continue;
            merged = merged == null
                ? entry.Resolver
                : entry.Resolver.MergeWith(merged);
        }

        return merged;
    }

    /// <summary>
    ///     Builds a single RecordCollection by merging all loaded entry records in load order.
    ///     Later entries override earlier ones for duplicate FormIDs.
    ///     Returns null if no entries have records.
    /// </summary>
    public RecordCollection? BuildMergedRecords()
    {
        RecordCollection? merged = null;
        foreach (var entry in Entries)
        {
            if (entry.Records == null) continue;
            merged = merged == null
                ? entry.Records
                : merged.MergeWith(entry.Records);
        }

        return merged;
    }

    /// <summary>
    ///     Returns the RecordCollection from the last loaded entry that has one.
    ///     This provides the "winning" terrain data for world map rendering,
    ///     matching game behavior where the last-loaded ESM's worldspace data wins.
    /// </summary>
    public RecordCollection? GetTerrainRecords()
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i].Records != null)
                return Entries[i].Records;
        }

        return null;
    }

    /// <summary>
    ///     Returns the file path of the entry providing terrain records.
    /// </summary>
    public string? GetTerrainFilePath()
    {
        for (var i = Entries.Count - 1; i >= 0; i--)
        {
            if (Entries[i].Records != null)
                return Entries[i].FilePath;
        }

        return null;
    }

    public void Dispose()
    {
        foreach (var entry in Entries)
            entry.Dispose();
        Entries.Clear();
        Subtitles = null;
        SubtitleCsvPath = null;
    }
}
