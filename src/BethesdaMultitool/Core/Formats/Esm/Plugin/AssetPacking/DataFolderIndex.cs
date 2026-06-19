using BethesdaMultitool.Core.Formats.Bsa;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

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
///     Loose files take priority over BSA entries (mirrors FNV's runtime override rules).
///     BSAs are scanned in alphabetical filename order; later entries with the same
///     normalized path are ignored.
/// </summary>
internal sealed class DataFolderIndex : IDisposable
{
    private readonly Dictionary<string, List<AssetSource>> _byBasename =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    ///     Entries keyed by last directory token (lowercase). Used by the resolver's
    ///     substring-suffix pass to cheaply gather "candidates in the same folder as the
    ///     request" without walking every indexed entry per query. Catches the common
    ///     case where a final-build mesh got a prefix added in its filename but stayed
    ///     in the same folder — e.g. <c>rubble\ibeam02.nif</c> ↔ <c>rubble\c_ibeam02.nif</c>.
    /// </summary>
    private readonly Dictionary<string, List<AssetSource>> _byLastDirectory =
        new(StringComparer.Ordinal);

    /// <summary>
    ///     Filename-without-extension reduced to lowercase letters/digits only — every
    ///     space, underscore, dash, and apostrophe is dropped. Lets the resolver catch
    ///     renames between the prototype and final FNV that only changed separator style
    ///     (e.g. <c>monorailplatform.nif</c> ↔ <c>monorail_platform.nif</c>, or
    ///     <c>Lucky38Sign.nif</c> ↔ <c>lucky_38_sign.nif</c>).
    /// </summary>
    private readonly Dictionary<string, List<AssetSource>> _byLooseBasename =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, AssetSource> _byPath = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<BsaExtractor> _ownedExtractors = [];

    /// <summary>
    ///     The path actually used for indexing — equal to <see cref="DataFolderPath" /> when
    ///     that already points at a Data folder, or an auto-detected child Data folder when
    ///     the user supplied an install root (e.g. <c>Fallout 3 goty</c> →
    ///     <c>Fallout 3 goty\Data</c>) or an Xbox 360 disc layout (e.g.
    ///     <c>Fallout New Vegas (July 21, 2010)</c> →
    ///     <c>Fallout New Vegas (July 21, 2010)\FalloutNV\Data</c>).
    /// </summary>
    private string _effectiveDataFolderPath = string.Empty;

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
    ///     How many BSA file records were skipped during indexing because their payload
    ///     lies in a zero-filled tail of a partially-recovered ("partial data") archive.
    ///     Those entries would extract to all zeros, so they're dropped to let resolution
    ///     fall through to a complete source instead of packing garbage.
    /// </summary>
    public int TruncatedEntrySkipCount { get; private set; }

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
        _byLooseBasename.Clear();
        _byLastDirectory.Clear();
        _disposed = true;
    }

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

        // Auto-detect the actual Data folder when the user passes a parent. The PC install
        // layout puts BSAs under <root>\Data\ (FO3 GotY, FNV Steam) and the Xbox 360 disc
        // layout puts them under <root>\<GameName>\Data\ (e.g. proto FalloutNV\Data). Without
        // this descent, IndexLooseFiles registers paths as "Data\meshes\..." (which won't
        // match runtime requests for "meshes\...") and IndexBsas misses every BSA entirely
        // because Directory.GetFiles top-only doesn't recurse.
        _effectiveDataFolderPath = ResolveDataPathOrSelf(DataFolderPath);

        // 1) Loose files (highest priority within this folder)
        IndexLooseFiles();

        // 2) BSAs in alphabetical order (mirrors FNV's SArchiveList convention)
        IndexBsas();
    }

    private static string ResolveDataPathOrSelf(string rootPath)
    {
        // If the user already pointed at a Data folder (BSAs at top level, or a Data\ child
        // that itself contains BSAs we'd shadow), keep it.
        if (DirectoryHasAnyBsa(rootPath))
        {
            return rootPath;
        }

        // PC install layout: <root>\Data
        var directChild = Path.Combine(rootPath, "Data");
        if (DirectoryHasAnyBsa(directChild))
        {
            return directChild;
        }

        // Xbox 360 disc layout: <root>\<GameName>\Data (e.g. FalloutNV\Data on proto discs).
        IEnumerable<string> immediateSubdirs;
        try
        {
            immediateSubdirs = Directory.EnumerateDirectories(rootPath);
        }
        catch
        {
            return rootPath;
        }

        foreach (var subdir in immediateSubdirs)
        {
            var nested = Path.Combine(subdir, "Data");
            if (DirectoryHasAnyBsa(nested))
            {
                return nested;
            }
        }

        return rootPath;
    }

    private static bool DirectoryHasAnyBsa(string path)
    {
        if (!Directory.Exists(path))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(path, "*.bsa", SearchOption.TopDirectoryOnly).Any();
        }
        catch
        {
            return false;
        }
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

    /// <summary>
    ///     Return every indexed asset whose filename, with extension stripped and
    ///     separators (space / underscore / dash / apostrophe) removed and case-folded,
    ///     matches the given <paramref name="looseBasename" />. The caller is responsible
    ///     for normalizing its key via <see cref="ComputeLooseBasename" />.
    /// </summary>
    public IReadOnlyList<AssetSource> EnumerateByLooseBasename(string looseBasename)
    {
        return _byLooseBasename.TryGetValue(looseBasename, out var list) ? list : [];
    }

    /// <summary>
    ///     Return every indexed asset whose immediate parent directory matches
    ///     <paramref name="lastDirectory" /> (case-insensitive). Used by the substring-
    ///     suffix loose pass so the search stays bounded to "same folder" candidates
    ///     instead of walking every indexed entry.
    /// </summary>
    public IReadOnlyList<AssetSource> EnumerateByLastDirectory(string lastDirectory)
    {
        return _byLastDirectory.TryGetValue(lastDirectory, out var list) ? list : [];
    }

    /// <summary>
    ///     Reduce a filename to a separator-free, case-folded comparison key. Drops the
    ///     extension and every <c>' '</c>, <c>'_'</c>, <c>'-'</c>, <c>'\''</c> character.
    ///     Returns the empty string when the input has no comparable characters left.
    /// </summary>
    public static string ComputeLooseBasename(string fileNameWithExtension)
    {
        return AssetPathRules.ComputeLooseBasename(fileNameWithExtension);
    }

    /// <summary>
    ///     Like <see cref="ComputeLooseBasename" /> but additionally strips a leading
    ///     and/or trailing <c>nv</c> token from the loose stem. The FNV final build
    ///     occasionally removed the <c>nv</c> namespace token that prototype filenames
    ///     carried (e.g. <c>nv_slotmachine</c> ↔ <c>slotmachine</c>,
    ///     <c>rockcanyonrubblepile05nv</c> ↔ <c>rockcanyonrubblepile05</c>). This variant
    ///     is queried only as a fallback after the strict loose match fails, so unrelated
    ///     <c>nv*</c> prototype assets aren't conflated with same-named final assets.
    ///     Returns <see cref="string.Empty" /> when the input has no leading/trailing
    ///     <c>nv</c> to strip (caller can skip the secondary lookup entirely in that case).
    /// </summary>
    public static string ComputeLooseBasenameWithoutNvAffix(string fileNameWithExtension)
    {
        return AssetPathRules.ComputeLooseBasenameWithoutNvAffix(fileNameWithExtension);
    }

    // ====================================================================================
    // Indexing
    // ====================================================================================

    private void IndexLooseFiles()
    {
        // Walk the entire Data folder tree once. Capture only files whose extension
        // looks like an asset; everything else is irrelevant to the packer.
        var dataRoot = _effectiveDataFolderPath;
        var rootLen = dataRoot.Length;
        if (dataRoot.EndsWith(Path.DirectorySeparatorChar) ||
            dataRoot.EndsWith(Path.AltDirectorySeparatorChar))
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
            looseFiles = Directory.EnumerateFiles(dataRoot, "*", SearchOption.AllDirectories);
        }
        catch
        {
            return;
        }

        foreach (var fullPath in looseFiles)
        {
            var ext = Path.GetExtension(fullPath);
            if (!AssetPathRules.AssetExtensions.Contains(ext))
            {
                continue;
            }

            var relativeRaw = fullPath.Length > rootLen ? fullPath[rootLen..] : fullPath;
            var normalized = AssetPathRules.NormalizeDataRelativePath(relativeRaw);
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
            bsaPaths = Directory.GetFiles(_effectiveDataFolderPath, "*.bsa", SearchOption.TopDirectoryOnly);
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

            // Partially-recovered BSAs keep an intact file table but a zero-filled payload
            // tail. Records pointing past the last real byte extract to all zeros, so skip
            // them — resolution then falls through to a complete source.
            var dataBoundary = extractor.FindDataTruncationBoundary();

            foreach (var record in extractor.Archive.AllFiles)
            {
                if (record.Name is null || record.Folder is null)
                {
                    continue;
                }

                if (record.Offset >= dataBoundary)
                {
                    TruncatedEntrySkipCount++;
                    continue;
                }

                var fullPath = record.FullPath;
                var ext = Path.GetExtension(fullPath);
                if (!AssetPathRules.AssetExtensions.Contains(ext))
                {
                    continue;
                }

                var normalized = AssetPathRules.NormalizeDataRelativePath(fullPath);
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

        // Loose-basename index — same accumulation strategy. Skip when normalization
        // leaves nothing (e.g. a filename consisting purely of separators).
        var loose = ComputeLooseBasename(basename);
        if (loose.Length == 0)
        {
            return;
        }

        if (!_byLooseBasename.TryGetValue(loose, out var looseList))
        {
            looseList = [];
            _byLooseBasename[loose] = looseList;
        }

        looseList.Add(source);

        // When the basename carries a leading/trailing 'nv' token (the FNV namespace
        // affix), ALSO index the asset under the stripped form so lookups for the
        // un-namespaced variant can find it (e.g. request `slot.nif` finds candidate
        // `nv_slot.nif` indexed under both `nvslot` and `slot`). The request side has its
        // own nv-strip pass in DataFolderResolver — together they cover both directions.
        var looseNoNv = ComputeLooseBasenameWithoutNvAffix(basename);
        if (looseNoNv.Length > 0 && !string.Equals(looseNoNv, loose, StringComparison.Ordinal))
        {
            if (!_byLooseBasename.TryGetValue(looseNoNv, out var looseNoNvList))
            {
                looseNoNvList = [];
                _byLooseBasename[looseNoNv] = looseNoNvList;
            }

            looseNoNvList.Add(source);
        }

        // Last-directory index: bucket by the immediate parent folder (lowercased).
        var lastSep = normalizedPath.LastIndexOf('\\');
        if (lastSep <= 0)
        {
            return;
        }

        var dirEnd = lastSep;
        var dirStart = normalizedPath.LastIndexOf('\\', dirEnd - 1) + 1;
        var lastDir = normalizedPath.Substring(dirStart, dirEnd - dirStart);
        if (lastDir.Length == 0)
        {
            return;
        }

        if (!_byLastDirectory.TryGetValue(lastDir, out var dirList))
        {
            dirList = [];
            _byLastDirectory[lastDir] = dirList;
        }

        dirList.Add(source);
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
        _byLooseBasename.Clear();
        _byLastDirectory.Clear();
        TruncatedEntrySkipCount = 0;
    }
}
