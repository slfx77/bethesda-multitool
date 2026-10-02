using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

public sealed partial class WorldMapControl
{
    private WorldAssetBinding? _assetBinding;

    private void BindAssetSources(WorldViewData data)
    {
        _assetBinding = new WorldAssetBinding(data, action => DispatcherQueue.TryEnqueue(() => action()),
            RefreshResidentAssets);
        ShowAssetWatchStatus(data);
    }

    private void UnbindAssetSources()
    {
        _assetBinding?.Dispose();
        _assetBinding = null;
        if (_data is { } data) LandscapeTexturePalette.Release(data);
    }

    private void RefreshResidentAssets()
    {
        if (_data is not { } data) return;
        // Every delayed bitmap/apply now belongs to a retired generation, including top-down.
        CancelTopDownOverlay();
        InvalidateWorldBitmap(keepCurrentBitmap: false);
        DisposeCellDetailBitmaps();
        LandscapeTexturePalette.Release(data);
        ShowAssetWatchStatus(data);
        MapCanvas.Invalidate();
    }

    private void ShowAssetWatchStatus(WorldViewData data)
    {
        if (data.UnwatchedAssetRoots.Count > 0)
            ShowLayerBuildStatus("Asset watching unavailable; reload to refresh.", busy: false);
    }
}
