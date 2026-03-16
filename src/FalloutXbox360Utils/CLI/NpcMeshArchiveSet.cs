using FalloutXbox360Utils.Core.Formats.Bsa;

namespace FalloutXbox360Utils.CLI;

/// <summary>
///     Ordered mesh-asset lookup across one primary meshes BSA and optional fallback BSAs.
///     The first archive containing a requested virtual path wins.
/// </summary>
internal sealed class NpcMeshArchiveSet : IDisposable
{
    private readonly Dictionary<string, MeshArchiveHit?> _hitCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<MeshArchiveSource> _sources;

    private NpcMeshArchiveSet(List<MeshArchiveSource> sources)
    {
        _sources = sources;
    }

    public string PrimaryPath => _sources[0].ArchivePath;

    public IReadOnlyList<string> ArchivePaths => _sources.Select(source => source.ArchivePath).ToArray();

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
            sources.Add(new MeshArchiveSource(
                path,
                BsaParser.Parse(path),
                new BsaExtractor(path)));
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

    private MeshArchiveHit? ResolveHit(string virtualPath)
    {
        var normalized = virtualPath.Replace('/', '\\');
        if (_hitCache.TryGetValue(normalized, out var cached))
        {
            return cached;
        }

        foreach (var source in _sources)
        {
            var fileRecord = source.Archive.FindFile(normalized);
            if (fileRecord != null)
            {
                var hit = new MeshArchiveHit(source, fileRecord);
                _hitCache[normalized] = hit;
                return hit;
            }
        }

        _hitCache[normalized] = null;
        return null;
    }

    private sealed record MeshArchiveSource(
        string ArchivePath,
        BsaArchive Archive,
        BsaExtractor Extractor);

    private sealed record MeshArchiveHit(
        MeshArchiveSource Source,
        BsaFileRecord FileRecord);
}
