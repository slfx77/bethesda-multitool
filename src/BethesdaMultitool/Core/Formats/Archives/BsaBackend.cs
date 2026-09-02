using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Models;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     Classic BSA (Morrowind v0x100 and Oblivion..SkyrimSE v103-105) behind the backend seam — a
///     thin adapter over <see cref="BsaExtractor" />, which keeps the memory-mapped extraction,
///     folder tree, and the Xbox-360 conversion toggles that <see cref="MarkShared" /> locks out.
/// </summary>
internal sealed class BsaBackend : IArchiveBackend
{
    public BsaBackend(BsaExtractor extractor)
    {
        Extractor = extractor;
    }

    /// <summary>
    ///     The backing extractor — the hook for the inherently BSA/Xbox-360 conversion path
    ///     (DDX→DDS, XMA→WAV, NIF endian swap), which has no analogue in any other family.
    /// </summary>
    public BsaExtractor Extractor { get; }

    public string FormatName => "BSA";

    public string PlatformLabel => Extractor.Archive.Platform;

    public int TotalFiles => Extractor.Archive.TotalFiles;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        var defaultCompressed = Extractor.Archive.Header.DefaultCompressed;
        return Extractor.Archive.Folders
            .SelectMany(static folder => folder.Files)
            .Select(f => ToEntry(f, defaultCompressed))
            .ToList();
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        return entry.Record is BsaFileRecord record
            ? Extractor.ExtractFile(record)
            : throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
    }

    public async Task<bool> ExtractToDiskAsync(ArchiveEntry entry, string outputDir, bool overwrite)
    {
        if (entry.Record is not BsaFileRecord record)
        {
            throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }

        var result = await Extractor.ExtractFileToDiskAsync(record, outputDir, overwrite).ConfigureAwait(false);
        return result.Success;
    }

    public void MarkShared()
    {
        Extractor.MarkShared();
    }

    public Dictionary<string, int> GetExtensionStats()
    {
        return Extractor.GetExtensionStats();
    }

    public Dictionary<string, int> GetFolderStats()
    {
        return Extractor.GetFolderStats();
    }

    public void Dispose()
    {
        Extractor.Dispose();
    }

    private static ArchiveEntry ToEntry(BsaFileRecord f, bool defaultCompressed)
    {
        return new ArchiveEntry(
            f.FullPath,
            f.Folder?.Name ?? string.Empty,
            f.Name ?? f.FullPath,
            ExtensionOf(f.FullPath),
            f.Size,
            f.Offset,
            defaultCompressed != f.CompressionToggle,
            f);
    }

    private static string ExtensionOf(string fullPath)
    {
        var ext = Path.GetExtension(fullPath);
        return string.IsNullOrEmpty(ext) ? string.Empty : ext.ToLowerInvariant();
    }
}
