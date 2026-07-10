using System.Linq;
using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Analysis;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Microsoft.UI.Xaml;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace BethesdaMultitool;

/// <summary>
///     Toolbar "Export" button: opens <see cref="MapExport3DDialog" /> and renders the active exterior
///     worldspace as an orthographic / isometric / trimetric PNG (or a tiled grid + manifest) via the
///     offscreen path in <c>WorldView3DControl.Export3D.cs</c>. The live render loop is paused for the
///     duration so the export and the live frame don't fight over the shared command recorder.
/// </summary>
public sealed partial class WorldView3DControl
{
    private static readonly System.Text.Json.JsonSerializerOptions Export3DJsonOptions = new() { WriteIndented = true };

    // Z span fallback when a worldspace has no finite placed-object Z (top-down ignores it anyway; the
    // tilted modes need some vertical extent to frame). Matches the cell-grid's default-ish relief band.
    private const float Export3DFallbackMinZ = -8192f;
    private const float Export3DFallbackMaxZ = 32768f;

    private async void ExportButton_Click(object sender, RoutedEventArgs e)
    {
        if (_data is null) return;
        if (!CanRenderProjectionExport)
        {
            ShowStatus("3D export isn't ready yet — the renderer is still initializing.", autoDismiss: true);
            return;
        }
        if (_selectedInterior is not null)
        {
            ShowStatus("3D export covers exterior worldspaces. Switch to an exterior view first.", autoDismiss: true);
            return;
        }

        var cells = GetSelectedWorldspaceCells(_data).Cells
            .Where(c => c.GridX is int && c.GridY is int).ToList();
        if (cells.Count == 0)
        {
            ShowStatus("No exterior cells in the active worldspace to export.", autoDismiss: true);
            return;
        }

        var minGx = cells.Min(c => c.GridX!.Value);
        var maxGx = cells.Max(c => c.GridX!.Value);
        var minGy = cells.Min(c => c.GridY!.Value);
        var maxGy = cells.Max(c => c.GridY!.Value);
        var worldMinX = minGx * _cellSize;
        var worldMaxX = (maxGx + 1) * _cellSize;
        var worldMinY = minGy * _cellSize;
        var worldMaxY = (maxGy + 1) * _cellSize;
        var (worldMinZ, worldMaxZ) = ComputeGridZExtent(cells) ?? (Export3DFallbackMinZ, Export3DFallbackMaxZ);

        var dialog = new MapExport3DDialog(
            worldMinX, worldMaxX, worldMinY, worldMaxY, worldMinZ, worldMaxZ,
            Export3DMaxTileDimension, Export3DMaxImageDimension,
            showTerrain: _showTerrain, showMeshes: _showReferences, showWater: _showWater,
            showActivators: !_hiddenCategories.Contains(PlacedObjectCategory.Activator),
            showMarkers: _showMarkers, showDisabled: _showDisabled, showNavMesh: _showNavMesh,
            showCollision: _showCollision, showGrid: _showWireframe,
            enableLighting: _showLighting, enableFog: _showFog)
        {
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() != Microsoft.UI.Xaml.Controls.ContentDialogResult.Primary) return;
        var req = dialog.GetRequest();

        var hidden = new HashSet<PlacedObjectCategory>(_hiddenCategories);
        if (req.ShowActivators) hidden.Remove(PlacedObjectCategory.Activator);
        else hidden.Add(PlacedObjectCategory.Activator);

        var opts = new Export3DOptions(
            ShowTerrain: req.ShowTerrain, ShowReferences: req.ShowMeshes, ShowWater: req.ShowWater,
            ShowNavMesh: req.ShowNavMesh, ShowCollision: req.ShowCollision, ShowGrid: req.ShowGrid,
            ShowDisabled: req.ShowDisabled, ShowMarkers: req.ShowMarkers,
            EnableLighting: req.EnableLighting, EnableFog: req.EnableFog,
            HiddenCategories: hidden);

        var plan = MapExport3DPlanner.Plan(
            req.Mode, req.DirectionQuadrant, worldMinX, worldMaxX, worldMinY, worldMaxY,
            worldMinZ, worldMaxZ, req.Scale, req.Tiled, Export3DMaxTileDimension, Export3DMaxImageDimension);

        // Per-tile PNGs + manifest only when the user asked for tiles AND there is more than one
        // (a one-tile "tiled" run stays a single basePath PNG, as before). A non-tiled run whose grid
        // exceeds one tile is an internal render detail — stitched into a single PNG.
        var tiledOutput = req.Tiled && (plan.Cols * plan.Rows) > 1;

        var wsIdx = WorldspaceComboBox.SelectedIndex;
        var wsName = _data.Worldspaces is { } wss && wsIdx >= 0 && wsIdx < wss.Count
            ? wss[wsIdx].EditorId ?? wss[wsIdx].FullName ?? "worldspace"
            : "worldspace";

        var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        picker.FileTypeChoices.Add("PNG Image", [".png"]);
        picker.SuggestedFileName = $"{wsName}_3d";
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(FalloutApp.Current.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var progress = new ExportProgressController(XamlRoot);
        _ = progress.ShowAsync();
        // Pause the live render loop: the export records on the shared command recorder, which would
        // race the per-frame loop. Drain the GPU so no in-flight frame collides with the first tile.
        DetachRenderLoop();
        try
        {
            _commandRecorder12?.WaitForGpuIdle();
            await RunProjectionExportAsync(
                plan, opts, file.Path, worldMaxZ - worldMinZ, tiledOutput, progress, progress.Cts.Token);
        }
        catch (OperationCanceledException)
        {
            // User canceled — keep whatever tiles were already written.
        }
        catch (Exception ex)
        {
            Core.Diagnostics.Logger.Instance.Warn("3D export failed: {0}", ex.ToString());
        }
        finally
        {
            progress.Complete();
            AttachRenderLoop();
        }
    }

    private async Task RunProjectionExportAsync(
        Export3DPlan plan, Export3DOptions opts, string basePath, float zSpan, bool tiledOutput,
        ExportProgressController progress, CancellationToken ct)
    {
        var (basisRight, basisUp) = OrthoViewProjBuilder.CameraBasis(plan.Azimuth, plan.Elevation);
        var shadingEye = OrthoViewProjBuilder.EyePosition(plan.Focus, plan.Azimuth, plan.Elevation);
        var halfWFull = plan.OrthoHalfHeight * plan.Aspect;
        var halfHFull = plan.OrthoHalfHeight;

        var dir = Path.GetDirectoryName(basePath) ?? "";
        var name = Path.GetFileNameWithoutExtension(basePath);
        var ext = Path.GetExtension(basePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";

        var totalTiles = plan.Cols * plan.Rows;
        var manifestTiles = new List<object>();
        var tileIndex = 0;
        // Single-PNG output: render into one RGBA buffer, tile by tile (the grid is an internal
        // consequence of the per-tile GPU target cap). Pre-cleared to transparent — the same clear an
        // empty rendered frame carries — so empty tiles can simply be skipped. 16384² = 1 GiB, inside
        // the managed-array ceiling; a cancel mid-run discards the buffer without a partial PNG.
        var stitched = tiledOutput ? null : new byte[(long)plan.ImageWidth * plan.ImageHeight * 4];

        for (var tj = 0; tj < plan.Rows; tj++)
        {
            for (var ti = 0; ti < plan.Cols; ti++)
            {
                ct.ThrowIfCancellationRequested();
                tileIndex++;
                progress.Report(totalTiles > 1 ? "Rendering tile" : "Rendering", tileIndex, totalTiles);
                await Task.Yield();

                var viewProj = OrthoViewProjBuilder.BuildViewProjTile(
                    plan.Focus, plan.Azimuth, plan.Elevation, plan.OrthoHalfHeight, plan.Aspect,
                    ti, tj, plan.Cols, plan.Rows);

                // Per-tile cull: the tile's view-space center mapped back to world, with a radius covering
                // the tile diagonal + the vertical span (tilted views see geometry leaning in) + slack.
                var cxView = -halfWFull + ((ti + 0.5f) / plan.Cols * 2f * halfWFull);
                var cyView = halfHFull - ((tj + 0.5f) / plan.Rows * 2f * halfHFull);
                var tileCenter = plan.Focus + (basisRight * cxView) + (basisUp * cyView);
                var tileHalfW = halfWFull / plan.Cols;
                var tileHalfH = halfHFull / plan.Rows;
                var tileRadius = MathF.Sqrt((tileHalfW * tileHalfW) + (tileHalfH * tileHalfH))
                                 + MathF.Abs(zSpan) + (2f * _cellSize);
                var cylinder = OrthoViewProjBuilder.BuildCoverCylinder(tileCenter, tileRadius);

                // Re-request until streaming settles so the export captures the fully-loaded scene.
                const int maxIterations = 80; // ~4s/tile cap so a region with missing assets can't hang
                Export3DTile? tile = null;
                for (var it = 0; it < maxIterations; it++)
                {
                    ct.ThrowIfCancellationRequested();
                    tile = await RenderProjectionTileAsync(
                        viewProj, cylinder, basisRight, basisUp, shadingEye,
                        plan.TileWidth, plan.TileHeight, opts, ct);
                    if (tile is null || tile.IsComplete) break;
                    progress.Report($"Loading meshes (tile {tileIndex}/{totalTiles})", tileIndex, totalTiles);
                    await Task.Delay(50, ct);
                }
                if (tile is null) continue;

                // Skip empty tiles: most of a worldspace's bounding-box grid covers ocean / void with
                // nothing rendered, so the tile is a single uniform clear color. In a tiled run writing
                // those produced thousands of blank PNGs; in stitch mode the buffer is pre-cleared to
                // the same transparent clear, so the slot is already correct. A tile with ANY geometry
                // has ≥2 distinct pixels. (A 1×1 non-tiled export always saves at the end regardless —
                // even an empty frame is the result.)
                var w = tile.Width;
                var h = tile.Height;
                var bgra = tile.Bgra;
                if (stitched is { } image)
                {
                    var col = ti;
                    var row = tj;
                    // Uniform-clear scan + B↔R swap + row copy are pure CPU over a ~16 MB buffer —
                    // keep them off the UI thread.
                    await Task.Run(() =>
                    {
                        if (IsUniformTile(bgra) && bgra.Length >= 4
                            && bgra[0] == 0 && bgra[1] == 0 && bgra[2] == 0 && bgra[3] == 0)
                        {
                            return; // empty tile — the pre-cleared slot already matches
                        }
                        ExportTileStitcher.CopyTile(
                            BgraToRgba(bgra), w, h, image, plan.ImageWidth, plan.ImageHeight, col, row);
                    }, ct);
                    continue;
                }

                if (IsUniformTile(bgra))
                {
                    continue;
                }

                progress.Report("Saving tile", tileIndex, totalTiles);
                var tilePath = Path.Combine(dir, $"{name}_r{tj}_c{ti}{ext}");
                await Task.Run(() => PngWriter.SaveRgba(BgraToRgba(bgra), w, h, tilePath), ct);

                manifestTiles.Add(new
                {
                    row = tj, col = ti, file = Path.GetFileName(tilePath),
                    imageW = w, imageH = h,
                });
            }
        }

        if (stitched is { } finalImage)
        {
            // One PNG at the planned size — the picker's placeholder at basePath is overwritten.
            // Saving ~1 GiB of RGBA takes seconds even at the tuned encode settings; keep it off the
            // UI thread and report it so the wait is explained.
            progress.Report("Saving", totalTiles, totalTiles);
            await Task.Run(
                () => PngWriter.SaveRgba(finalImage, plan.ImageWidth, plan.ImageHeight, basePath), ct);
            return;
        }

        if (tiledOutput)
        {
            progress.Report("Writing manifest", totalTiles, totalTiles);
            string projectionName;
            if (plan.Elevation >= 89.5f)
            {
                projectionName = "Orthographic";
            }
            else
            {
                projectionName = plan.Elevation > 28f ? "Isometric" : "Trimetric";
            }

            var manifest = new
            {
                projection = projectionName,
                azimuthDeg = plan.Azimuth,
                elevationDeg = plan.Elevation,
                tilesWide = plan.Cols, tilesTall = plan.Rows,
                imageWidth = plan.ImageWidth, imageHeight = plan.ImageHeight,
                tileWidth = plan.TileWidth, tileHeight = plan.TileHeight,
                tiles = manifestTiles,
            };
            var json = System.Text.Json.JsonSerializer.Serialize(manifest, Export3DJsonOptions);
            await File.WriteAllTextAsync(Path.Combine(dir, $"{name}_manifest.json"), json, ct);

            // The picker created a 0-byte placeholder at basePath; a tiled run never writes it.
            try
            {
                if (File.Exists(basePath)) File.Delete(basePath);
            }
            catch (IOException ex)
            {
                Core.Diagnostics.Logger.Instance.Warn(
                    "3D export: could not delete empty base file '{0}': {1}", basePath, ex.Message);
            }
        }
    }

    /// <summary>True when every pixel in the tightly-packed BGRA buffer is identical — i.e. nothing was
    /// rendered and the tile is just the uniform clear color (an empty region of the worldspace).</summary>
    private static bool IsUniformTile(byte[] bgra)
    {
        if (bgra.Length < 8) return true;
        byte b0 = bgra[0], g0 = bgra[1], r0 = bgra[2], a0 = bgra[3];
        for (var i = 4; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i] != b0 || bgra[i + 1] != g0 || bgra[i + 2] != r0 || bgra[i + 3] != a0)
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Swaps B↔R in a tightly-packed BGRA buffer (the offscreen readback) to RGBA for
    /// <see cref="PngWriter.SaveRgba" />.</summary>
    private static byte[] BgraToRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            rgba[i] = bgra[i + 2];
            rgba[i + 1] = bgra[i + 1];
            rgba[i + 2] = bgra[i];
            rgba[i + 3] = bgra[i + 3];
        }
        return rgba;
    }
}
