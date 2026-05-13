using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutXbox360Utils.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Source of an asset's bytes — either a loose file on disk or a record inside an
///     already-opened BSA. Reading is deferred until the resolver asks for bytes, so the
///     index can be cheaply built even for very large data folders.
/// </summary>
internal abstract record AssetSource
{
    /// <summary>The full normalized path the source resolves to (relative to Data\).</summary>
    public required string NormalizedPath { get; init; }

    /// <summary>True if this source is part of an Xbox 360 BSA and needs PC conversion.</summary>
    public required bool IsXbox360 { get; init; }

    public abstract byte[] Read();
}

internal sealed record LooseFileAssetSource : AssetSource
{
    public required string AbsolutePath { get; init; }

    public override byte[] Read()
    {
        return File.ReadAllBytes(AbsolutePath);
    }
}

internal sealed record BsaAssetSource : AssetSource
{
    public required BsaExtractor Extractor { get; init; }
    public required BsaFileRecord Record { get; init; }
    public required string ArchiveFileName { get; init; }

    public override byte[] Read()
    {
        return Extractor.ExtractFile(Record);
    }
}

/// <summary>
///     Indexes one game-data folder (loose files + every <c>*.bsa</c> at the top level)
///     for fast exact-path and basename lookup. Disposable — owns the underlying memory-
///     mapped BSA extractors.
///
///     Loose files take priority over BSA entries (mirrors FNV's runtime override rules).
///     BSAs are scanned in alphabetical filename order; later entries with the same
///     normalized path are ignored.
/// </summary>
internal sealed class DataFolderIndex : IDisposable
{
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".nif", ".dds", ".ddx", ".kf", ".wav", ".lip", ".egm", ".egt",
        ".xwm", ".ogg", ".bik", ".psa", ".tri"
    };

    private readonly Dictionary<string, AssetSource> _byPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, List<AssetSource>> _byBasename =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<BsaExtractor> _ownedExtractors = [];

    private bool _disposed;

    public DataFolderIndex(string dataFolderPath, bool xbox360FormatHint)
    {
        DataFolderPath = dataFolderPath;
        Xbox360FormatHint = xbox360FormatHint;
    }

    /// <summary>Absolute path of the data folder this index was built from.</summary>
    public string DataFolderPath { get; }

    /// <summary>
    ///     User-supplied hint that this folder contains Xbox 360 BSAs. The actual flag is
    ///     read from each BSA header; the hint is used only for loose files (which carry no
    ///     header).
    /// </summary>
    public bool Xbox360FormatHint { get; }

    /// <summary>How many entries were indexed across loose files + all BSAs.</summary>
    public int EntryCount => _byPath.Count;

    /// <summary>
    ///     Walk the data folder, indexing every loose asset and every BSA's contents.
    ///     Safe to call once; subsequent calls clear and rebuild.
    /// </summary>
    public void Build()
    {
        if (!Directory.Exists(DataFolderPath))
        {
            throw new DirectoryNotFoundException($"Data folder not found: {DataFolderPath}");
        }

        Clear();

        // 1) Loose files (highest priority within this folder)
        IndexLooseFiles();

        // 2) BSAs in alphabetical order (mirrors FNV's SArchiveList convention)
        IndexBsas();
    }

    /// <summary>
    ///     Try an exact lookup of the requested normalized path.
    /// </summary>
    public bool TryResolveExact(string normalizedPath, out AssetSource source)
    {
        return _byPath.TryGetValue(normalizedPath, out source!);
    }

    /// <summary>
    ///     Return every indexed asset whose basename matches the given basename
    ///     (filename + extension, case-insensitive). Used by <c>DataFolderResolver</c>
    ///     for fuzzy fallback.
    /// </summary>
    public IReadOnlyList<AssetSource> EnumerateByBasename(string basename)
    {
        return _byBasename.TryGetValue(basename, out var list) ? list : [];
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        foreach (var extractor in _ownedExtractors)
        {
            try
            {
                extractor.Dispose();
            }
            catch
            {
                // Best-effort cleanup
            }
        }

        _ownedExtractors.Clear();
        _byPath.Clear();
        _byBasename.Clear();
        _disposed = true;
    }

    // ====================================================================================
    // Indexing
    // ====================================================================================

    private void IndexLooseFiles()
    {
        // Walk the entire Data folder tree once. Capture only files whose extension
        // looks like an asset; everything else is irrelevant to the packer.
        var rootLen = DataFolderPath.Length;
        if (DataFolderPath.EndsWith(Path.DirectorySeparatorChar) ||
            DataFolderPath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            // already trailing separator
        }
        else
        {
            rootLen++;
        }

        IEnumerable<string> looseFiles;
        try
        {
            looseFiles = Directory.EnumerateFiles(DataFolderPath, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var fullPath in looseFiles)
        {
            var ext = Path.GetExtension(fullPath);
            if (!AssetExtensions.Contains(ext))
            {
                continue;
            }

            var relativeRaw = fullPath.Length > rootLen ? fullPath[rootLen..] : fullPath;
            var normalized = AssetPathCollector.NormalizePath(relativeRaw);
            if (normalized.Length == 0)
            {
                continue;
            }

            var source = new LooseFileAssetSource
            {
                NormalizedPath = normalized,
                AbsolutePath = fullPath,
                // Loose files have no archive header — fall back to the user's folder-level hint.
                IsXbox360 = Xbox360FormatHint
            };

            AddSource(normalized, source);
        }
    }

    private void IndexBsas()
    {
        string[] bsaPaths;
        try
        {
            bsaPaths = Directory.GetFiles(DataFolderPath, "*.bsa", SearchOption.TopDirectoryOnly);
        }
        catch
        {
            return;
        }

        Array.Sort(bsaPaths, StringComparer.OrdinalIgnoreCase);

        foreach (var bsaPath in bsaPaths)
        {
            BsaExtractor? extractor = null;
            try
            {
                extractor = new BsaExtractor(bsaPath);
            }
            catch
            {
                continue; // skip unreadable BSAs
            }

            _ownedExtractors.Add(extractor);
            var isXbox360 = extractor.Archive.Header.IsXbox360;
            var archiveFileName = Path.GetFileName(bsaPath);

            foreach (var record in extractor.Archive.AllFiles)
            {
                if (record.Name is null || record.Folder is null)
                {
                    continue;
                }

                var fullPath = record.FullPath;
                var ext = Path.GetExtension(fullPath);
                if (!AssetExtensions.Contains(ext))
                {
                    continue;
                }

                var normalized = AssetPathCollector.NormalizePath(fullPath);
                if (normalized.Length == 0)
                {
                    continue;
                }

                var source = new BsaAssetSource
                {
                    NormalizedPath = normalized,
                    IsXbox360 = isXbox360,
                    Extractor = extractor,
                    Record = record,
                    ArchiveFileName = archiveFileName
                };

                AddSource(normalized, source);
            }
        }
    }

    private void AddSource(string normalizedPath, AssetSource source)
    {
        // Path-level priority: first-write wins. Loose files index first, so they beat BSAs.
        // Within BSAs, alphabetical order; first BSA wins. Mirrors FNV's load order semantics.
        _byPath.TryAdd(normalizedPath, source);

        // Basename index always accumulates: every candidate is visible to the fuzzy resolver.
        var basename = Path.GetFileName(normalizedPath);
        if (string.IsNullOrEmpty(basename))
        {
            return;
        }

        if (!_byBasename.TryGetValue(basename, out var list))
        {
            list = [];
            _byBasename[basename] = list;
        }

        list.Add(source);
    }

    private void Clear()
    {
        foreach (var extractor in _ownedExtractors)
        {
            try
            {
                extractor.Dispose();
            }
            catch
            {
                /* Best-effort cleanup */
            }
        }

        _ownedExtractors.Clear();
        _byPath.Clear();
        _byBasename.Clear();
    }
}
