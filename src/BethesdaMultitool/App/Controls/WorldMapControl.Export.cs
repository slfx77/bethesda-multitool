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
            initialLongEdgePx: ExportLongEdge)
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

        EnsureMarkerIcons(MapCanvas);

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
            BethesdaMultitool.Core.Logger.Instance.Warn("Map export failed: {0}", ex.ToString());
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

        // Texture layer decodes per-tile; other layers build their single (small) 33-px/cell bitmap
        // once and reuse it for every tile (Win2D clips it to each tile's bounds).
        LandscapeTexturePalette? palette = null;
        WaterColorPalette? waterPalette = null;
        if (req.Layer == WorldMapLayer.TerrainTextures && _data is not null)
        {
            palette = LandscapeTexturePalette.GetOrCreate(_data);
            waterPalette = req.IncludeWater && _state.SelectedWorldspace?.WaterFormId is uint wid
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
                _currentDefaultWaterHeight, _currentColorScheme, req.IncludeWater,
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
                        var includeWater = req.IncludeWater;
                        var waterHeight = _currentDefaultWaterHeight;
                        var pal = palette;
                        var wpal = waterPalette;
                        var perCell = await Task.Run(
                            () => WorldMapLayerRenderer.RenderTerrainTexturesPerCell(
                                marginCells, pal, waterHeight, includeWater, cache, ppc, wpal), ct);
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

                    var tilePath = tiled ? Path.Combine(dir, $"{name}_r{tj}_c{ti}{ext}") : basePath;
                    try
                    {
                        await WorldMapExporter.ExportWorldspacePngAsync(
                            tilePath, tileW, tileH, ppc, tgx0, tgx1, tgy0, tgy1,
                            MapCanvas,
                            single?.Bitmap, single?.PixelWidth ?? 0, single?.PixelHeight ?? 0,
                            single?.MinX ?? 0, single?.MaxY ?? 0,
                            tileBitmaps,
                            _state.FilteredMarkers, hiddenCategories, _markerIconBitmaps, _currentColorScheme,
                            _data, activeCells, req.IncludeNavMesh, req.IncludeGrid);
                    }
                    finally
                    {
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
            }
        }
        finally
        {
            single?.Bitmap.Dispose();
        }
    }
}
