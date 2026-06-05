using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Ordered mesh-asset lookup across one primary meshes BSA and optional fallback BSAs.
///     The first archive containing a requested virtual path wins.
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
            source.Extractor.Dispose();
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
            var fileInfo = new FileInfo(path);
            var archive = BsaParser.Parse(path);
            sources.Add(new MeshArchiveSource(
                path,
                archive,
                new BsaExtractor(path),
                BuildFileIndex(archive),
                fileInfo.Length,
                fileInfo.LastWriteTimeUtc.Ticks));
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

        data = hit.Source.Extractor.ExtractFile(hit.FileRecord);
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

        return new NpcMeshArchiveLookupMetadata(
            normalized,
            Found: true,
            ArchiveSetIdentity,
            hit.Source.ArchivePath,
            hit.Source.ArchiveLength,
            hit.Source.ArchiveLastWriteUtcTicks,
            hit.FileRecord.NameHash,
            hit.FileRecord.RawSize,
            hit.FileRecord.Size,
            hit.FileRecord.Offset);
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
                if (source.FileIndex.TryGetValue(normalized, out var fileRecord))
                {
                    var hit = new MeshArchiveHit(source, fileRecord);
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

    private sealed record MeshArchiveSource(
        string ArchivePath,
        BsaArchive Archive,
        BsaExtractor Extractor,
        Dictionary<string, BsaFileRecord> FileIndex,
        long ArchiveLength,
        long ArchiveLastWriteUtcTicks);

    private sealed record MeshArchiveHit(
        MeshArchiveSource Source,
        BsaFileRecord FileRecord);
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
