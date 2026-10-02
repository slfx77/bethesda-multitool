namespace BethesdaMultitool.Core.WorldData;

/// <summary>Debounced hints for declared asset roots; never watches a general working directory.</summary>
internal sealed class WorldAssetChangeMonitor : IDisposable
{
    private readonly object _gate = new();
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly Timer _timer;
    private readonly Action _changed;
    private bool _disposed;

    internal WorldAssetChangeMonitor(IEnumerable<string> roots, Action changed)
    {
        _changed = changed;
        _timer = new Timer(_ => Publish(), null, Timeout.Infinite, Timeout.Infinite);
        var unavailable = new List<string>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            FileSystemWatcher? watcher = null;
            try
            {
                // An absent root is reported rather than widening the watch to its parent.
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                   NotifyFilters.LastWrite | NotifyFilters.Size,
                    InternalBufferSize = 32768
                };
                watcher.Changed += Changed;
                watcher.Created += Changed;
                watcher.Deleted += Changed;
                watcher.Renamed += Renamed;
                watcher.Error += (_, _) => Schedule(); // overflow invalidates the whole asset generation
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
                watcher = null; // ownership transferred only after successful startup
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                unavailable.Add(root);
            }
            finally { watcher?.Dispose(); }
        }
        UnavailableRoots = unavailable.AsReadOnly();
    }

    internal IReadOnlyList<string> UnavailableRoots { get; }

    internal static bool IsAssetPath(string root, string path)
    {
        var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
        if (relative.StartsWith("../", StringComparison.Ordinal) || Path.IsPathRooted(relative)) return false;
        if (relative.Equals("Data", StringComparison.OrdinalIgnoreCase)) return true;
        // Only virtual asset roots and discovered archive names. Render/export/report directories
        // beside the source are not dependencies and must not trigger a refresh loop.
        if (relative.StartsWith("Data/", StringComparison.OrdinalIgnoreCase)) relative = relative[5..];
        var first = relative.Split('/', 2)[0].ToLowerInvariant();
        return first is "meshes" or "textures" or "materials" or "trees" or "geometries"
            ? true
            : !relative.Contains('/') && Path.GetExtension(relative).ToLowerInvariant() is ".bsa" or ".ba2";
    }

    private void Changed(object sender, FileSystemEventArgs e)
    {
        if (sender is FileSystemWatcher watcher && IsAssetPath(watcher.Path, e.FullPath)) Schedule();
    }

    private void Renamed(object sender, RenamedEventArgs e)
    {
        if (sender is FileSystemWatcher watcher &&
            (IsAssetPath(watcher.Path, e.FullPath) || IsAssetPath(watcher.Path, e.OldFullPath))) Schedule();
    }

    private void Schedule()
    {
        lock (_gate)
            if (!_disposed) _timer.Change(350, Timeout.Infinite);
    }

    private void Publish()
    {
        lock (_gate) { if (_disposed) return; }
        // The receiver checks its subscription generation. A queued callback cannot revive a view.
        try { _changed(); }
        catch (Exception ex) { BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn("Asset change notification failed: {0}", ex); }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _timer.Dispose();
        }
        foreach (var watcher in _watchers) watcher.Dispose();
    }
}
