using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Windows.Foundation;

namespace BethesdaMultitool;

/// <summary>World bitmap build/apply (single + per-cell + coarse-tile), the standalone world-water layer,
/// the layer-build status banner, marker icons, the cell-grid spatial index, and cursor state.</summary>
public sealed partial class WorldMapControl
{
    private List<CellRecord> GetActiveCells()
    {
        return _state.GetActiveCells();
    }

    private void BuildCellGridLookup()
    {
        if (_data is null)
        {
            _cellGridLookup = null;
            _spatialIndex = null;
            return;
        }

        _spatialIndex = WorldSpatialIndex.Build(
            _data,
            GetActiveCells(),
            _state.FilteredMarkers,
            _state.SelectedWorldspace?.FormId,
            _currentDefaultWaterHeight);
        _cellGridLookup = _spatialIndex.CellsByGrid.ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private void ApplyZoomToFitWorldspace()
    {
        WorldMapViewportHelper.ZoomToFitWorldspace(
            GetActiveCells(),
            (float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight,
            out _zoom, out _panOffset);
    }

    /// <summary>
    ///     Lazily builds the standalone world water bitmap (<see cref="_worldWaterBitmap" />) once per
    ///     worldspace. Cheap CPU-only pass (33 px/cell water mask → premult RGBA), so it runs on a
    ///     background task rather than the heavier <see cref="WorldLayerBuildService" /> path. Built
    ///     regardless of the water toggle so toggling water ON is an instant redraw. No-op when already
    ///     built/building for this worldspace.
    /// </summary>
    private void EnsureWorldWaterBitmap(uint? worldspaceFormId)
    {
        if (_worldWaterBuilding || _data is null) return;
        // Build ONCE per worldspace — keyed on the worldspace, NOT on a non-null bitmap. A worldspace
        // with no water makes RenderWorldWaterAggregate return null; the old `_worldWaterBitmap is not
        // null` guard then never tripped, so this re-kicked a FULL-worldspace water build EVERY draw
        // (each a whole-heightmap pass on a background core) and its completion re-Invalidate'd the
        // canvas — a continuous CPU peg with the map visually static. Confirmed via process dump
        // (_worldWaterBuilding=1, every other thread idle). A worldspace switch clears the id in
        // ClearWorldBitmaps so a real change still rebuilds.
        if (_worldWaterWorldspaceFormId == worldspaceFormId) return;

        var cells = GetActiveCells();
        if (cells.Count == 0) return;

        _worldWaterBuilding = true;
        // Mark this worldspace attempted up front so a null result (no water) OR a build exception can't
        // re-trigger the guard above next frame. ApplyWorldWaterResult re-affirms it on completion.
        _worldWaterWorldspaceFormId = worldspaceFormId;
        var version = ++_worldWaterVersion;
        var defaultWaterHeight = _currentDefaultWaterHeight;
        var palette = _currentWaterPalette; // toggle-independent: the bitmap is built colored, drawn only when _showWater
        var cache = _data.RenderCache;
        // Snapshot the cell list for the background pass (mirrors the terrain-aggregate build).
        _ = BuildWorldWaterBitmapAsync(cells.ToList(), defaultWaterHeight, palette, cache, version, worldspaceFormId);
    }

    private async Task BuildWorldWaterBitmapAsync(
        List<CellRecord> cells, float? defaultWaterHeight, WaterColorPalette? palette,
        WorldRenderCache? cache, int version, uint? worldspaceFormId)
    {
        try
        {
            var bmp = await Task.Run(() =>
                WorldMapLayerRenderer.RenderWorldWaterAggregate(cells, defaultWaterHeight, cache, palette))
                .ConfigureAwait(false);
            _ = DispatcherQueue.TryEnqueue(() => ApplyWorldWaterResult(bmp, version, worldspaceFormId));
        }
        catch (Exception ex)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn("World water bitmap build failed: {0}", ex.ToString());
            _ = DispatcherQueue.TryEnqueue(() => { _worldWaterBuilding = false; });
        }
    }

    private void ApplyWorldWaterResult(WorldMapLayerRenderer.LayerBitmap? bmp, int version, uint? worldspaceFormId)
    {
        _worldWaterBuilding = false;
        if (version != _worldWaterVersion) return; // superseded (worldspace switched / cache cleared)

        _worldWaterBitmap?.Dispose();
        _worldWaterBitmap = null;
        if (bmp is { } b)
        {
            _worldWaterBitmap = CanvasBitmap.CreateFromBytes(
                MapCanvas, b.Pixels, b.Width, b.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _worldWaterMinX = b.MinCellX;
            _worldWaterMaxY = b.MaxCellY;
            _worldWaterPixelWidth = b.Width;
            _worldWaterPixelHeight = b.Height;
        }
        _worldWaterWorldspaceFormId = worldspaceFormId;
        Map2DProfilerTrace.Event("world-water-built",
            $"ws=0x{worldspaceFormId ?? 0:X8} present={(_worldWaterBitmap is not null)} size={_worldWaterPixelWidth}x{_worldWaterPixelHeight}");
        MapCanvas.Invalidate();
    }

    private async Task BuildAndApplyWorldBitmapAsync(LayerBuildRequest request)
    {
        try
        {
            var result = await WorldLayerBuildService.BuildAsync(request).ConfigureAwait(false);
            _ = DispatcherQueue.TryEnqueue(() => ApplyWorldBitmapBuildResult(result));
        }
        catch (Exception ex)
        {
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                if (request.Version == _worldHeightmapBuildVersion)
                {
                    _worldHeightmapDirty = false;
                    if (request.Layer == WorldMapLayer.TerrainTextures)
                    {
                        _terrainTextureViewportKey = null;
                    }
                    ShowLayerBuildStatus($"{request.Layer.DisplayName()} failed: {ex.Message}", busy: false);
                    MapCanvas.Invalidate();
                }
            });
        }
    }

    private void ApplyWorldBitmapBuildResult(LayerBuildResult result)
    {
        if (result.Version != _worldHeightmapBuildVersion)
        {
            return;
        }

        var hasContent = result.Bitmap is not null || result.CellPixels is not null
            || result.CoarseTiles is not null;
        if (!hasContent)
        {
            if (_currentLayer == WorldMapLayer.TerrainTextures)
            {
                _terrainTextureViewportKey = null;
            }
            ShowLayerBuildStatus(result.Message ?? $"{_currentLayer.DisplayName()} has no renderable data.", busy: false);
            MapCanvas.Invalidate();
            return;
        }

        // Incremental merge for per-cell results: when the cached dict was already populated
        // for the current layer, dispatch only built new cells (the EnsureHeightmapBitmap
        // diff). Merge them in instead of disposing the rest. The key now includes
        // pixelsPerCell so multiple resolutions of the same cell can coexist.
        var isIncrementalMerge = result.CellPixels is not null
            && _layerCellBitmaps is not null
            && _layerCellBitmapsLayer == _currentLayer;

        if (!isIncrementalMerge)
        {
            _worldHeightmapBitmap?.Dispose();
            _worldHeightmapBitmap = null;
            if (_layerCellBitmaps is not null)
            {
                var disposed = _layerCellBitmaps.Count;
                foreach (var bmp in _layerCellBitmaps.Values) bmp.Dispose();
                _layerCellBitmaps = null;
                _layerCellBitmapsCacheGen++;
                Map2DProfilerTrace.Event("cache-gen-bump",
                    $"to={_layerCellBitmapsCacheGen} reason=ApplyWorldBitmapBuildResult disposed={disposed}");
            }
            DisposeCoarseTileBitmaps();
            _layerCellBitmapsLayer = null;
            // The cache the held cap was sized for was just replaced; size fresh next rebuild.
            _layerCellBitmapCapPolicy.Reset();
        }

        if (result.CellPixels is not null)
        {
            _layerCellBitmaps ??= new OrderedDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>(result.CellPixels.Count);
            foreach (var ((gx, gy), pixels) in result.CellPixels)
            {
                var tripleKey = (gx, gy, result.CellPixelsPerCell);
                if (_layerCellBitmaps.TryGetValue(tripleKey, out var stale))
                {
                    stale.Dispose();
                    _layerCellBitmaps.Remove(tripleKey);
                }
                _layerCellBitmaps.Add(tripleKey, CanvasBitmap.CreateFromBytes(
                    MapCanvas,
                    pixels,
                    result.CellPixelsPerCell,
                    result.CellPixelsPerCell,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized));
            }
            _layerCellBitmapsLayer = _currentLayer;
        }
        else if (result.CoarseTiles is not null)
        {
            // Oversized worldspace: one bitmap per coarse tile (bounded count). The cleared-above state
            // guarantees _coarseTileBitmaps is null here, so this is always a fresh full build.
            _coarseTileBitmaps = new Dictionary<(int tileGx, int tileGy), CanvasBitmap>(result.CoarseTiles.Count);
            _coarseTileCellSpan = result.CoarseTileCellSpan;
            _coarseTilePixelsPerCell = result.CoarseTilePixelsPerCell;
            _coarseTileBitmapsLayer = _currentLayer;
            var tileSidePx = result.CoarseTileCellSpan * result.CoarseTilePixelsPerCell;
            foreach (var ((tileGx, tileGy), pixels) in result.CoarseTiles)
            {
                _coarseTileBitmaps[(tileGx, tileGy)] = CanvasBitmap.CreateFromBytes(
                    MapCanvas,
                    pixels,
                    tileSidePx,
                    tileSidePx,
                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            }
            Map2DProfilerTrace.Event("coarse-tiles-built",
                $"layer={_currentLayer} tiles={_coarseTileBitmaps.Count} span={_coarseTileCellSpan} ppc={_coarseTilePixelsPerCell}");
        }
        else if (result.Bitmap is { } bitmap)
        {
            _worldHeightmapBitmap = CanvasBitmap.CreateFromBytes(
                MapCanvas,
                bitmap.Pixels,
                bitmap.Width,
                bitmap.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _worldHmMinX = bitmap.MinCellX;
            _worldHmMaxY = bitmap.MaxCellY;
            _worldHmPixelWidth = bitmap.Width;
            _worldHmPixelHeight = bitmap.Height;
        }

        HideLayerBuildStatus();
        MapCanvas.Invalidate();
    }

    private void ShowLayerBuildStatus(string message, bool busy)
    {
        if (LayerBuildOverlay is null) return;
        LayerBuildStatusText.Text = message;
        LayerBuildProgress.IsActive = busy;
        LayerBuildProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        LayerBuildOverlay.Visibility = Visibility.Visible;
    }

    private void HideLayerBuildStatus()
    {
        if (LayerBuildOverlay is null) return;
        LayerBuildProgress.IsActive = false;
        LayerBuildOverlay.Visibility = Visibility.Collapsed;
    }

    private void SetInteractiveCursor(bool interactive)
    {
        var shape = interactive ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow;
        if (shape != _currentCursorShape)
        {
            _currentCursorShape = shape;
            ProtectedCursor = InputSystemCursor.Create(shape);
        }
    }

    /// <summary>
    ///     Builds the marker icon set for the loaded game (installed archive first, bundled fallback,
    ///     glyph-only when no authentic art is wired), rebuilding only when the scene changes. The marker
    ///     TYPE is always resolved per game via <c>MapMarkerCatalog</c>; this only governs the icon art.
    /// </summary>
    private void EnsureMarkerIconSet(ICanvasResourceCreator resourceCreator)
    {
        var game = _data?.Game ?? Core.Games.BethesdaGame.Unknown;
        if (_markerIconSet is not null && _markerIconSetGame == game) return;

        _markerIconSet?.Dispose();
        DisposeTintedMarkerIcons();
        _markerIconSet = MapMarkerIconSetFactory.Create(_data, resourceCreator);
        _markerIconSetGame = game;
    }

    /// <summary>
    ///     Returns the per-raw-value icon bitmaps to blit for the current marker set: pre-tinted to the
    ///     color scheme for the embedded (monochrome) set, the raw set otherwise, or null/empty for the
    ///     glyph-only set (the caller then draws catalog glyph/color dots). Tinting once here (a handful of
    ///     one-shot <see cref="ColorMatrixEffect" /> passes into per-icon render targets) replaces the old
    ///     per-marker-per-frame effect in <c>DrawMapMarkers</c>, which dominated frame time at zoomed-out
    ///     overview (every worldspace marker in view). Cached until the color scheme changes.
    /// </summary>
    private IReadOnlyDictionary<int, CanvasBitmap>? GetMarkerDrawIcons(
        ICanvasResourceCreator resourceCreator, HeightmapColorScheme scheme)
    {
        var set = _markerIconSet;
        if (set is null) return null;
        var baseIcons = set.Icons;
        if (baseIcons.Count == 0 || !set.RequiresTinting) return baseIcons; // glyph-only or pre-styled atlas

        var argb = (uint)((255 << 24) | (scheme.R << 16) | (scheme.G << 8) | scheme.B);
        if (_tintedMarkerIconBitmaps is not null && _tintedMarkerColorArgb == argb)
        {
            return _tintedMarkerIconBitmaps;
        }

        DisposeTintedMarkerIcons();
        var tintScale = new Matrix5x4
        {
            M11 = scheme.R / 255f, M22 = scheme.G / 255f, M33 = scheme.B / 255f, M44 = 1f
        };
        var tinted = new Dictionary<int, CanvasBitmap>(baseIcons.Count);
        foreach (var (raw, icon) in baseIcons)
        {
            var w = (float)icon.SizeInPixels.Width;
            var h = (float)icon.SizeInPixels.Height;
            // 96 DPI render target → DIPs == pixels, so the icon maps 1:1 with no resample.
            var rt = new CanvasRenderTarget(resourceCreator, w, h, 96f);
            using (var rtds = rt.CreateDrawingSession())
            {
                rtds.Clear(Colors.Transparent);
                using var tintEffect = new ColorMatrixEffect { Source = icon, ColorMatrix = tintScale };
                rtds.DrawImage(tintEffect, new Rect(0, 0, w, h), new Rect(0, 0, w, h));
            }
            tinted[raw] = rt; // CanvasRenderTarget is a CanvasBitmap; blit it directly per marker.
        }

        _tintedMarkerIconBitmaps = tinted;
        _tintedMarkerColorArgb = argb;
        return tinted;
    }

    private void DisposeMarkerIcons()
    {
        _markerIconSet?.Dispose();
        _markerIconSet = null;
        _markerIconSetGame = Core.Games.BethesdaGame.Unknown;
        DisposeTintedMarkerIcons();
    }

    private void DisposeTintedMarkerIcons()
    {
        if (_tintedMarkerIconBitmaps != null)
        {
            // Only the render targets we created for tinting are ours to dispose; the base icons belong
            // to _markerIconSet. A pre-styled (untinted) set returns its base icons directly, so it never
            // populates this cache — every entry here is a tint render target.
            foreach (var bmp in _tintedMarkerIconBitmaps.Values) bmp.Dispose();
            _tintedMarkerIconBitmaps = null;
            _tintedMarkerColorArgb = 0;
        }
    }
}

