using BethesdaMultitool.Core.Formats.Bsa.Index;

namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     <see cref="IGameFileSystem" /> over a single BSA or BA2, dispatched by file magic via
///     <see cref="ArchiveReader.Open" />. Reads are lock-free and concurrent: BSA extraction is
///     memory-mapped per call and BA2 extraction reads absolute offsets from one shared read-only
///     view accessor — both proven under the 3D viewer's parallel decode workers.
/// </summary>
public sealed class ArchiveFileSystem : IGameFileSystem
{
    private readonly ArchiveReader _reader;

    /// <summary>Opens <paramref name="archivePath" /> (BSA or BA2 by magic) and owns the reader.</summary>
    public ArchiveFileSystem(string archivePath)
    {
        _reader = ArchiveReader.Open(archivePath);
        Label = archivePath;
    }

    public string Label { get; }

    /// <summary>The wrapped reader, for consumers needing format-specific surfaces (e.g. BSA conversion).</summary>
    public ArchiveReader Reader => _reader;

    public bool Exists(string path) => _reader.FindEntry(path) is not null;

    public GameFileEntry? TryStat(string path) =>
        _reader.FindEntry(path) is { } entry ? ToEntry(entry) : null;

    public byte[]? TryReadAllBytes(string path) =>
        _reader.FindEntry(path) is { } entry ? _reader.Extract(entry) : null;

    public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
    {
        var normalizedPrefix = prefix is null ? null : VfsPath.Normalize(prefix);
        foreach (var entry in _reader.ListFiles())
        {
            var normalized = VfsPath.Normalize(entry.FullPath);
            if (VfsPath.MatchesPrefix(normalized, normalizedPrefix))
            {
                yield return new GameFileEntry(normalized, entry.Size, Label);
            }
        }
    }

    public void Dispose() => _reader.Dispose();

    private GameFileEntry ToEntry(ArchiveReader.ArchiveEntry entry) =>
        new(VfsPath.Normalize(entry.FullPath), entry.Size, Label);
}
