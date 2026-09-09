namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     Mounts another file system under a directory name, so a layer whose own root is (say) the
///     Battlespire CD's <c>videos</c> directory surfaces its files as <c>videos\ANCHORS.SMK</c>
///     rather than at the VFS root. Everything is delegated after the prefix is stripped, and
///     paths outside the prefix answer "absent".
/// </summary>
public sealed class PrefixedFileSystem : IGameFileSystem
{
    private readonly IGameFileSystem _inner;
    private readonly string _prefix;

    /// <summary>Creates a prefixed view of the supplied file system.</summary>
    /// <param name="inner">The file system to expose.</param>
    /// <param name="prefix">The directory name (or <c>a\b</c> chain) to expose it under.</param>
    public PrefixedFileSystem(IGameFileSystem inner, string prefix)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);

        _inner = inner;
        _prefix = VfsPath.Normalize(prefix).TrimEnd('\\') + '\\';
        Label = $"{inner.Label} as {_prefix}";
    }

    public string Label { get; }

    public bool Exists(string path)
    {
        return Strip(path) is { } innerPath && _inner.Exists(innerPath);
    }

    public GameFileEntry? TryStat(string path)
    {
        return Strip(path) is { } innerPath ? Wrap(_inner.TryStat(innerPath)) : null;
    }

    public byte[]? TryReadAllBytes(string path)
    {
        return Strip(path) is { } innerPath ? _inner.TryReadAllBytes(innerPath) : null;
    }

    public GameFileReadResult? TryReadAllBytesBounded(string path, long maximumBytes)
    {
        if (Strip(path) is not { } innerPath)
        {
            return null;
        }

        var result = _inner.TryReadAllBytesBounded(innerPath, maximumBytes);
        return result is null ? null : result with { Entry = Wrap(result.Entry)! };
    }

    public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
    {
        var normalized = prefix is null ? string.Empty : VfsPath.Normalize(prefix);
        if (normalized.Length == 0 || _prefix.StartsWith(normalized, StringComparison.OrdinalIgnoreCase))
        {
            // The caller's prefix ends inside (or at) our mount name: everything qualifies.
            return _inner.EnumerateFiles().Select(e => Wrap(e)!);
        }

        return Strip(normalized) is { } innerPrefix
            ? _inner.EnumerateFiles(innerPrefix).Select(e => Wrap(e)!)
            : [];
    }

    public GameFileEnumerationPage EnumerateFilesBounded(string? prefix, int maximumEntries)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumEntries);

        var entries = new List<GameFileEntry>();
        var truncated = false;
        foreach (var entry in EnumerateFiles(prefix))
        {
            if (entries.Count >= maximumEntries)
            {
                truncated = true;
                break;
            }

            entries.Add(entry);
        }

        return new GameFileEnumerationPage(entries, truncated);
    }

    public void Dispose()
    {
        _inner.Dispose();
    }

    private string? Strip(string path)
    {
        var normalized = VfsPath.Normalize(path);
        return normalized.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[_prefix.Length..]
            : null;
    }

    private GameFileEntry? Wrap(GameFileEntry? entry)
    {
        return entry is null ? null : entry with { Path = _prefix + VfsPath.Normalize(entry.Path) };
    }
}
