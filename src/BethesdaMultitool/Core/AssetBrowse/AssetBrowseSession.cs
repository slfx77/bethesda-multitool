using BethesdaMultitool.Core.Vfs;

namespace BethesdaMultitool.Core.AssetBrowse;

/// <summary>
///     One open asset-browse source: the <see cref="IGameFileSystem" /> plus the eagerly built
///     tree. The session owns the filesystem its factory created (both factories open privately —
///     no shared registry handles) and disposes it exactly once; double-dispose is a no-op.
///     Open off the UI thread: <see cref="OpenArchive" /> parses the archive table and both
///     factories enumerate every entry to build the tree.
/// </summary>
public sealed class AssetBrowseSession : IDisposable
{
    private bool _disposed;

    /// <summary>Takes ownership of <paramref name="fileSystem" /> (internal for tests).</summary>
    internal AssetBrowseSession(IGameFileSystem fileSystem, string sourceLabel, string sourcePath, AssetNode root)
    {
        FileSystem = fileSystem;
        SourceLabel = sourceLabel;
        SourcePath = sourcePath;
        Root = root;
    }

    /// <summary>Short display identity — the directory or archive file name.</summary>
    public string SourceLabel { get; }

    /// <summary>Full path of the opened directory or archive.</summary>
    public string SourcePath { get; }

    /// <summary>The built tree; its root is a folder node named <see cref="SourceLabel" />.</summary>
    public AssetNode Root { get; }

    /// <summary>The owned filesystem (previews/extraction). Invalid after <see cref="Dispose" />.</summary>
    public IGameFileSystem FileSystem { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        FileSystem.Dispose();
    }

    /// <summary>Opens a loose directory (files only — archives inside it stay unexpanded leaves).</summary>
    public static AssetBrowseSession OpenFolder(string directory)
    {
        var fullPath = Path.GetFullPath(directory);
        return Create(new LooseFileSystem(fullPath), fullPath);
    }

    /// <summary>Opens one archive (BSA or BA2, dispatched by magic via <see cref="GameFileSystem.OpenArchive" />).</summary>
    public static AssetBrowseSession OpenArchive(string archivePath)
    {
        var fullPath = Path.GetFullPath(archivePath);
        return Create(GameFileSystem.OpenArchive(fullPath), fullPath);
    }

    private static AssetBrowseSession Create(IGameFileSystem fs, string sourcePath)
    {
        try
        {
            var label = LabelFor(sourcePath);
            return new AssetBrowseSession(fs, label, sourcePath, AssetTreeBuilder.Build(fs, label));
        }
        catch
        {
            // The factory owns the filesystem from the moment it opens; a failed build must not leak it.
            fs.Dispose();
            throw;
        }
    }

    /// <summary>Last path segment (directory or file name); the full path when there is none (drive root).</summary>
    private static string LabelFor(string sourcePath)
    {
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(sourcePath));
        return name.Length > 0 ? name : sourcePath;
    }
}
