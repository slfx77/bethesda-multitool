namespace BethesdaMultitool.Core.Vfs;

/// <summary>
///     <see cref="IGameFileSystem" /> over a loose-file root (typically a game <c>Data</c>
///     directory). Stateless over the OS filesystem, so inherently safe for concurrent reads.
///     Virtual paths are resolved relative to the root; anything escaping it (<c>..</c>, rooted
///     paths) does not resolve.
/// </summary>
public sealed class LooseFileSystem : IGameFileSystem
{
    private readonly string _root;

    public LooseFileSystem(string rootDirectory)
    {
        _root = Path.GetFullPath(rootDirectory);
        Label = _root;
    }

    public string Label { get; }

    public bool Exists(string path)
    {
        return Resolve(path) is { } full && File.Exists(full);
    }

    public GameFileEntry? TryStat(string path)
    {
        if (Resolve(path) is not { } full)
        {
            return null;
        }

        var info = new FileInfo(full);
        return info.Exists ? new GameFileEntry(VfsPath.Normalize(path), info.Length, Label) : null;
    }

    public byte[]? TryReadAllBytes(string path)
    {
        if (Resolve(path) is not { } full || !File.Exists(full))
        {
            return null;
        }

        try
        {
            return File.ReadAllBytes(full);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Locked/unreadable loose file: null lets a layered mount fall through to an archive
            // copy, matching the hand-rolled loose→archive chains this API replaces.
            return null;
        }
    }

    public IEnumerable<GameFileEntry> EnumerateFiles(string? prefix = null)
    {
        if (!Directory.Exists(_root))
        {
            yield break;
        }

        var normalizedPrefix = prefix is null ? null : VfsPath.Normalize(prefix);
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            var relative = VfsPath.Normalize(Path.GetRelativePath(_root, file));
            if (VfsPath.MatchesPrefix(relative, normalizedPrefix))
            {
                yield return new GameFileEntry(relative, new FileInfo(file).Length, Label);
            }
        }
    }

    public void Dispose()
    {
        // Nothing owned: purely a view over the OS filesystem.
    }

    /// <summary>Root-relative resolution; null when the path is rooted or contains a parent segment.</summary>
    private string? Resolve(string path)
    {
        var normalized = VfsPath.Normalize(path);
        if (normalized.Length == 0 || Path.IsPathRooted(normalized))
        {
            return null;
        }

        // Reject any `..` segment outright rather than trusting full-path round-tripping: a
        // traversal that happens to re-enter the root is still not a valid virtual path.
        foreach (var segment in normalized.Split('\\'))
        {
            if (segment == "..")
            {
                return null;
            }
        }

        // Virtual paths are backslash-normalized, but '\' is not a separator on Unix (the CLI
        // TFM runs on Linux CI) — translate to the OS separator before combining, or a
        // multi-segment path like "Strings\X.STRINGS" becomes one bogus filename.
        var osRelative = Path.DirectorySeparatorChar == '\\'
            ? normalized
            : normalized.Replace('\\', Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(_root, osRelative));
        return full.StartsWith(_root, StringComparison.OrdinalIgnoreCase) ? full : null;
    }
}
