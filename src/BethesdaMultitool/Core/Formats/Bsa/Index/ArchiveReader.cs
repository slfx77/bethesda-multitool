using BethesdaMultitool.Core.Formats.Archives;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Formats.Bsa.Extraction;
using BethesdaMultitool.Core.Formats.Bsa.Models;

namespace BethesdaMultitool.Core.Formats.Bsa.Index;

/// <summary>
///     Unified read-only accessor over a Bethesda game archive, chosen by content probe
///     (<see cref="ArchiveProbe" />) — a classic BSA or a Bethesda Archive 2 today, with the classic
///     families (Arena/XnGine BSA, Fallout DAT, Tactics BOS) joining behind the same seam. Lets
///     callers (the CLI <c>archive</c> command group, the GUI extractor tab + NIF browser, the audio
///     transcriber, the VFS) accept any container without caring which it is. The texture side
///     already dispatches via <c>NifTextureArchiveSourceFactory</c>; this is the mesh/general-side
///     parallel. Backends are memory-mapped and thread-safe, so <see cref="Extract" /> may be called
///     concurrently.
/// </summary>
public sealed class ArchiveReader : IDisposable
{
    private readonly IArchiveBackend _backend;

    // Lazy<T> (ExecutionAndPublication) so racing first lookups build the index exactly once —
    // the old `_byPath ??= BuildIndex()` let concurrent first calls each build a full private
    // index (wasted work; last assignment won).
    private readonly Lazy<Dictionary<string, ArchiveEntry>> _byPath;

    private ArchiveReader(IArchiveBackend backend)
    {
        _backend = backend;
        _byPath = new Lazy<Dictionary<string, ArchiveEntry>>(BuildIndex);
    }

    /// <summary>The format backend — the seam <c>ArchiveHandleRegistry</c> shares handles through.</summary>
    internal IArchiveBackend Backend => _backend;

    /// <summary>True when the underlying container is a BA2 (vs a classic BSA).</summary>
    public bool IsBa2 => _backend is Ba2Backend;

    /// <summary>Short format label for display: <c>"BSA"</c> or <c>"BA2"</c> (more as families land).</summary>
    public string FormatName => _backend.FormatName;

    /// <summary>Platform label: a BSA may be Xbox 360 or PC; a BA2 is always PC.</summary>
    public string PlatformLabel => _backend.PlatformLabel;

    /// <summary>Total entry count across the container.</summary>
    public int TotalFiles => _backend.TotalFiles;

    /// <summary>The parsed BSA archive, or null when this is not a BSA (for format-specific display).</summary>
    public BsaArchive? Bsa => AsBsaExtractor?.Archive;

    /// <summary>The parsed BA2 archive, or null when this is not a BA2 (for format-specific display).</summary>
    public Ba2Archive? Ba2 => AsBa2Extractor?.Archive;

    /// <summary>
    ///     The underlying BSA extractor, non-null only for a BSA. This is the hook for the inherently
    ///     BSA/Xbox-360 conversion path (DDX→DDS, XMA→WAV, NIF endian swap), which has no analogue in
    ///     any other family because they are already PC formats.
    /// </summary>
    public BsaExtractor? AsBsaExtractor => (_backend as BsaBackend)?.Extractor;

    /// <summary>
    ///     The underlying BA2 extractor, non-null only for a BA2. Counterpart of
    ///     <see cref="AsBsaExtractor" /> for callers that need record-typed extraction
    ///     (e.g. texture sources built over a shared <see cref="ArchiveReader" /> handle).
    /// </summary>
    public Ba2Extractor? AsBa2Extractor => (_backend as Ba2Backend)?.Extractor;

    public void Dispose()
    {
        _backend.Dispose();
    }

    /// <summary>Opens <paramref name="path" /> with the format chosen by <see cref="ArchiveProbe" />.</summary>
    public static ArchiveReader Open(string path)
    {
        return new ArchiveReader(ArchiveProbe.Open(path));
    }

    /// <summary>All entries in the archive (folder trees flattened; flat containers are already lists).</summary>
    public IReadOnlyList<ArchiveEntry> ListFiles()
    {
        return _backend.ListFiles();
    }

    /// <summary>Extracts an entry returned by <see cref="ListFiles" /> to bytes. Thread-safe.</summary>
    public byte[] Extract(ArchiveEntry entry)
    {
        return _backend.Extract(entry);
    }

    /// <summary>
    ///     Extracts an entry to <paramref name="outputDir" /> under its virtual path. Returns whether
    ///     the file was written (or already present when <paramref name="overwrite" /> is false).
    /// </summary>
    public Task<bool> ExtractToDiskAsync(ArchiveEntry entry, string outputDir, bool overwrite = false)
    {
        return _backend.ExtractToDiskAsync(entry, outputDir, overwrite);
    }

    /// <summary>File-extension histogram, delegated to the backing extractor.</summary>
    public Dictionary<string, int> GetExtensionStats()
    {
        return _backend.GetExtensionStats();
    }

    /// <summary>
    ///     Files-per-folder histogram. Folder-tree formats report their real tree; flat containers
    ///     derive it from entry paths so every format presents the same grouping.
    /// </summary>
    public Dictionary<string, int> GetFolderStats()
    {
        return _backend.GetFolderStats();
    }

    /// <summary>
    ///     Reads a file by its full virtual path (case-insensitive, accepts <c>/</c> or <c>\</c>), or
    ///     null when absent. Builds a path index on first use for O(1) lookups. Thread-safe.
    /// </summary>
    public byte[]? ReadFile(string fullPath)
    {
        return FindEntry(fullPath) is { } entry ? Extract(entry) : null;
    }

    /// <summary>
    ///     Looks up an entry by full virtual path without extracting it (case-insensitive, accepts
    ///     <c>/</c> or <c>\</c>), or null when absent. Thread-safe.
    /// </summary>
    public ArchiveEntry? FindEntry(string fullPath)
    {
        return _byPath.Value.TryGetValue(Normalize(fullPath), out var entry) ? entry : null;
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

    private static string Normalize(string path)
    {
        return path.Replace('/', '\\');
    }

    /// <summary>
    ///     One archive entry. Carries the resolved, format-neutral metadata every consumer needs
    ///     (path, size, offset, compressed state) plus the backing format record used to extract it.
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
