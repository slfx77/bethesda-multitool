using System.Collections.ObjectModel;
using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Subtitles;
using BethesdaMultitool.Core.Semantic;

namespace BethesdaMultitool;

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
    ///     <paramref name="primaryFileName" /> names the session's externally-merged primary plugin
    ///     (global mod index 0) so TES4-family entries' master references rebase against it.
    /// </summary>
    public RecordCollection? BuildMergedRecords(string? primaryFileName = null)
    {
        // TES4-family plugins carry file-local mod indices in their FormID high bytes (every DLC's own
        // records are raw 0x01xxxxxx); rebase them into shared load-order slots so unrelated records
        // from different plugins stop colliding on merge (see Tes4LoadOrderFormIdMapper).
        var tes4Mapper = Tes4LoadOrderFormIdMapper.TryCreate(
            Entries.Select(entry => entry.FilePath).ToList(),
            primaryFileName);

        RecordCollection? merged = null;
        for (var i = 0; i < Entries.Count; i++)
        {
            var records = Entries[i].Records;
            if (records == null) continue;

            // TES3 plugins carry only file-local synthetic FormIDs; stamp each entry with a load index so
            // distinct records from different plugins don't collide on merge (no-op for other games). The
            // Load Order is supplementary to the externally-loaded primary file, which holds the unstamped
            // 0x00 range, so entries start at index 1 to stay clear of it (see Tes3LoadOrderNamespacer).
            records = Tes3LoadOrderNamespacer.Namespaced(records, i + 1);
            if (tes4Mapper is not null)
            {
                records = tes4Mapper.Namespaced(records, Entries[i].FilePath);
            }

            merged = merged == null ? records : merged.MergeWith(records);
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

