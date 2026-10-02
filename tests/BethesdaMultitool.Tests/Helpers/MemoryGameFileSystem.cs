using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Tests.Helpers;

/// <summary>
///     An in-memory <see cref="IGameFileSystem" /> layer over (virtual path, bytes) pairs, for texture-companion tests
///     that need real payloads without touching the file system. Paths compare case-insensitively with either separator,
///     as every BMT layer does; <see cref="IGameFileSystem.TryReadAllBytesBounded" /> reports this layer's label as the
///     entry source, and counts reads so a test can see how often a layer was touched.
/// </summary>
internal sealed class MemoryGameFileSystem : IGameFileSystem
{
    private readonly Dictionary<string, byte[]> _files = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates a layer.</summary>
    public MemoryGameFileSystem(string label, params (string Path, byte[] Bytes)[] files)
    {
        Label = label;
        foreach (var (path, bytes) in files)
        {
            _files[Normalize(path)] = bytes;
        }
    }

    /// <summary>The number of successful bounded reads.</summary>
    public int Reads { get; private set; }

    /// <inheritdoc />
    public string Label { get; }

    /// <inheritdoc />
    public bool Exists(string path)
    {
        return _files.ContainsKey(Normalize(path));
    }

    /// <inheritdoc />
    public GameFileEntry? TryStat(string path)
    {
        var key = Normalize(path);
        return _files.TryGetValue(key, out var bytes) ? new GameFileEntry(key, bytes.Length, Label) : null;
    }

    /// <inheritdoc />
    public byte[]? TryReadAllBytes(string path)
    {
        return _files.TryGetValue(Normalize(path), out var bytes) ? [.. bytes] : null;
    }

    /// <inheritdoc />
    public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
    {
        var key = Normalize(path);
        if (!_files.TryGetValue(key, out var bytes) || bytes.Length > maximumBytes)
        {
            return null;
        }

        Reads++;
        return new GameFileReadResult(new GameFileEntry(key, bytes.Length, Label), [.. bytes]);
    }

    /// <inheritdoc />
    public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
    {
        var normalized = prefix is null ? null : Normalize(prefix);
        return _files.Where(f => normalized is null || f.Key.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
            .Select(f => new GameFileEntry(f.Key, f.Value.Length, Label)).ToArray();
    }

    /// <inheritdoc />
    public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries)
    {
        var entries = EnumerateFiles(prefix).ToArray();
        return new GameFileEnumerationPage(entries.Take(maximumEntries).ToArray(), entries.Length > maximumEntries);
    }

    /// <inheritdoc />
    public void Dispose()
    {
    }

    private static string Normalize(string path)
    {
        return path.Replace('/', '\\').TrimStart('\\');
    }
}
