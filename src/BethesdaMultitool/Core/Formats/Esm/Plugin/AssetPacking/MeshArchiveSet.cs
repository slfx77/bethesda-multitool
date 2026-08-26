using System.Collections.Concurrent;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;
using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.Formats.Esm.Plugin.AssetPacking;

/// <summary>
///     Ordered mesh-asset lookup across one primary meshes archive and optional fallback archives.
///     The first archive containing a requested virtual path wins. Each archive may be a BSA
///     (Morrowind→Skyrim/FNV) or a BA2 (Fallout 4 / Fallout 76) — dispatched by magic — so mesh
///     resolution is format-agnostic for the consumers (3D-viewer reference pipeline, NPC pipelines,
///     FaceGen verification tools).
///     A thin facade over the same <see cref="DataFolderIndex" /> + <see cref="DataFolderResolver" />
///     the DMP→ESM conversion uses, so there is a single asset-resolution implementation. It owns the
///     consumer-specific pieces that don't belong in the generic resolver: construction from an
///     explicit ordered archive-path list, the decoded-mesh disk-cache identity/metadata
///     (<see cref="ArchiveSetIdentity" /> / <see cref="GetLookupMetadata" />), and the
///     empty-baseline-as-sole-secondary wiring (the baseline-exact strategy yields a null
///     <c>Source</c>, so routing through the secondary guarantees every hit carries readable bytes).
///     By default resolution is EXACT-only (no fuzzy, no loose files) — matching the historical
///     behavior every consumer relies on. The 3D viewer opts into the fuzzy renamed-asset fallback
///     (and loose-file overrides) when browsing a memory dump, where prototype mesh paths were
///     renamed before the shipped archives (e.g.
///     <c>architecture\McCarran\NV_McCarran-Wallreg02.nif</c> →
///     <c>architecture\mccarran\mcmarranwallsdes\wallreg.nif</c>).
/// </summary>
internal sealed class MeshArchiveSet : IDisposable
{
    // Full archive path → (length, last-write-utc-ticks), for the decoded-mesh disk-cache key.
    private readonly Dictionary<string, (long Length, long Ticks)> _archiveStats;
    private readonly DataFolderIndex _emptyBaseline;
    private readonly DataFolderIndex _index;

    // Per-path resolution memo. DataFolderResolver.Resolve is read-only over the immutable index
    // (lock-free), so concurrent decode tasks can resolve in parallel; the memo just avoids
    // recomputing the (occasionally fuzzy) lookup for paths requested more than once.
    private readonly ConcurrentDictionary<string, DataFolderResolution> _resolveCache =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DataFolderResolver _resolver;

    // Persisted DMP rename map (MeshRenameMapService sidecar): normalized request → resolved donor
    // path. Consulted ahead of the live fuzzy fallback so a resolution pass the user ran once (the
    // same pass DMP→ESM conversion applies) wins over per-load heuristics.
    private readonly IReadOnlyDictionary<string, string>? _pathRenames;

    private MeshArchiveSet(
        IReadOnlyList<string> archivePaths, bool enableFuzzy, bool includeLooseFiles,
        IReadOnlyDictionary<string, string>? pathRenames = null)
    {
        _pathRenames = pathRenames is { Count: > 0 } ? pathRenames : null;
        ArchivePaths = archivePaths;
        _emptyBaseline = DataFolderIndex.FromArchivePaths([]);
        // Shared handles: the 3D viewer, NPC browser, and headless profiler open overlapping
        // archive sets in one process — the registry dedups the parse + memory map per archive.
        // ArchiveSetIdentity/GetLookupMetadata keep their own FileInfo stats (cache keys unchanged).
        _index = DataFolderIndex.FromArchivePaths(
            archivePaths, includeLooseFiles, ArchiveHandleRegistry.Shared);
        // Empty baseline + the real index as the SOLE secondary: the baseline-exact strategy returns
        // AlreadyInBaseline with a null Source (unusable for extraction), so routing everything through
        // the secondary guarantees every hit carries a readable Source (and a ResolvedPath when fuzzy).
        _resolver = new DataFolderResolver(_emptyBaseline, [_index], false, enableFuzzy);

        _archiveStats = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        var idParts = new List<string>(archivePaths.Count);
        foreach (var path in archivePaths)
        {
            var fileInfo = new FileInfo(path);
            var length = fileInfo.Exists ? fileInfo.Length : 0L;
            var ticks = fileInfo.Exists ? fileInfo.LastWriteTimeUtc.Ticks : 0L;
            _archiveStats[Path.GetFullPath(path)] = (length, ticks);
            idParts.Add($"{path}:{length}:{ticks}");
        }

        ArchiveSetIdentity = string.Join("|", idParts);
    }

    public string PrimaryPath => ArchivePaths[0];

    public IReadOnlyList<string> ArchivePaths { get; }

    internal string ArchiveSetIdentity { get; }

    public void Dispose()
    {
        _index.Dispose();
        _emptyBaseline.Dispose();
    }

    /// <summary>Exact-only resolution (no fuzzy, no loose files) — the historical default.</summary>
    public static MeshArchiveSet Open(string primaryMeshesBsaPath, string[]? extraMeshesBsaPaths)
    {
        return Open(primaryMeshesBsaPath, extraMeshesBsaPaths, false, false);
    }

    /// <summary>
    ///     Opens the archive set with explicit resolution options. <paramref name="enableFuzzy" />
    ///     turns on the renamed-asset fuzzy fallback (for browsing memory dumps);
    ///     <paramref name="includeLooseFiles" /> additionally indexes loose files in each archive's
    ///     directory (loose overrides archive entries, mirroring the game's load order).
    /// </summary>
    public static MeshArchiveSet Open(
        string primaryMeshesBsaPath,
        string[]? extraMeshesBsaPaths,
        bool enableFuzzy,
        bool includeLooseFiles = false,
        IReadOnlyDictionary<string, string>? pathRenames = null)
    {
        var paths = new List<string> { Path.GetFullPath(primaryMeshesBsaPath) };
        if (extraMeshesBsaPaths is { Length: > 0 })
        {
            foreach (var extraPath in extraMeshesBsaPaths)
            {
                var fullPath = Path.GetFullPath(extraPath);
                if (!paths.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
                {
                    paths.Add(fullPath);
                }
            }
        }

        return new MeshArchiveSet(paths, enableFuzzy, includeLooseFiles, pathRenames);
    }

    public bool TryExtractFile(string virtualPath, out byte[] data, out string archivePath)
    {
        return TryExtractFile(virtualPath, out data, out archivePath, out _);
    }

    /// <summary>
    ///     Extracts a mesh by virtual path. <paramref name="resolvedPath" /> reports the path the
    ///     bytes actually came from — equal to the request for an exact hit, or the substituted path
    ///     when a fuzzy fallback matched a renamed asset (used to surface "Fallback Mesh" in the UI).
    /// </summary>
    public bool TryExtractFile(string virtualPath, out byte[] data, out string archivePath, out string resolvedPath)
    {
        var normalized = Normalize(virtualPath);
        var resolution = Resolve(normalized);
        if (resolution.Source is null)
        {
            data = [];
            archivePath = string.Empty;
            resolvedPath = string.Empty;
            return false;
        }

        data = resolution.Source.Read();
        archivePath = ArchivePathOf(resolution.Source);
        resolvedPath = resolution.ResolvedPath ?? normalized;
        return true;
    }

    /// <summary>
    ///     Resolves a virtual path WITHOUT reading bytes. Returns the backing archive path and the
    ///     resolved (possibly fuzzy-substituted) virtual path. Used by the inspect panel to show the
    ///     fallback mesh a renamed prototype ref actually rendered from.
    /// </summary>
    public bool TryResolvePath(string virtualPath, out string archivePath, out string resolvedPath)
    {
        var normalized = Normalize(virtualPath);
        var resolution = Resolve(normalized);
        if (resolution.Source is null)
        {
            archivePath = string.Empty;
            resolvedPath = string.Empty;
            return false;
        }

        archivePath = ArchivePathOf(resolution.Source);
        resolvedPath = resolution.ResolvedPath ?? normalized;
        return true;
    }

    internal MeshArchiveLookupMetadata GetLookupMetadata(string virtualPath)
    {
        var normalized = Normalize(virtualPath);
        var resolution = Resolve(normalized);
        if (resolution.Source is null)
        {
            return new MeshArchiveLookupMetadata(
                normalized,
                false,
                ArchiveSetIdentity,
                null,
                null,
                null,
                null,
                null,
                null,
                null);
        }

        var (archivePath, archiveLength, archiveTicks) = ArchiveStatsOf(resolution.Source);
        var meta = GetSourceMetadata(resolution.Source);
        return new MeshArchiveLookupMetadata(
            resolution.ResolvedPath ?? normalized,
            true,
            ArchiveSetIdentity,
            archivePath,
            archiveLength,
            archiveTicks,
            meta?.NameHash,
            meta?.RawSize,
            meta?.Size,
            meta?.Offset);
    }

    private DataFolderResolution Resolve(string normalizedPath)
    {
        return _resolveCache.GetOrAdd(normalizedPath, path =>
        {
            // The persisted rename map is a deliberate, user-triggered resolution (conversion
            // parity), so it outranks the live fuzzy cascade — but a mapped path that no longer
            // resolves (donor set changed since the sidecar was built) falls through to the
            // normal lookup rather than failing.
            if (_pathRenames is not null && _pathRenames.TryGetValue(path, out var mapped))
            {
                var mappedNormalized = Normalize(mapped);
                var viaMap = _resolver.Resolve(mappedNormalized);
                if (viaMap.Kind != AssetResolutionKind.Missing)
                {
                    // An exact hit on the MAPPED path reports no ResolvedPath ("you got what you
                    // asked for") — but relative to the ORIGINAL request this is a substitution,
                    // and consumers surface ResolvedPath as the "Fallback Mesh" label.
                    return viaMap.ResolvedPath is null ? viaMap with { ResolvedPath = mappedNormalized } : viaMap;
                }
            }

            return _resolver.Resolve(path);
        });
    }

    private static string Normalize(string virtualPath)
    {
        // Lowercase, matching how AssetPathRules.NormalizeDataRelativePath builds the index keys.
        // The conversion pipeline lowercases every request (TryNormalizeRequestPath) before it hits
        // DataFolderResolver, but render-side requests arrived mixed-case — and the index's
        // by-last-directory buckets are Ordinal over lowercased keys, so "SCOL"/"Furniture" lookups
        // returned empty and the substring-suffix + directory-containment fuzzy passes were dead
        // code at render time. That is why prototype renames (SCOLParkingLotChunk03 →
        // scolparkinglotchunk03b) resolved during DMP→ESM conversion but not in the viewer.
        return virtualPath.Replace('/', '\\').ToLowerInvariant();
    }

    private static string ArchivePathOf(AssetSource source)
    {
        return source switch
        {
            BsaAssetSource bsa => bsa.ArchivePath,
            Ba2AssetSource ba2 => ba2.ArchivePath,
            LooseFileAssetSource loose => loose.AbsolutePath,
            _ => string.Empty
        };
    }

    private (string ArchivePath, long? ArchiveLength, long? ArchiveTicks) ArchiveStatsOf(AssetSource source)
    {
        var archivePath = ArchivePathOf(source);
        if (source is LooseFileAssetSource loose)
        {
            // Loose meshes have no archive; key the disk cache on the file's own size + mtime so an
            // edited loose mesh re-decodes instead of serving a stale warm-cache entry.
            var fileInfo = new FileInfo(loose.AbsolutePath);
            return fileInfo.Exists
                ? (archivePath, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks)
                : (archivePath, null, null);
        }

        return _archiveStats.TryGetValue(archivePath, out var stat)
            ? (archivePath, stat.Length, stat.Ticks)
            : (archivePath, null, null);
    }

    /// <summary>
    ///     File-identity fields used (with the archive identity) as the decoded-mesh disk-cache key.
    ///     BSA records map directly; a BA2 DX10 (texture) entry keys off its first chunk; a loose
    ///     file has no record-level metadata (null — the archive-stat fields key it instead).
    /// </summary>
    private static (ulong NameHash, uint RawSize, uint Size, uint Offset)? GetSourceMetadata(AssetSource source)
    {
        return source switch
        {
            BsaAssetSource bsa => (bsa.Record.NameHash, bsa.Record.RawSize, bsa.Record.Size, bsa.Record.Offset),
            Ba2AssetSource ba2 => ba2.Record is { Kind: Ba2HeaderType.Texture, Texture: { Chunks.Count: > 0 } texture }
                ? (ba2.Record.NameHash, texture.Chunks[0].PackedSize, texture.Chunks[0].FullSize,
                    (uint)texture.Chunks[0].Offset)
                : ((ulong)ba2.Record.NameHash, ba2.Record.PackedSize, ba2.Record.RealSize, (uint)ba2.Record.Offset),
            _ => null
        };
    }
}

internal sealed record MeshArchiveLookupMetadata(
    string NormalizedPath,
    bool Found,
    string ArchiveSetIdentity,
    string? ArchivePath,
    long? ArchiveLength,
    long? ArchiveLastWriteUtcTicks,
    ulong? FileNameHash,
    uint? FileRawSize,
    uint? FileSize,
    uint? FileOffset);
