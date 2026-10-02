using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>Asset generations belong to one selected record snapshot, including captured/preview mode.</summary>
internal sealed partial class WorldViewData
{
    private readonly object _assetGate = new();
    private WorldAssetSnapshot? _assetSnapshot;
    private long _assetRevision;
    private WorldAssetChangeMonitor? _assetMonitor;
    private int _assetWatchers;
    private int _assetWatchGeneration;

    internal event EventHandler? AssetSourcesChanged;
    internal long AssetRevision { get { lock (_assetGate) return _assetRevision; } }
    internal IReadOnlyList<string> UnwatchedAssetRoots
    {
        get { lock (_assetGate) return _assetMonitor?.UnavailableRoots ?? []; }
    }

    internal WorldAssetSnapshot AssetSnapshot
    {
        get
        {
            lock (_assetGate)
                return _assetSnapshot ??= new(_assetRevision,
                    string.IsNullOrEmpty(SourceFilePath) ? BsaDiscoveryResult.Empty :
                    AssetSourceDiscovery.Discover(SourceFilePath, AdditionalDataPaths,
                        AssetDataDirectories, includeLooseFiles: true));
        }
    }

    /// <summary>Explicit refresh is also the fallback when a declared root cannot be watched.</summary>
    internal void RefreshAssetSources()
    {
        lock (_assetGate)
        {
            _assetRevision++;
            _assetSnapshot = null;
        }
        AssetSourcesChanged?.Invoke(this, EventArgs.Empty);
    }

    internal IDisposable WatchAssetChanges()
    {
        lock (_assetGate)
        {
            if (_assetWatchers++ == 0) StartAssetMonitor();
        }
        return new AssetWatchLease(this);
    }

    private void AssetInputsChanged()
    {
        lock (_assetGate)
        {
            _assetMonitor?.Dispose();
            _assetMonitor = null;
            ++_assetWatchGeneration;
            if (_assetWatchers != 0) StartAssetMonitor();
        }
        RefreshAssetSources();
    }

    private void StartAssetMonitor()
    {
        var generation = ++_assetWatchGeneration;
        var roots = new List<string>();
        if (SourceFilePath is { Length: > 0 }) roots.Add(Path.GetDirectoryName(Path.GetFullPath(SourceFilePath))!);
        roots.AddRange(AdditionalDataPaths.Select(p => Path.GetDirectoryName(Path.GetFullPath(p))!));
        roots.AddRange(AssetDataDirectories.Select(Path.GetFullPath));
        _assetMonitor = new WorldAssetChangeMonitor(roots, () =>
        {
            lock (_assetGate)
            {
                if (_assetWatchers == 0 || generation != _assetWatchGeneration) return;
                _assetRevision++;
                _assetSnapshot = null;
            }
            AssetSourcesChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void StopAssetMonitor()
    {
        lock (_assetGate)
        {
            if (--_assetWatchers != 0) return;
            ++_assetWatchGeneration;
            _assetMonitor?.Dispose();
            _assetMonitor = null;
        }
    }

    private sealed class AssetWatchLease(WorldViewData owner) : IDisposable
    {
        private WorldViewData? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.StopAssetMonitor();
    }
}

/// <summary>Reference identity prevents a same-path asset or selected-record replacement sharing resident output.</summary>
internal sealed record WorldAssetSnapshot(long Revision, BsaDiscoveryResult Sources);
