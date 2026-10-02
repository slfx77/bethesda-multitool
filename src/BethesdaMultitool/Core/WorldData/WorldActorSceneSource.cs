using BethesdaMultitool.Core.Formats.Nif.Rendering.Npc;
using BethesdaMultitool.Core.Assets;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Orchestration;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
/// One world pipeline's exact selected records and ordered assets. Composition is serialized;
/// scenes are returned to the renderer's byte-bounded cache, never retained by this source.
/// </summary>
internal sealed class WorldActorSceneSource : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, WorldActorDefinition> _actors;
    private readonly Func<CancellationToken, NpcBrowserService?> _createService;
    private readonly string[] _meshSources;
    private NpcBrowserService? _service;
    private bool _created;
    private bool _disposed;

    internal WorldActorSceneSource(WorldActorCatalog catalog, string primaryPath,
        IReadOnlyList<string> meshSources, IReadOnlyList<string> textureSources,
        AssetSourcePlan? meshPlan = null, AssetSourcePlan? texturePlan = null)
    {
        _actors = catalog.Actors.ToDictionary(actor => actor.CacheKey, StringComparer.OrdinalIgnoreCase);
        _meshSources = meshSources.ToArray();
        var assets = new BsaDiscoveryResult(meshSources.ToArray(), textureSources.ToArray(), false)
            { MeshPlan = meshPlan, TexturePlan = texturePlan };
        _createService = token => NpcBrowserService.TryCreateFromResolver(
            catalog.Resolver, primaryPath, assets, cancellationToken: token);
    }

    internal bool Contains(string cacheKey) => _actors.ContainsKey(cacheKey);
    internal string GetCacheIdentity(string cacheKey) => _service?.GetAssetCacheIdentity(cacheKey) ?? cacheKey;

    internal WorldActorScene? Build(string cacheKey, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_actors.TryGetValue(cacheKey, out var actor)) return null;
        EnterGate(cancellationToken);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_created)
            {
                _service = _createService(cancellationToken);
                _created = true;
            }
            if (_service is null) return null;
            var scene = _service.BuildWorldViewerScene(actor.BaseFormId,
                actor.PlacementType == "ACRE", cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            _service.RememberActorAssets(cacheKey, scene?.AssetReadReceipts ?? _service.AssetReceipts);
            return scene is null ? null : new WorldActorScene(actor, scene, _meshSources);
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        // The mesh cache cancels/drains decode producers before disposing this source.
        EnterGate(CancellationToken.None);
        try
        {
            _disposed = true;
            _service?.Dispose();
            _service = null;
        }
        finally { _gate.Release(); }
    }

    private void EnterGate(CancellationToken cancellationToken)
    {
        // Disposal can run on the XAML thread. Acquire asynchronously and observe completion
        // without entering the STA message pump while an in-flight composition releases it.
        var acquisition = _gate.WaitAsync(cancellationToken);
        NonPumpingWait.Wait(acquisition);
        acquisition.GetAwaiter().GetResult();
    }
}

internal sealed record WorldActorScene(WorldActorDefinition Actor, BethesdaViewerScene Scene,
    IReadOnlyList<string> MeshSources);
