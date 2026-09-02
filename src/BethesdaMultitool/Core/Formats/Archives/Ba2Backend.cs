using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using ArchiveEntry = BethesdaMultitool.Core.Formats.Bsa.Index.ArchiveReader.ArchiveEntry;

namespace BethesdaMultitool.Core.Formats.Archives;

/// <summary>
///     Bethesda Archive 2 (<c>.ba2</c>, FO4/FO76/Starfield) behind the backend seam — an adapter
///     over <see cref="Ba2Extractor" />. BA2 is a flat list, so the folder histogram comes from the
///     interface default's path derivation, matching the historical <c>ArchiveReader</c> behavior.
/// </summary>
internal sealed class Ba2Backend : IArchiveBackend
{
    public Ba2Backend(Ba2Extractor extractor)
    {
        Extractor = extractor;
    }

    /// <summary>The backing extractor, for callers that need record-typed extraction.</summary>
    public Ba2Extractor Extractor { get; }

    public string FormatName => "BA2";

    public string PlatformLabel => "PC";

    public int TotalFiles => Extractor.Archive.TotalFiles;

    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        return Extractor.Archive.Files.Select(ToEntry).ToList();
    }

    public byte[] Extract(ArchiveEntry entry)
    {
        return entry.Record is Ba2FileRecord record
            ? Extractor.ExtractFile(record)
            : throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
    }

    public Task<bool> ExtractToDiskAsync(ArchiveEntry entry, string outputDir, bool overwrite)
    {
        return entry.Record is Ba2FileRecord record
            ? Extractor.ExtractFileToDiskAsync(record, outputDir, overwrite)
            : throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
    }

    public Dictionary<string, int> GetExtensionStats()
    {
        return Extractor.GetExtensionStats();
    }

    public void Dispose()
    {
        Extractor.Dispose();
    }

    private static ArchiveEntry ToEntry(Ba2FileRecord f)
    {
        var fullPath = f.FullPath;
        return new ArchiveEntry(
            fullPath,
            DirectoryOf(fullPath),
            NameOf(fullPath),
            ExtensionOf(fullPath, f.Extension),
            f.RealSize,
            (long)f.Offset,
            f.Compressed,
            f);
    }

    private static string ExtensionOf(string fullPath, string? fallbackExtension)
    {
        var ext = Path.GetExtension(fullPath);
        if (!string.IsNullOrEmpty(ext))
        {
            return ext.ToLowerInvariant();
        }

        return string.IsNullOrEmpty(fallbackExtension) ? string.Empty : "." + fallbackExtension.ToLowerInvariant();
    }

    private static string DirectoryOf(string fullPath)
    {
        var idx = fullPath.LastIndexOfAny(['\\', '/']);
        return idx >= 0 ? fullPath[..idx] : string.Empty;
    }

    private static string NameOf(string fullPath)
    {
        var idx = fullPath.LastIndexOfAny(['\\', '/']);
        return idx >= 0 ? fullPath[(idx + 1)..] : fullPath;
    }
}
