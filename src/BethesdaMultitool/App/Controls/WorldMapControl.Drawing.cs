using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.UI;
using BethesdaMultitool.Core.Diagnostics;

namespace BethesdaMultitool;

/// <summary>Win2D draw callback and per-frame composite for the world map (overview / cell-detail).</summary>
public sealed partial class WorldMapControl
{
    /// <summary>
    ///     Logs a UI-thread render/timer-tick exception (with rich 2D view context) exactly once per
    ///     distinct signature, so a deterministic per-frame fault doesn't flood the log. Callers
    ///     swallow the exception so a single bad frame or rebuild tick can't tear down the whole
    ///     window — WinUI routes an unhandled UI-thread exception to
    ///     <see cref="FalloutApp.App_UnhandledException" />, which terminates the process. The full
    ///     exception (incl. stack) goes to the file log so the root cause is still diagnosable.
    /// </summary>
    private void LogUiThreadFault(string where, Exception ex)
    {
        var sig = $"{where}|{ex.GetType().FullName}|{ex.Message}";
        if (sig == _lastUiFaultSignature) return;
        _lastUiFaultSignature = sig;
        BethesdaMultitool.Core.Diagnostics.Logger.Instance.Error(
            "[Map2D] UI-thread fault in {0}: mode={1} layer={2} zoom={3:F5} pan=({4:F1},{5:F1}) " +
            "ws=0x{6:X8} renderedObjects={7} navMesh={8} aggregate={9} cacheSize={10} cap={11}\n{12}",
            where, _state.Mode, _currentLayer, _zoom, _panOffset.X, _panOffset.Y,
            _state.SelectedWorldspace?.FormId ?? 0u, _showRenderedObjects, _showNavMesh,
            _terrainTexturesAggregateActive, _layerCellBitmaps?.Count ?? 0, _layerCellBitmapCap, ex);
    }

    private void MapCanvas_Draw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var ds = args.DrawingSession;
        ds.Clear(Color.FromArgb(255, 20, 20, 25));

        if (_data == null) return;

        var canvasW = (float)sender.ActualWidth;
        var canvasH = (float)sender.ActualHeight;

        if (Map2DProfilerTrace.IsEnabled)
        {
            Map2DProfilerTrace.IncrementFrame();
            var cacheSize = _layerCellBitmaps?.Count ?? 0;
            Map2DProfilerTrace.Event("draw",
                $"zoom={_zoom:F4} pan=({_panOffset.X:F1},{_panOffset.Y:F1}) layer={_currentLayer} cacheSize={cacheSize} cap={_layerCellBitmapCap} cacheGen={_layerCellBitmapsCacheGen} buildVersion={_worldHeightmapBuildVersion}");
        }

        // A render exception here used to crash the whole app silently (routed to the console-only
        // App.UnhandledException). Catch + log with full context, skip the bad frame, keep the window
        // alive. The next Invalidate redraws cleanly once the transient condition clears.
        var drawStart = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            DrawMapContent(ds, canvasW, canvasH);
        }
        catch (Exception ex)
        {
            LogUiThreadFault("MapCanvas_Draw", ex);
        }

        if (Map2DProfilerTrace.IsEnabled)
        {
            var drawMs = System.Diagnostics.Stopwatch.GetElapsedTime(drawStart).TotalMilliseconds;
            if (drawMs >= 40)
            {
                Map2DProfilerTrace.Event("slow-draw",
                    $"ms={drawMs:F0} zoom={_zoom:F4} layer={_currentLayer} cacheSize={_layerCellBitmaps?.Count ?? 0}");
            }
        }
    }

    private void DrawMapContent(CanvasDrawingSession ds, float canvasW, float canvasH)
    {
        // MapCanvas_Draw already early-outs when _data is null, but that guard doesn't flow into this
        // extracted method — re-assert it so the nullable analysis treats _data as non-null below.
        if (_data is null) return;

        EnsureHeightmapBitmap(canvasW, canvasH);
        MaybeScheduleTopDownRequest(canvasW, canvasH);

        if (_state.Mode == ViewMode.WorldOverview)
        {
            EnsureMarkerIconSet(MapCanvas);
            // Per-game icons (pre-tinted once per color scheme for the embedded set; raw atlas crops or
            // null/glyph-only otherwise) + the game, so the renderer resolves each marker's type via
            // MapMarkerCatalog and falls back to a type-distinct glyph dot when no art exists.
            var markerContext = new MarkerRenderContext(
                _data.Game, GetMarkerDrawIcons(MapCanvas, _currentColorScheme));
            // Build the standalone world water layer once per worldspace (cheap guard); used as the water
            // pass over every non-per-cell terrain layer (heightmap/vertex/slope/regions/terrain-aggregate).
            EnsureWorldWaterBitmap(_state.SelectedWorldspace?.FormId);

            // TerrainTextures aggregate LOD: when active, composite the single downscaled bitmap and
            // suppress the per-cell dict so the renderer draws the aggregate (it prefers per-cell when
            // present). Otherwise the normal single-bitmap (+ per-cell for TerrainTextures zoomed in).
            var aggActive = _currentLayer == WorldMapLayer.TerrainTextures
                && _terrainTexturesAggregateActive && _terrainAggregateBitmap is not null;
            var overviewBitmap = aggActive ? _terrainAggregateBitmap : _worldHeightmapBitmap;
            var overviewBmpW = aggActive ? _terrainAggPixelWidth : _worldHmPixelWidth;
            var overviewBmpH = aggActive ? _terrainAggPixelHeight : _worldHmPixelHeight;
            var overviewMinX = aggActive ? _terrainAggMinX : _worldHmMinX;
            var overviewMaxY = aggActive ? _terrainAggMaxY : _worldHmMaxY;
            // ONLY the TerrainTextures layer draws the per-cell tile cache. Other layers (Heightmap,
            // VertexColors, Slope, Regions) draw their single aggregate bitmap. Without the layer check,
            // a TerrainTextures session that streamed the whole worldspace left _layerCellBitmaps populated
            // (up to ~16k tiles), and every Heightmap redraw then scanned + DrawImage'd all of them — a
            // continuous UI-thread peg (confirmed via a process dump: the hot thread sat in
            // DrawTextureCellBitmaps with a 16,396-entry cache while on the Heightmap layer). aggActive
            // already implies the TerrainTextures layer, so it stays correct for the aggregate LOD too.
            var overviewCells = (_currentLayer == WorldMapLayer.TerrainTextures && !aggActive)
                ? _layerCellBitmaps
                : null;
            // Exactly one flat-water source per frame: the per-cell tile cache when the per-cell terrain
            // path is active (zoomed-in TerrainTextures), else the shared world water bitmap (aggregate +
            // every secondary single-bitmap layer). Both are suppressed when the overlay is active.
            var overviewWater = overviewCells is not null ? _layerWaterCellBitmaps : null;
            var worldWaterBmp = overviewCells is null ? _worldWaterBitmap : null;
            var overviewBmpPixelsPerCell = aggActive
                ? _terrainAggPixelsPerCell
                : WorldMapLayerRenderer.HeightmapPixelsPerCell;

            // Coarse tiles are a heightmap-family artifact (oversized worldspace). A layer switch keeps
            // them resident for a smooth transition (InvalidateWorldBitmap keepCurrentBitmap), but
            // DrawWorldOverview draws them as an EXCLUSIVE background that wins over the TerrainTextures
            // tiles — so only hand them over when they were built for the CURRENT layer. Otherwise stale
            // heightmap coarse tiles hide the terrain textures (the "toggle layers → heightmap sticks" bug).
            var coarseForLayer = _coarseTileBitmapsLayer == _currentLayer ? _coarseTileBitmaps : null;
            var coarseSpanForLayer = coarseForLayer is not null ? _coarseTileCellSpan : 0;
            var coarsePpcForLayer = coarseForLayer is not null ? _coarseTilePixelsPerCell : 0;

            // Draw-decision trace (gated): on the TerrainTextures layer, what is actually composited —
            // the aggregate, the per-cell tiles, leftover coarse tiles, or just the heightmap base. Logged
            // only when the decision changes so it pinpoints the frame the heightmap starts winning.
            if (Map2DProfilerTrace.IsEnabled && _currentLayer == WorldMapLayer.TerrainTextures)
            {
                var drawKey = $"agg={aggActive}/{overviewBitmap is not null} cells={overviewCells?.Count ?? -1} coarse={coarseForLayer?.Count ?? -1} staleCoarse={(_coarseTileBitmaps is not null && _coarseTileBitmapsLayer != _currentLayer)} hmBmp={_worldHeightmapBitmap is not null} aggBmp={_terrainAggregateBitmap is not null}";
                if (drawKey != _lastTerrainDrawLog)
                {
                    _lastTerrainDrawLog = drawKey;
                    Map2DProfilerTrace.Event("tt-draw", drawKey);
                }
            }

            WorldMapOverviewRenderer.DrawWorldOverview(
                ds, _data, GetActiveCells(), _state.FilteredMarkers, _cellGridLookup, _spatialIndex,
                overviewBitmap,
                overviewBmpW, overviewBmpH, overviewMinX, overviewMaxY, overviewBmpPixelsPerCell,
                overviewCells,
                _zoom, _panOffset, canvasW, canvasH,
                _hiddenCategories, _hideDisabledActors,
                _state.SelectedObject, _hoveredObject,
                markerContext, _currentColorScheme,
                _showCellGrid,
                _showRenderedObjects, _topDownOverlay,
                _topDownWorldMinX, _topDownWorldMaxX, _topDownWorldMinY, _topDownWorldMaxY,
                overviewWater, _showWater,
                worldWaterBmp, _worldWaterMinX, _worldWaterMaxY,
                _worldWaterPixelWidth, _worldWaterPixelHeight,
                WorldMapLayerRenderer.HeightmapPixelsPerCell,
                coarseForLayer, coarseSpanForLayer, coarsePpcForLayer);

            if (_showNavMesh)
            {
                ds.Transform = WorldMapViewportHelper.GetViewTransform(_zoom, _panOffset);
                var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
                    canvasW, canvasH, _zoom, _panOffset);
                WorldMapNavMeshOverlayRenderer.DrawWorldOverview(
                    ds, _data, GetActiveCells(), _spatialIndex, tlWorld, brWorld, _zoom);
            }

            // Dangling-REFR overlay (opt-in via DanglingRefsComboBox).
            // Drawn after overview so tinted cells overlay heightmap + cell grid.
            // Pass the active worldspace so Lucky38-interior actors don't get rendered
            // on the WastelandNV exterior map (their X/Y coords mean different things
            // in each worldspace's coordinate system).
            WorldMapDanglingRefOverlayRenderer.DrawOverlay(
                ds, _data.DanglingRefs, _data, _danglingThreshold,
                _state.SelectedWorldspace?.FormId,
                _spatialIndex,
                _zoom, _panOffset, canvasW, canvasH);
        }
        else if (_state.SelectedCell != null)
        {
            WorldMapCellDetailRenderer.DrawCellDetail(
                ds, _state.SelectedCell, _data, _cellHeightmapBitmap,
                _zoom, _panOffset, canvasW, canvasH,
                _hiddenCategories, _hideDisabledActors,
                _state.SelectedObject, _hoveredObject,
                _showRenderedObjects && IsTopDownEligible(), _topDownOverlay,
                _topDownWorldMinX, _topDownWorldMaxX, _topDownWorldMinY, _topDownWorldMaxY,
                _cellWaterBitmap, _showWater);

            if (_showNavMesh)
            {
                ds.Transform = WorldMapViewportHelper.GetViewTransform(_zoom, _panOffset);
                WorldMapNavMeshOverlayRenderer.DrawCellDetail(ds, _data, _state.SelectedCell, _zoom);
            }
        }

        // HUD (screen-space)
        ds.Transform = System.Numerics.Matrix3x2.Identity;
        ZoomLevelText.Text = $"{_zoom:P0}";
    }
}
