using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Models;

namespace BethesdaMultitool.Core.Formats.Bsa.Index;

/// <summary>
///     Unified read-only accessor over a Bethesda mesh/general archive — a classic BSA or a Bethesda
///     Archive 2 (<c>.ba2</c>) — chosen by file magic (<see cref="Ba2Parser.IsBa2File(string)" />). Lets
///     callers (the CLI <c>archive</c> command group, the GUI extractor tab + NIF browser, the audio
///     transcriber) accept either container without caring which it is. The texture side already
///     dispatches via <c>NifTextureArchiveSourceFactory</c>; this is the mesh/general-side parallel.
///     Both backing extractors are memory-mapped and thread-safe, so <see cref="Extract" /> may be
///     called concurrently.
/// </summary>
public sealed class ArchiveReader : IDisposable
{
    private readonly BsaExtractor? _bsa;
    private readonly Ba2Extractor? _ba2;
    private Dictionary<string, ArchiveEntry>? _byPath;

    private ArchiveReader(BsaExtractor bsa) => _bsa = bsa;

    private ArchiveReader(Ba2Extractor ba2) => _ba2 = ba2;

    /// <summary>True when the underlying container is a BA2 (vs a classic BSA).</summary>
    public bool IsBa2 => _ba2 != null;

    /// <summary>Short format label for display: <c>"BSA"</c> or <c>"BA2"</c>.</summary>
    public string FormatName => IsBa2 ? "BA2" : "BSA";

    /// <summary>Platform label: a BSA may be Xbox 360 or PC; a BA2 is always PC.</summary>
    public string PlatformLabel => _bsa?.Archive.Platform ?? "PC";

    /// <summary>Total entry count across the container.</summary>
    public int TotalFiles => _ba2?.Archive.TotalFiles ?? _bsa!.Archive.TotalFiles;

    /// <summary>The parsed BSA archive, or null when this is a BA2 (for format-specific display).</summary>
    public BsaArchive? Bsa => _bsa?.Archive;

    /// <summary>The parsed BA2 archive, or null when this is a BSA (for format-specific display).</summary>
    public Ba2Archive? Ba2 => _ba2?.Archive;

    /// <summary>
    ///     The underlying BSA extractor, non-null only for a BSA. This is the hook for the inherently
    ///     BSA/Xbox-360 conversion path (DDX→DDS, XMA→WAV, NIF endian swap), which has no BA2 analogue
    ///     because BA2 is already a PC format.
    /// </summary>
    public BsaExtractor? AsBsaExtractor => _bsa;

    /// <summary>Opens <paramref name="path" /> as a BA2 when it carries the BTDX magic, else as a BSA.</summary>
    public static ArchiveReader Open(string path) =>
        Ba2Parser.IsBa2File(path)
            ? new ArchiveReader(new Ba2Extractor(path))
            : new ArchiveReader(new BsaExtractor(path));

    /// <summary>All entries in the archive (BSA folder tree flattened; BA2 is already a flat list).</summary>
    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        if (_ba2 != null)
        {
            return _ba2.Archive.Files.Select(ToEntry).ToList();
        }

        var defaultCompressed = _bsa!.Archive.Header.DefaultCompressed;
        return _bsa.Archive.Folders
            .SelectMany(folder => folder.Files)
            .Select(f => ToEntry(f, defaultCompressed))
            .ToList();
    }

    /// <summary>Extracts an entry returned by <see cref="ListFiles" /> to bytes. Thread-safe.</summary>
    public byte[] Extract(ArchiveEntry entry) => entry.Record switch
    {
        Ba2FileRecord r => _ba2!.ExtractFile(r),
        BsaFileRecord r => _bsa!.ExtractFile(r),
        _ => throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.")
    };

    /// <summary>
    ///     Extracts an entry to <paramref name="outputDir" /> under its virtual path. Returns whether
    ///     the file was written (or already present when <paramref name="overwrite" /> is false).
    /// </summary>
    public async Task<bool> ExtractToDiskAsync(ArchiveEntry entry, string outputDir, bool overwrite = false)
    {
        switch (entry.Record)
        {
            case Ba2FileRecord r:
                return await _ba2!.ExtractFileToDiskAsync(r, outputDir, overwrite).ConfigureAwait(false);
            case BsaFileRecord r:
                var result = await _bsa!.ExtractFileToDiskAsync(r, outputDir, overwrite).ConfigureAwait(false);
                return result.Success;
            default:
                throw new InvalidOperationException("ArchiveReader entry has an unrecognized record type.");
        }
    }

    /// <summary>File-extension histogram, delegated to the backing extractor.</summary>
    public Dictionary<string, int> GetExtensionStats() =>
        _ba2?.GetExtensionStats() ?? _bsa!.GetExtensionStats();

    /// <summary>
    ///     Files-per-folder histogram. BSA has a real folder tree; for a flat BA2 it is derived from the
    ///     directory portion of each entry path so both containers present the same grouping.
    /// </summary>
    public Dictionary<string, int> GetFolderStats()
    {
        if (_bsa != null)
        {
            return _bsa.GetFolderStats();
        }

        var stats = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ListFiles())
        {
            var folder = string.IsNullOrEmpty(entry.FolderPath) ? "(root)" : entry.FolderPath;
            stats.TryGetValue(folder, out var count);
            stats[folder] = count + 1;
        }

        return stats.OrderByDescending(kv => kv.Value).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    /// <summary>
    ///     Reads a file by its full virtual path (case-insensitive, accepts <c>/</c> or <c>\</c>), or
    ///     null when absent. Builds a path index on first use for O(1) lookups.
    /// </summary>
    public byte[]? ReadFile(string fullPath)
    {
        _byPath ??= BuildIndex();
        return _byPath.TryGetValue(Normalize(fullPath), out var entry) ? Extract(entry) : null;
    }

    public void Dispose()
    {
        _bsa?.Dispose();
        _ba2?.Dispose();
    }

    private static ArchiveEntry ToEntry(BsaFileRecord f, bool defaultCompressed) => new(
        f.FullPath,
        f.Folder?.Name ?? string.Empty,
        f.Name ?? f.FullPath,
        ExtensionOf(f.FullPath, null),
        f.Size,
        f.Offset,
        defaultCompressed != f.CompressionToggle,
        f);

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

    private Dictionary<string, ArchiveEntry> BuildIndex()
    {
        var map = new Dictionary<string, ArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ListFiles())
        {
            map[Normalize(entry.FullPath)] = entry;
        }

        return map;
    }

    private static string Normalize(string path) => path.Replace('/', '\\');

    /// <summary>
    ///     One archive entry. Carries the resolved, format-neutral metadata every consumer needs
    ///     (path, size, offset, compressed state) plus the backing BSA/BA2 record used to extract it.
    /// </summary>
    public sealed record ArchiveEntry(
        string FullPath,
        string FolderPath,
        string Name,
        string Extension,
        long Size,
        long Offset,
        bool Compressed,
        object Record);
}
