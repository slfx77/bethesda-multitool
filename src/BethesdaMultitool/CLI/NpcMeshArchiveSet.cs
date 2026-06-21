using BethesdaMultitool.Core.Formats.Bsa;
using BethesdaMultitool.Core.Formats.Bsa.Ba2;

namespace BethesdaMultitool.CLI;

/// <summary>
///     Ordered mesh-asset lookup across one primary meshes archive and optional fallback archives.
///     The first archive containing a requested virtual path wins. Each archive may be a BSA
///     (Morrowind→Skyrim/FNV) or a BA2 (Fallout 4 / Fallout 76) — dispatched by magic — so mesh
///     resolution is format-agnostic for the consumers (3D-viewer reference pipeline, NPC pipelines).
/// </summary>
internal sealed class NpcMeshArchiveSet : IDisposable
{
    private readonly Dictionary<string, MeshArchiveHit?> _hitCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _hitCacheLock = new();
    private readonly List<MeshArchiveSource> _sources;

    private NpcMeshArchiveSet(List<MeshArchiveSource> sources)
    {
        _sources = sources;
        ArchivePaths = sources.Select(source => source.ArchivePath).ToArray();
        ArchiveSetIdentity = string.Join(
            "|",
            sources.Select(static source =>
                $"{source.ArchivePath}:{source.ArchiveLength}:{source.ArchiveLastWriteUtcTicks}"));
    }

    public string PrimaryPath => _sources[0].ArchivePath;

    public IReadOnlyList<string> ArchivePaths { get; }

    internal string ArchiveSetIdentity { get; }

    public void Dispose()
    {
        foreach (var source in _sources)
        {
            source.Dispose();
        }
    }

    public static NpcMeshArchiveSet Open(string primaryMeshesBsaPath, string[]? extraMeshesBsaPaths)
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

        var sources = new List<MeshArchiveSource>(paths.Count);
        foreach (var path in paths)
        {
            sources.Add(Ba2Parser.IsBa2File(path)
                ? MeshArchiveSource.OpenBa2(path)
                : MeshArchiveSource.OpenBsa(path));
        }

        return new NpcMeshArchiveSet(sources);
    }

    public bool TryExtractFile(string virtualPath, out byte[] data, out string archivePath)
    {
        var hit = ResolveHit(virtualPath);
        if (hit == null)
        {
            data = [];
            archivePath = string.Empty;
            return false;
        }

        data = hit.Source.Extract(hit.Record);
        archivePath = hit.Source.ArchivePath;
        return true;
    }

    internal NpcMeshArchiveLookupMetadata GetLookupMetadata(string virtualPath)
    {
        var normalized = virtualPath.Replace('/', '\\');
        var hit = ResolveHit(normalized);
        if (hit is null)
        {
            return new NpcMeshArchiveLookupMetadata(
                normalized,
                Found: false,
                ArchiveSetIdentity,
                ArchivePath: null,
                ArchiveLength: null,
                ArchiveLastWriteUtcTicks: null,
                FileNameHash: null,
                FileRawSize: null,
                FileSize: null,
                FileOffset: null);
        }

        var (nameHash, rawSize, size, offset) = MeshArchiveSource.GetMetadata(hit.Record);
        return new NpcMeshArchiveLookupMetadata(
            normalized,
            Found: true,
            ArchiveSetIdentity,
            hit.Source.ArchivePath,
            hit.Source.ArchiveLength,
            hit.Source.ArchiveLastWriteUtcTicks,
            nameHash,
            rawSize,
            size,
            offset);
    }

    private MeshArchiveHit? ResolveHit(string virtualPath)
    {
        var normalized = virtualPath.Replace('/', '\\');
        // Thread-safe: BsaExtractor.ExtractFile is already lock-free (memory-mapped), so the only
        // shared mutable state across concurrent decode tasks is this resolution cache. Guarding it
        // here lets ReferenceMeshCache12 drop its coarse archive lock and decode meshes in parallel.
        lock (_hitCacheLock)
        {
            if (_hitCache.TryGetValue(normalized, out var cached))
            {
                return cached;
            }

            foreach (var source in _sources)
            {
                if (source.TryResolve(normalized, out var record))
                {
                    var hit = new MeshArchiveHit(source, record);
                    _hitCache[normalized] = hit;
                    return hit;
                }
            }

            _hitCache[normalized] = null;
            return null;
        }
    }

    internal static Dictionary<string, BsaFileRecord> BuildFileIndex(BsaArchive archive)
    {
        var fileIndex = new Dictionary<string, BsaFileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in archive.AllFiles)
        {
            var path = file.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            fileIndex[path.Replace('/', '\\')] = file;
        }

        return fileIndex;
    }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Major Code Smell",
        "S3398:\"private\" methods called only by inner classes should be moved to those classes",
        Justification = "Deliberately paired with the BSA index builder (BuildFileIndex) as sibling archive " +
                        "index helpers; moving only the BA2 builder into MeshArchiveSource would split the pair.")]
    private static Dictionary<string, Ba2FileRecord> BuildBa2FileIndex(Ba2Archive archive)
    {
        var fileIndex = new Dictionary<string, Ba2FileRecord>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in archive.AllFiles)
        {
            var path = file.FullPath;
            if (string.IsNullOrEmpty(path))
            {
                continue;
            }

            fileIndex[path.Replace('/', '\\')] = file;
        }

        return fileIndex;
    }

    /// <summary>
    ///     One backing archive — either a BSA or a BA2. Holds the format-specific extractor + path
    ///     index and exposes a format-agnostic resolve/extract/metadata surface. The resolved record is
    ///     carried as <see cref="object" /> (a <see cref="BsaFileRecord" /> or <see cref="Ba2FileRecord" />,
    ///     both reference types — no boxing) so the caller's hit cache stays format-agnostic.
    /// </summary>
    private sealed class MeshArchiveSource : IDisposable
    {
        private readonly BsaExtractor? _bsaExtractor;
        private readonly Dictionary<string, BsaFileRecord>? _bsaIndex;
        private readonly Ba2Extractor? _ba2Extractor;
        private readonly Dictionary<string, Ba2FileRecord>? _ba2Index;

        private MeshArchiveSource(
            string archivePath, long archiveLength, long archiveLastWriteUtcTicks,
            BsaExtractor? bsaExtractor, Dictionary<string, BsaFileRecord>? bsaIndex,
            Ba2Extractor? ba2Extractor, Dictionary<string, Ba2FileRecord>? ba2Index)
        {
            ArchivePath = archivePath;
            ArchiveLength = archiveLength;
            ArchiveLastWriteUtcTicks = archiveLastWriteUtcTicks;
            _bsaExtractor = bsaExtractor;
            _bsaIndex = bsaIndex;
            _ba2Extractor = ba2Extractor;
            _ba2Index = ba2Index;
        }

        public string ArchivePath { get; }
        public long ArchiveLength { get; }
        public long ArchiveLastWriteUtcTicks { get; }

        public static MeshArchiveSource OpenBsa(string path)
        {
            var fileInfo = new FileInfo(path);
            var archive = BsaParser.Parse(path);
            return new MeshArchiveSource(
                path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks,
                new BsaExtractor(path), BuildFileIndex(archive), null, null);
        }

        public static MeshArchiveSource OpenBa2(string path)
        {
            var fileInfo = new FileInfo(path);
            var extractor = new Ba2Extractor(path);
            return new MeshArchiveSource(
                path, fileInfo.Length, fileInfo.LastWriteTimeUtc.Ticks,
                null, null, extractor, BuildBa2FileIndex(extractor.Archive));
        }

        public bool TryResolve(string normalizedPath, out object record)
        {
            if (_bsaIndex is not null && _bsaIndex.TryGetValue(normalizedPath, out var bsa))
            {
                record = bsa;
                return true;
            }

            if (_ba2Index is not null && _ba2Index.TryGetValue(normalizedPath, out var ba2))
            {
                record = ba2;
                return true;
            }

            record = null!;
            return false;
        }

        public byte[] Extract(object record) => record switch
        {
            BsaFileRecord bsa => _bsaExtractor!.ExtractFile(bsa),
            Ba2FileRecord ba2 => _ba2Extractor!.ExtractFile(ba2),
            _ => throw new InvalidOperationException($"Unknown mesh record type: {record.GetType()}")
        };

        /// <summary>
        ///     Identity fields used as the decoded-mesh disk-cache key (archive identity already
        ///     distinguishes archives; this distinguishes files within one). BA2 GNRL maps directly;
        ///     a stray DX10 entry (textures normally) keys off its first chunk.
        /// </summary>
        public static (ulong NameHash, uint RawSize, uint Size, uint Offset) GetMetadata(object record) => record switch
        {
            BsaFileRecord bsa => (bsa.NameHash, bsa.RawSize, bsa.Size, bsa.Offset),
            Ba2FileRecord { Kind: Ba2HeaderType.Texture, Texture: { Chunks.Count: > 0 } texture } ba2
                => (ba2.NameHash, texture.Chunks[0].PackedSize, texture.Chunks[0].FullSize, (uint)texture.Chunks[0].Offset),
            Ba2FileRecord ba2 => (ba2.NameHash, ba2.PackedSize, ba2.RealSize, (uint)ba2.Offset),
            _ => throw new InvalidOperationException($"Unknown mesh record type: {record.GetType()}")
        };

        public void Dispose()
        {
            _bsaExtractor?.Dispose();
            _ba2Extractor?.Dispose();
        }
    }

    private sealed record MeshArchiveHit(
        MeshArchiveSource Source,
        object Record);
}

internal sealed record NpcMeshArchiveLookupMetadata(
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
