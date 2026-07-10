using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>PNG export of the active worldspace: single capped image or a grid of GPU-bounded tiles.</summary>
public sealed partial class WorldMapControl
{
    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data == null) return;

        var activeCells = GetActiveCells();
        var cellsWithGrid = activeCells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        if (cellsWithGrid.Count == 0) return;

        var minGx = cellsWithGrid.Min(c => c.GridX!.Value);
        var maxGx = cellsWithGrid.Max(c => c.GridX!.Value);
        var minGy = cellsWithGrid.Min(c => c.GridY!.Value);
        var maxGy = cellsWithGrid.Max(c => c.GridY!.Value);
        var cellsWide = maxGx - minGx + 1;
        var cellsTall = maxGy - minGy + 1;

        var dialog = new MapExportDialog(
            cellsWide, cellsTall,
            initialLayer: _currentLayer,
            initialIncludeMarkers: !_hiddenCategories.Contains(PlacedObjectCategory.MapMarker),
            initialIncludeNavMesh: _showNavMesh,
            initialIncludeWater: _showWater,
            initialIncludeGrid: _showCellGrid,
            initialLongEdgePx: ExportLongEdge,
            canRenderMeshes: _topDownProvider?.CanRenderTopDown == true)
        {
            XamlRoot = XamlRoot
        };

        var dialogResult = await dialog.ShowAsync();
        if (dialogResult != ContentDialogResult.Primary) return;
        var req = dialog.GetRequest();

        // Resolve output px/cell. Don't upscale beyond the layer's real source detail (132/528 for
        // texture, 132 for the 33-native heightmap-family layers).
        var maxGridDim = Math.Max(cellsWide, cellsTall);
        var maxSourcePpc = req.Layer == WorldMapLayer.TerrainTextures
            ? WorldMapLayerRenderer.MaxTexturePixelsPerCell
            : WorldMapLayerRenderer.HeightmapPixelsPerCell * 4;
        var ppc = Math.Clamp(req.LongEdgePx / maxGridDim, 1, maxSourcePpc);

        int cellsPerTile;
        if (req.Tiled)
        {
            // Split into tiles each within the GPU max-texture bound.
            cellsPerTile = Math.Max(1, WorldMapExporter.ExportMaxTileDimension / ppc);
        }
        else
        {
            // Single image: clamp px/cell so the whole worldspace fits one texture.
            ppc = Math.Min(ppc, Math.Max(1, WorldMapExporter.ExportMaxTileDimension / maxGridDim));
            cellsPerTile = maxGridDim;
        }

        var wsName = _state.SelectedWorldspace?.EditorId ?? _state.SelectedWorldspace?.FullName ?? "worldspace";
        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        picker.SuggestedFileName = $"{wsName}_map";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        EnsureMarkerIconSet(MapCanvas);

        // Apply markers preference: hidden if user unchecked Map markers in the dialog.
        var exportHiddenCategories = new HashSet<PlacedObjectCategory>(_hiddenCategories);
        if (!req.IncludeMarkers)
        {
            exportHiddenCategories.Add(PlacedObjectCategory.MapMarker);
        }
        else
        {
            exportHiddenCategories.Remove(PlacedObjectCategory.MapMarker);
        }

        var progressDialog = new ExportProgressController(XamlRoot);
        _ = progressDialog.ShowAsync();
        ExportButton.IsEnabled = false;
        try
        {
            await RunExportAsync(req, activeCells, file.Path, minGx, maxGx, minGy, maxGy,
                ppc, cellsPerTile, exportHiddenCategories, progressDialog, progressDialog.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            // User canceled — leave whatever tiles were already written.
        }
        catch (Exception ex)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn("Map export failed: {0}", ex.ToString());
        }
        finally
        {
            progressDialog.Complete();
            ExportButton.IsEnabled = true;
        }
    }

    /// <summary>
    ///     Renders the export as one capped image or a grid of GPU-bounded tiles. Decodes each tile's
    ///     terrain cells off the UI thread (bounded memory: only the tile's cells, plus a 1-cell margin
    ///     so cross-tile edge blending is seamless), uploads + composites + saves on the UI thread, and
    ///     reports progress between tiles so the modal dialog animates. Tiled runs emit
    ///     <c>{name}_r{row}_c{col}.png</c> plus a <c>{name}_manifest.json</c>.
    /// </summary>
    private async Task RunExportAsync(
        MapExportRequest req, List<CellRecord> activeCells, string basePath,
        int minGx, int maxGx, int minGy, int maxGy, int ppc, int cellsPerTile,
        HashSet<PlacedObjectCategory> hiddenCategories,
        ExportProgressController progress, CancellationToken ct)
    {
        var cols = (maxGx - minGx) / cellsPerTile + 1;
        var rows = (maxGy - minGy) / cellsPerTile + 1;
        var totalTiles = cols * rows;
        var tiled = totalTiles > 1;

        // Rendered-meshes overlay: a per-tile top-down 3D render composited over the terrain. When on,
        // the overlay bakes its own height-correct water, so the 2D terrain background renders water-FREE
        // (else water would draw twice). The overlay's showWater still honors req.IncludeWater.
        var overlayProvider = req.IncludeRenderedMeshes ? _topDownProvider : null;
        var overlayMeshes = overlayProvider?.CanRenderTopDown == true;
        var terrainShowWater = req.IncludeWater && !overlayMeshes;

        // Texture layer decodes per-tile; other layers build their single (small) 33-px/cell bitmap
        // once and reuse it for every tile (Win2D clips it to each tile's bounds).
        LandscapeTexturePalette? palette = null;
        WaterColorPalette? waterPalette = null;
        if (req.Layer == WorldMapLayer.TerrainTextures && _data is not null)
        {
            palette = LandscapeTexturePalette.GetOrCreate(_data);
            waterPalette = terrainShowWater && _state.SelectedWorldspace?.WaterFormId is uint wid
                ? WaterColorPalette.GetOrCreate(_data, wid)
                : null;
        }

        WorldMapHeightmapBuilder.HeightmapInfo? single = null;
        if (palette is null)
        {
            single = WorldMapHeightmapBuilder.Build(
                MapCanvas, activeCells, _cachedGrayscale, _cachedWaterMask,
                _cachedHmWidth, _cachedHmHeight,
                _state.SelectedWorldspace, _data,
                _currentDefaultWaterHeight, _currentColorScheme, terrainShowWater,
                req.Layer, _data?.RenderCache);
        }

        var dir = Path.GetDirectoryName(basePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var manifestTiles = new List<object>();

        try
        {
            var tileIndex = 0;
            for (var tj = 0; tj < rows; tj++)
            {
                for (var ti = 0; ti < cols; ti++)
                {
                    ct.ThrowIfCancellationRequested();
                    tileIndex++;
                    progress.Report(tiled ? "Rendering tile" : "Rendering", tileIndex, totalTiles);
                    await Task.Yield(); // let the dialog paint the new status before the heavy work

                    var tgx0 = minGx + (ti * cellsPerTile);
                    var tgx1 = Math.Min(maxGx, tgx0 + cellsPerTile - 1);
                    var tgy0 = minGy + (tj * cellsPerTile);
                    var tgy1 = Math.Min(maxGy, tgy0 + cellsPerTile - 1);
                    var tileW = (tgx1 - tgx0 + 1) * ppc;
                    var tileH = (tgy1 - tgy0 + 1) * ppc;

                    Dictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? tileBitmaps = null;
                    if (palette is not null)
                    {
                        // +1-cell margin so edge cells see their cross-tile neighbors when blending.
                        var marginCells = activeCells.Where(c =>
                            c.GridX is int gx && c.GridY is int gy &&
                            gx >= tgx0 - 1 && gx <= tgx1 + 1 && gy >= tgy0 - 1 && gy <= tgy1 + 1).ToList();
                        var cache = _data?.RenderCache;
                        var includeWater = terrainShowWater;
                        var waterHeight = _currentDefaultWaterHeight;
                        var pal = palette;
                        var wpal = waterPalette;
                        var shading = CurrentTerrainShading();
                        var perCell = await Task.Run(
                            () => WorldMapLayerRenderer.RenderTerrainTexturesPerCell(
                                marginCells, pal, waterHeight, includeWater, cache, ppc, wpal, shading), ct);
                        if (perCell is not null)
                        {
                            tileBitmaps = new Dictionary<(int, int, int), CanvasBitmap>(perCell.Count);
                            foreach (var ((gx, gy), bytes) in perCell)
                            {
                                tileBitmaps[(gx, gy, ppc)] = CanvasBitmap.CreateFromBytes(
                                    MapCanvas, bytes, ppc, ppc,
                                    Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
                            }
                        }
                    }

                    // Rendered-meshes overlay for this tile: a top-down 3D render of the tile's world rect,
                    // looped until streaming settles so the export captures the fully-loaded scene.
                    CanvasBitmap? overlayBitmap = null;
                    MapExportMeshOverlay? overlay = null;
                    if (overlayMeshes && overlayProvider is not null)
                    {
                        overlay = await RenderTileMeshOverlayAsync(
                            overlayProvider, tgx0, tgx1, tgy0, tgy1, tileW, tileH, req.IncludeWater,
                            hiddenCategories, progress, tileIndex, totalTiles, ct);
                        overlayBitmap = overlay?.Bitmap;
                    }

                    var tilePath = tiled ? Path.Combine(dir, $"{name}_r{tj}_c{ti}{ext}") : basePath;
                    try
                    {
                        await WorldMapExporter.ExportWorldspacePngAsync(
                            tilePath, tileW, tileH, ppc, tgx0, tgx1, tgy0, tgy1,
                            MapCanvas,
                            single?.Bitmap, single?.PixelWidth ?? 0, single?.PixelHeight ?? 0,
                            single?.MinX ?? 0, single?.MaxY ?? 0,
                            tileBitmaps,
                            _state.FilteredMarkers, hiddenCategories, _markerIconSet?.Icons, _currentColorScheme,
                            _data, activeCells, req.IncludeNavMesh, req.IncludeGrid,
                            overlayBitmap,
                            overlay?.WorldMinX ?? 0f, overlay?.WorldMaxX ?? 0f,
                            overlay?.WorldMinY ?? 0f, overlay?.WorldMaxY ?? 0f);
                    }
                    finally
                    {
                        overlayBitmap?.Dispose();
                        if (tileBitmaps is not null)
                        {
                            foreach (var bmp in tileBitmaps.Values) bmp.Dispose();
                        }
                    }

                    manifestTiles.Add(new
                    {
                        row = tj, col = ti, file = Path.GetFileName(tilePath),
                        gridX0 = tgx0, gridX1 = tgx1, gridY0 = tgy0, gridY1 = tgy1,
                        imageW = tileW, imageH = tileH
                    });
                }
            }

            if (tiled)
            {
                progress.Report("Writing manifest", totalTiles, totalTiles);
                var manifest = new
                {
                    layer = req.Layer.ToString(),
                    pixelsPerCell = ppc,
                    tilesWide = cols, tilesTall = rows,
                    gridX0 = minGx, gridX1 = maxGx, gridY0 = minGy, gridY1 = maxGy,
                    tiles = manifestTiles
                };
                var json = System.Text.Json.JsonSerializer.Serialize(manifest, IndentedJsonOptions);
                await File.WriteAllTextAsync(Path.Combine(dir, $"{name}_manifest.json"), json, ct);

                // The FileSavePicker creates a 0-byte placeholder at basePath ({ws}_map.png), but a
                // tiled run never writes it (tiles use {name}_r{r}_c{c}.png + a manifest). Remove the
                // stray empty file so the output folder only holds the real tiles + manifest. Best
                // effort — a leftover file must not fail an otherwise-successful export.
                try
                {
                    if (File.Exists(basePath)) File.Delete(basePath);
                }
                catch (IOException ex)
                {
                    BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                        "Map export: could not delete empty base file '{0}': {1}", basePath, ex.Message);
                }
            }
        }
        finally
        {
            single?.Bitmap.Dispose();
        }
    }

    /// <summary>A rendered-meshes overlay tile: the BGRA bitmap plus the world rect (north-Y) it covers.</summary>
    private sealed record MapExportMeshOverlay(
        CanvasBitmap Bitmap, float WorldMinX, float WorldMaxX, float WorldMinY, float WorldMaxY);

    /// <summary>
    ///     Renders one export tile's rendered-meshes overlay: a top-down 3D render of the tile's world
    ///     rect, re-requested until streaming settles (<see cref="TopDownRender.IsFullySettled" />) so the
    ///     export captures the fully-loaded scene rather than a half-streamed frame. The render dimension
    ///     is clamped internally by the provider; the bitmap is later drawn into the tile's full world
    ///     rect (upscaled if the provider capped it). Returns null on provider failure.
    /// </summary>
    private async Task<MapExportMeshOverlay?> RenderTileMeshOverlayAsync(
        ITopDownSceneRenderer provider,
        int tgx0, int tgx1, int tgy0, int tgy1, int tileW, int tileH, bool showWater,
        HashSet<PlacedObjectCategory> hiddenCategories,
        ExportProgressController progress, int tileIndex, int totalTiles, CancellationToken ct)
    {
        // One cell = _cellSize world units (8192 Morrowind, 4096 Fallout); matches WorldMapExporter so the
        // 3D overlay tile aligns with the exported terrain at the same scale.
        var worldMinX = tgx0 * _cellSize;
        var worldMaxX = (tgx1 + 1) * _cellSize;
        var worldMinY = tgy0 * _cellSize;       // world north-Y
        var worldMaxY = (tgy1 + 1) * _cellSize;

        // Re-request until streaming FULLY settles (strict gate: no submesh withheld on pending
        // textures — the loose IsComplete let tiles ship with placeholder-white leaf cards). The
        // re-renders drive streaming, so this must stay a render→check→delay loop. Wall-clock
        // time box (the old 40-iteration ≈2s cap silently accepted half-streamed tiles; heavy
        // scenes need up to ~20s) because permanently-missing textures pin the strict counter.
        TopDownRender? render = null;
        var settleTimer = System.Diagnostics.Stopwatch.StartNew();
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            render = await provider.RenderTopDownAsync(
                worldMinX, worldMaxX, worldMinY, worldMaxY, tileW, tileH,
                showDisabled: !_hideDisabledActors, showWater: showWater,
                worldspaceFormId: _state.SelectedWorldspace?.FormId,
                hiddenCategories: hiddenCategories,
                enableLighting: _hillshadeLightingEnabled, gameHour: _gameHour,
                interiorCellFormId: null, ct); // export is exterior worldspace tiles only
            if (render is null || render.IsFullySettled) break;
            if (settleTimer.Elapsed >= StreamingQuiescence.DefaultSettleTimeout)
            {
                BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                    "Map export: mesh overlay tile {0}/{1} rendered before streaming fully settled " +
                    "({2:F0}s time box; complete={3}) — some meshes/textures may be missing.",
                    tileIndex, totalTiles,
                    StreamingQuiescence.DefaultSettleTimeout.TotalSeconds, render.IsComplete);
                progress.Report(
                    $"Tile {tileIndex}/{totalTiles}: streaming timed out — exporting as-is",
                    tileIndex, totalTiles);
                break;
            }
            progress.Report($"Loading meshes (tile {tileIndex}/{totalTiles})", tileIndex, totalTiles);
            await Task.Delay(50, ct); // let background mesh/texture decode advance before re-rendering
        }

        if (render is null) return null;
        var bmp = CanvasBitmap.CreateFromBytes(
            MapCanvas, render.Bgra, render.Width, render.Height,
            Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
        return new MapExportMeshOverlay(
            bmp, render.WorldMinX, render.WorldMaxX, render.WorldMinY, render.WorldMaxY);
    }
}
