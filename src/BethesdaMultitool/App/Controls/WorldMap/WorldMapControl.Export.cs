using System.Diagnostics;
using System.Globalization;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Core.Utils;
using BethesdaMultitool.Core.WorldData;
using Microsoft.Graphics.Canvas;

namespace BethesdaMultitool;

/// <summary>
///     PNG export of the active worldspace: single capped image or a grid of GPU-bounded tiles. The
///     options and the run trigger live in the right-panel Export tab
///     (<c>WorldMapControl.ExportPanel.cs</c>); this file is the render pipeline it drives.
/// </summary>
public sealed partial class WorldMapControl
{
    /// <summary>
    ///     Renders the export as one capped image or a grid of GPU-bounded tiles. Decodes each tile's
    ///     terrain cells off the UI thread (bounded memory: only the tile's cells, plus a 1-cell margin
    ///     so cross-tile edge blending is seamless), uploads + composites + saves on the UI thread, and
    ///     reports progress between tiles so the modal dialog animates. Tiled runs emit
    ///     <c>{name}_r{row}_c{col}.png</c> plus a <c>{name}_manifest.json</c>.
    /// </summary>
    private async Task RunExportAsync(
        MapExportRequest req,
        List<CellRecord> activeCells,
        string basePath,
        WorldMapExportPlan plan,
        HashSet<PlacedObjectCategory> hiddenCategories,
        ExportProgressController progress, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var minGx = plan.Grid.MinGridX;
        var maxGx = plan.Grid.MaxGridX;
        var minGy = plan.Grid.MinGridY;
        var maxGy = plan.Grid.MaxGridY;
        var ppc = plan.PixelsPerCell;
        var cellsPerTile = plan.CellsPerTile;
        var cols = plan.Columns;
        var rows = plan.Rows;
        var totalTiles = plan.TotalTiles;
        // Honor the explicit output contract even when the requested grid happens to fit in one
        // physical tile: checked Tiled means _r0_c0.png plus a manifest, not a silently different
        // single-PNG layout.
        var tiled = req.Tiled;

        // Resolve and validate the exact owned paths before touching output. The first successful
        // PNG publication atomically invalidates any prior owned companion manifest; a canceled or
        // failed rerun therefore never leaves a stale manifest claiming a mixed tile set is complete.
        var fullBasePath = Path.GetFullPath(basePath);
        var dir = Path.GetDirectoryName(fullBasePath);
        var name = Path.GetFileNameWithoutExtension(fullBasePath);
        if (string.IsNullOrEmpty(dir) || string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Map export requires a named file in a concrete directory.", nameof(basePath));
        }

        var ext = Path.GetExtension(fullBasePath);
        if (string.IsNullOrEmpty(ext)) ext = ".png";
        var manifestPath = Path.Combine(dir, $"{name}_manifest.json");
        basePath = fullBasePath;
        var manifestInvalidated = false;
        WorldMapExportManifestOwnership.EnsureSelectedPathIsNotAnOwnedTile(basePath);
        WorldMapExportManifestOwnership.EnsureExistingCompanionIsOwned(
            manifestPath, name, ext);

        // The existing Map2D profiler gate is captured once at process start. The normal export path
        // therefore creates no timing session, phase observer, or per-tile field set when tracing is off.
        var exportTrace = PngExportTraceSession.StartIfEnabled(
            req.Layer,
            tiled,
            totalTiles,
            cols,
            rows,
            ppc);
        var exportOutcome = "faulted";
        try
        {
            // Rendered-meshes overlay: a per-tile top-down 3D render composited over the terrain. When on,
            // the overlay bakes its own height-correct water, so the 2D terrain background renders water-FREE
            // (else water would draw twice). The overlay's showWater still honors req.IncludeWater.
            var overlayProvider = req.IncludeRenderedMeshes ? _topDownProvider : null;
            var overlayMeshes = overlayProvider?.CanRenderTopDown == true;
            var terrainShowWater = req.IncludeWater && !overlayMeshes;

            // Texture layer decodes per-tile; other layers build their single (small) 33-px/cell bitmap
            // once and reuse it for every tile (Win2D clips it to each tile's bounds).
            var sharedDataPrepStarted = exportTrace?.StartTiming() ?? 0;
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
                // Pass the SAME hillshade parameters the live map renders with — an export that
                // silently used RenderSlope's 4096-calibrated defaults would not match what the user
                // saw on screen (standing rule: capture must equal the live renderer).
                single = WorldMapHeightmapBuilder.Build(
                    MapCanvas, activeCells, _cachedGrayscale, _cachedWaterMask,
                    _cachedHmWidth, _cachedHmHeight,
                    _state.SelectedWorldspace, _data,
                    _currentDefaultWaterHeight, _currentColorScheme, terrainShowWater,
                    req.Layer, _data?.RenderCache,
                    CurrentHillshadeLightDir(), CurrentHillshadeZScale());
            }

            exportTrace?.RecordSharedDataPrep(
                sharedDataPrepStarted,
                palette is null ? "heightmap-build" : "terrain-palette",
                single?.PixelWidth,
                single?.PixelHeight);

            // Visit order is deliberately independent from output identity. Keep a physical index with
            // each entry so the manifest can retain its historical row-major ordering after traversal.
            var manifestTiles = new List<(int PhysicalIndex, object Tile)>();

            try
            {
                for (var visitOrdinal = 0; visitOrdinal < totalTiles; visitOrdinal++)
                {
                    var coordinate = ExportTileTraversal.GetSerpentineCoordinate(
                        visitOrdinal, cols, rows);
                    var tj = coordinate.Row;
                    var ti = coordinate.Column;
                    var tileIndex = visitOrdinal + 1;
                    ct.ThrowIfCancellationRequested();
                    progress.Report(tiled ? "Rendering tile" : "Rendering", tileIndex, totalTiles);
                    await Task.Yield(); // let the dialog paint the new status before the heavy work

                    var tgx0 = checked((int)((long)minGx + ((long)ti * cellsPerTile)));
                    var tgx1 = (int)Math.Min(maxGx, (long)tgx0 + cellsPerTile - 1);
                    var tgy0 = checked((int)((long)minGy + ((long)tj * cellsPerTile)));
                    var tgy1 = (int)Math.Min(maxGy, (long)tgy0 + cellsPerTile - 1);
                    var tileW = checked((tgx1 - tgx0 + 1) * ppc);
                    var tileH = checked((tgy1 - tgy0 + 1) * ppc);
                    var tileWorldBounds = plan.GetTileWorldBounds(tgx0, tgx1, tgy0, tgy1);
                    // Start after Task.Yield so dialog-paint scheduling is not charged to the tile.
                    var tileTrace = exportTrace?.BeginTile(
                        visitOrdinal,
                        tileIndex,
                        tj,
                        ti,
                        tileW,
                        tileH,
                        tgx0,
                        tgx1,
                        tgy0,
                        tgy1,
                        tiled ? "tile-png" : "single-png",
                        terrainApplicable: palette is not null,
                        overlayRequested: overlayMeshes && overlayProvider is not null);

                    try
                    {
                        Dictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? tileBitmaps = null;
                        if (palette is not null)
                        {
                            var tilePrepStarted = tileTrace?.StartTiming() ?? 0;
                            // +1-cell margin so edge cells see their cross-tile neighbors when blending.
                            var marginCells = activeCells.Where(c =>
                                c.GridX is int gx && c.GridY is int gy &&
                                (long)gx >= (long)tgx0 - 1 && (long)gx <= (long)tgx1 + 1 &&
                                (long)gy >= (long)tgy0 - 1 && (long)gy <= (long)tgy1 + 1).ToList();
                            var cache = _data?.RenderCache;
                            var includeWater = terrainShowWater;
                            var waterHeight = _currentDefaultWaterHeight;
                            var pal = palette;
                            var wpal = waterPalette;
                            var shading = CurrentTerrainShading();
                            var tilePrepWallMs = tileTrace?.ElapsedMilliseconds(tilePrepStarted);
                            double? terrainWorkerWallMs = null;
                            var terrainAwaitStarted = tileTrace?.StartTiming() ?? 0;
                            Dictionary<(int gx, int gy), byte[]>? perCell = null;
                            var terrainDataPrepOutcome = "faulted";
                            var terrainSourceBytes = 0L;
                            try
                            {
                                perCell = await Task.Run(
                                    () =>
                                    {
                                        var workerStarted = tileTrace?.StartTiming() ?? 0;
                                        try
                                        {
                                            return WorldMapLayerRenderer.RenderTerrainTexturesPerCell(
                                                marginCells, pal, waterHeight, includeWater, cache, ppc, wpal, shading);
                                        }
                                        finally
                                        {
                                            terrainWorkerWallMs = tileTrace?.ElapsedMilliseconds(workerStarted);
                                        }
                                    }, ct);
                                terrainDataPrepOutcome = "completed";
                            }
                            catch (OperationCanceledException)
                            {
                                terrainDataPrepOutcome = "canceled";
                                throw;
                            }
                            finally
                            {
                                var terrainAwaitWallMs = tileTrace?.ElapsedMilliseconds(terrainAwaitStarted);
                                terrainSourceBytes = tileTrace is null || perCell is null
                                    ? 0L
                                    : perCell.Values.Sum(static bytes => (long)bytes.Length);
                                tileTrace?.RecordTerrainDataPrep(
                                    tilePrepWallMs,
                                    terrainWorkerWallMs,
                                    terrainAwaitWallMs,
                                    marginCells.Count,
                                    perCell?.Count ?? 0,
                                    terrainSourceBytes,
                                    terrainDataPrepOutcome);
                            }

                            if (perCell is not null)
                            {
                                tileBitmaps = new Dictionary<(int, int, int), CanvasBitmap>(perCell.Count);
                                var bitmapCreateStarted = tileTrace?.StartTiming() ?? 0;
                                try
                                {
                                    foreach (var ((gx, gy), bytes) in perCell)
                                    {
                                        tileBitmaps[(gx, gy, ppc)] = CanvasBitmap.CreateFromBytes(
                                            MapCanvas, bytes, ppc, ppc,
                                            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
                                    }

                                    tileTrace?.RecordTerrainBitmapCreateSubmission(
                                        bitmapCreateStarted,
                                        tileBitmaps.Count,
                                        terrainSourceBytes,
                                        succeeded: true);
                                }
                                catch
                                {
                                    // Stop the creation/upload-submission sample before cleanup so disposal
                                    // time is not misreported as failed bitmap creation.
                                    tileTrace?.RecordTerrainBitmapCreateSubmission(
                                        bitmapCreateStarted,
                                        tileBitmaps.Count,
                                        terrainSourceBytes,
                                        succeeded: false);
                                    foreach (var bitmap in tileBitmaps.Values) bitmap.Dispose();
                                    throw;
                                }
                            }
                        }
                        else
                        {
                            tileTrace?.RecordTerrainNotApplicable();
                        }

                        // Rendered-meshes overlay for this tile: a top-down 3D render of the tile's world rect,
                        // looped until streaming settles so the export captures the fully-loaded scene.
                        CanvasBitmap? overlayBitmap = null;
                        try
                        {
                            MapExportMeshOverlay? overlay = null;
                            if (overlayMeshes && overlayProvider is not null)
                            {
                                overlay = await RenderTileMeshOverlayAsync(
                                    overlayProvider, tileWorldBounds, tileW, tileH, req.IncludeWater,
                                    hiddenCategories, progress, tileIndex, totalTiles, tileTrace, ct);
                                overlayBitmap = overlay?.Bitmap;
                            }
                            else
                            {
                                tileTrace?.RecordOverlayNotRequested();
                            }

                            var tilePath = tiled ? Path.Combine(dir, $"{name}_r{tj}_c{ti}{ext}") : basePath;
                            // Stage an owned companion out of the way only after the replacement PNG has fully
                            // encoded to its temp file. AtomicFileWriter restores it if target publication fails.
                            // This covers tiled→single and single-tile-Tiled reruns without deleting unrelated JSON.
                            if (!manifestInvalidated)
                            {
                                ct.ThrowIfCancellationRequested();
                                WorldMapExportManifestOwnership.EnsureExistingCompanionIsOwned(
                                    manifestPath, name, ext);
                            }

                            Action<WorldMapPngWriterPhaseTiming>? writerPhaseObserver = tileTrace is null
                                ? null
                                : tileTrace.ObserveWriterPhase;
                            await WorldMapExporter.ExportWorldspacePngAsync(
                                tilePath, tileW, tileH, ppc, plan.CellWorldSize,
                                tgx0, tgx1, tgy0, tgy1,
                                MapCanvas,
                                single?.Bitmap, single?.PixelWidth ?? 0, single?.PixelHeight ?? 0,
                                single?.MinX ?? 0, single?.MaxY ?? 0,
                                tileBitmaps,
                                _state.FilteredMarkers, hiddenCategories, _markerIconSet?.Icons, _currentColorScheme,
                                _data, activeCells, req.IncludeNavMesh, req.IncludeGrid,
                                overlayBitmap,
                                overlay?.WorldMinX ?? 0f, overlay?.WorldMaxX ?? 0f,
                                overlay?.WorldMinY ?? 0f, overlay?.WorldMaxY ?? 0f,
                                companionPathToInvalidate: manifestInvalidated ? null : manifestPath,
                                cancellationToken: ct,
                                phaseObserver: writerPhaseObserver);
                            manifestInvalidated = true;
                            var physicalIndex = checked((tj * cols) + ti);
                            manifestTiles.Add((physicalIndex, new
                            {
                                row = tj,
                                col = ti,
                                file = Path.GetFileName(tilePath),
                                gridX0 = tgx0,
                                gridX1 = tgx1,
                                gridY0 = tgy0,
                                gridY1 = tgy1,
                                imageW = tileW,
                                imageH = tileH
                            }));
                        }
                        finally
                        {
                            overlayBitmap?.Dispose();
                            if (tileBitmaps is not null)
                            {
                                foreach (var bmp in tileBitmaps.Values) bmp.Dispose();
                            }
                        }

                        tileTrace?.Complete("success");
                    }
                    catch (OperationCanceledException)
                    {
                        tileTrace?.Complete("canceled");
                        throw;
                    }
                    catch (Exception ex)
                    {
                        tileTrace?.Complete("faulted", ex.GetType().Name);
                        throw;
                    }
                }

                if (tiled)
                {
                    progress.Report("Writing manifest", totalTiles, totalTiles);
                    var manifest = new
                    {
                        format = WorldMapExportManifestOwnership.FormatId,
                        version = WorldMapExportManifestOwnership.CurrentVersion,
                        layer = req.Layer.ToString(),
                        pixelsPerCell = ppc,
                        tilesWide = cols,
                        tilesTall = rows,
                        gridX0 = minGx,
                        gridX1 = maxGx,
                        gridY0 = minGy,
                        gridY1 = maxGy,
                        tiles = manifestTiles
                            .OrderBy(static entry => entry.PhysicalIndex)
                            .Select(static entry => entry.Tile)
                    };
                    var manifestSerializeStarted = exportTrace?.StartTiming() ?? 0;
                    var json = System.Text.Json.JsonSerializer.Serialize(manifest, IndentedJsonOptions);
                    var manifestSerializeWallMs = exportTrace?.ElapsedMilliseconds(manifestSerializeStarted);
                    exportTrace?.RecordManifestSerialization(
                        manifestSerializeWallMs,
                        System.Text.Encoding.UTF8.GetByteCount(json));

                    // Dispose every Win2D input before the atomic publication. Once the move succeeds,
                    // no later export operation can fail and leave a manifest from a failed run.
                    single?.Bitmap.Dispose();
                    single = null;
                    Action<AtomicFileWritePhaseTiming>? manifestPhaseObserver = exportTrace is null
                        ? null
                        : exportTrace.ObserveManifestAtomicPhase;
                    await AtomicFileWriter.WriteAsync(
                        manifestPath,
                        async (temporaryPath, token) =>
                        {
                            var writeStarted = exportTrace?.StartTiming() ?? 0;
                            try
                            {
                                await File.WriteAllTextAsync(temporaryPath, json, token);
                                var writeWallMs = exportTrace?.ElapsedMilliseconds(writeStarted);
                                var manifestFileBytes = exportTrace is null
                                    ? null
                                    : PngExportTraceSession.TryGetFileLength(temporaryPath);
                                exportTrace?.RecordManifestTemporaryWrite(
                                    writeWallMs,
                                    manifestFileBytes,
                                    succeeded: true);
                            }
                            catch
                            {
                                var writeWallMs = exportTrace?.ElapsedMilliseconds(writeStarted);
                                exportTrace?.RecordManifestTemporaryWrite(
                                    writeWallMs,
                                    byteCount: null,
                                    succeeded: false);
                                throw;
                            }
                        },
                        cancellationToken: ct,
                        phaseObserver: manifestPhaseObserver);
                }
            }
            finally
            {
                single?.Bitmap.Dispose();
            }

            exportOutcome = "completed";
        }
        catch (OperationCanceledException)
        {
            exportOutcome = "canceled";
            throw;
        }
        finally
        {
            exportTrace?.Complete(exportOutcome);
        }
    }

    /// <summary>
    ///     Renders one export tile's rendered-meshes overlay: a top-down 3D render of the tile's world
    ///     rect, re-requested until streaming settles (<see cref="TopDownRender.IsFullySettled" />) so the
    ///     export captures the fully-loaded scene rather than a half-streamed frame. The render dimension
    ///     is clamped internally by the provider; the bitmap is later drawn into the tile's full world
    ///     rect (upscaled if the provider capped it). Returns null on provider failure.
    /// </summary>
    private async Task<MapExportMeshOverlay?> RenderTileMeshOverlayAsync(
        ITopDownSceneRenderer provider,
        WorldMapExportWorldBounds worldBounds,
        int tileW, int tileH, bool showWater,
        HashSet<PlacedObjectCategory> hiddenCategories,
        ExportProgressController progress, int tileIndex, int totalTiles,
        PngExportTileTrace? tileTrace, CancellationToken ct)
    {
        // One cell = _cellSize world units (8192 Morrowind, 4096 Fallout); matches WorldMapExporter so the
        // 3D overlay tile aligns with the exported terrain at the same scale.
        var worldMinX = worldBounds.MinX;
        var worldMaxX = worldBounds.MaxX;
        var worldMinY = worldBounds.MinY; // world north-Y
        var worldMaxY = worldBounds.MaxY;

        // Re-request until streaming FULLY settles (strict gate: no submesh withheld on pending
        // textures — a looser completeness check ships tiles with placeholder-white leaf cards). The
        // re-renders drive streaming, so this must stay a render→check→delay loop. Wall-clock
        // time box, not an iteration cap (which silently accepts half-streamed tiles; heavy
        // scenes need up to ~20s), because permanently-missing textures pin the strict counter.
        TopDownRender? render = null;
        var settleTimer = Stopwatch.StartNew();
        var traceSettleStarted = tileTrace?.StartTiming() ?? 0;
        var renderCallsWallMs = 0d;
        var settleDelayWallMs = 0d;
        var attempts = 0;
        var timedOut = false;
        var overlayOutcome = "faulted";
        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempts++;
                var renderCallStarted = tileTrace?.StartTiming() ?? 0;
                try
                {
                    render = await provider.RenderTopDownAsync(
                        worldMinX, worldMaxX, worldMinY, worldMaxY, tileW, tileH,
                        showDisabled: !_hideDisabledActors, showWater: showWater,
                        worldspaceFormId: _state.SelectedWorldspace?.FormId,
                        hiddenCategories: hiddenCategories,
                        enableLighting: _hillshadeLightingEnabled, gameHour: _gameHour,
                        // Export is exterior worldspace tiles only, and each tile is composited over
                        // the map's terrain layer exactly as the live overlay is.
                        interiorCellFormId: null, includeTerrainColor: false,
                        // Tiles are stitched into a world-aligned mosaic; a tilted camera would not
                        // seam.
                        projection: TopDownProjection.Straight, contentWorldZ: null,
                        trimetricYawDegrees: TrimetricViewProjBuilder.YawDegrees, ct);
                }
                finally
                {
                    renderCallsWallMs += tileTrace?.ElapsedMilliseconds(renderCallStarted) ?? 0d;
                }

                if (render is null || render.IsFullySettled) break;
                if (settleTimer.Elapsed >= StreamingQuiescence.DefaultSettleTimeout)
                {
                    timedOut = true;
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
                var delayStarted = tileTrace?.StartTiming() ?? 0;
                try
                {
                    await Task.Delay(50, ct); // let background mesh/texture decode advance before re-rendering
                }
                finally
                {
                    settleDelayWallMs += tileTrace?.ElapsedMilliseconds(delayStarted) ?? 0d;
                }
            }

            if (render is null)
            {
                overlayOutcome = "provider-null";
            }
            else
            {
                overlayOutcome = timedOut ? "timed-out" : "fully-settled";
            }
        }
        catch (OperationCanceledException)
        {
            overlayOutcome = "canceled";
            throw;
        }
        finally
        {
            tileTrace?.RecordOverlaySettlement(
                traceSettleStarted,
                renderCallsWallMs,
                settleDelayWallMs,
                attempts,
                overlayOutcome,
                timedOut,
                render?.IsComplete,
                render?.IsFullySettled,
                render?.Bgra.LongLength);
        }

        if (render is null) return null;
        var bitmapCreateStarted = tileTrace?.StartTiming() ?? 0;
        CanvasBitmap bmp;
        try
        {
            bmp = CanvasBitmap.CreateFromBytes(
                MapCanvas, render.Bgra, render.Width, render.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            tileTrace?.RecordOverlayBitmapCreateSubmission(
                bitmapCreateStarted,
                render.Bgra.LongLength,
                succeeded: true);
        }
        catch
        {
            tileTrace?.RecordOverlayBitmapCreateSubmission(
                bitmapCreateStarted,
                render.Bgra.LongLength,
                succeeded: false);
            throw;
        }

        return new MapExportMeshOverlay(
            bmp, render.WorldMinX, render.WorldMaxX, render.WorldMinY, render.WorldMaxY);
    }

    /// <summary>A rendered-meshes overlay tile: the BGRA bitmap plus the world rect (north-Y) it covers.</summary>
    private sealed record MapExportMeshOverlay(
        CanvasBitmap Bitmap,
        float WorldMinX,
        float WorldMaxX,
        float WorldMinY,
        float WorldMaxY);

    /// <summary>
    ///     Trace-only export accumulator. It is never constructed unless the existing
    ///     <c>FALLOUT_MAP2D_TRACE</c> gate is enabled, and emits only one aggregate line per tile plus
    ///     one export summary so synchronous log I/O does not distort individual phase boundaries.
    /// </summary>
    private sealed class PngExportTraceSession
    {
        private const int SchemaVersion = 1;
        private readonly int _columns;
        private readonly object _gate = new();
        private readonly WorldMapLayer _layer;
        private readonly int _pixelsPerCell;
        private readonly int _plannedTiles;
        private readonly int _rows;
        private readonly string _runId = Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly bool _tiled;
        private double _atomicPublishMoveWallMs;
        private int _canceledTiles;
        private int _companionStageMoves;
        private double _companionStageMoveWallMs;
        private bool _completed;
        private double _composeDrawWallMs;
        private int _faultedTiles;
        private long? _manifestFileBytes;
        private double? _manifestPublishMoveWallMs;
        private bool? _manifestPublishSucceeded;
        private double? _manifestSerializeWallMs;
        private bool? _manifestTemporaryWriteSucceeded;
        private double? _manifestTemporaryWriteWallMs;
        private long? _manifestUtf8Bytes;
        private int _marginCellCount;
        private int _observedTiles;
        private int _overlayAttempts;
        private double _overlayBitmapCreateWallMs;
        private double _overlayDelayWallMs;
        private double _overlayRenderCallsWallMs;
        private double _overlaySettleWallMs;
        private long _overlaySourceBytes;
        private int _publishAttempts;
        private long _publishedPngBytes;
        private int _publishedPngLengthKnownCount;
        private int _publishedPngLengthUnknownCount;
        private int _renderedCellCount;
        private double _renderTargetCreateWallMs;
        private long? _sharedBitmapPixelBytes;
        private string _sharedDataPrepKind = "not-recorded";
        private double? _sharedDataPrepWallMs;
        private int _successfulPublishes;
        private int _successfulTiles;
        private double _terrainAwaitWallMs;
        private int _terrainBitmapCount;
        private double _terrainBitmapCreateWallMs;
        private long _terrainSourceBytes;
        private double _terrainWorkerWallMs;
        private double _tilePrepWallMs;
        private double _tileTotalWallMs;
        private double _tileUnattributedWallMs;
        private double _win2DSaveWallMs;

        private PngExportTraceSession(
            WorldMapLayer layer,
            bool tiled,
            int plannedTiles,
            int columns,
            int rows,
            int pixelsPerCell)
        {
            _layer = layer;
            _tiled = tiled;
            _plannedTiles = plannedTiles;
            _columns = columns;
            _rows = rows;
            _pixelsPerCell = pixelsPerCell;
        }

        internal string RunId => _runId;
        internal WorldMapLayer Layer => _layer;
        internal bool Tiled => _tiled;
        internal int PlannedTiles => _plannedTiles;
        internal int PixelsPerCell => _pixelsPerCell;

        internal static PngExportTraceSession? StartIfEnabled(
            WorldMapLayer layer,
            bool tiled,
            int plannedTiles,
            int columns,
            int rows,
            int pixelsPerCell) =>
            Map2DProfilerTrace.IsEnabled
                ? new PngExportTraceSession(
                    layer,
                    tiled,
                    plannedTiles,
                    columns,
                    rows,
                    pixelsPerCell)
                : null;

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "Instance invocation through a null-conditional is the trace-off timestamp gate.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "SonarQube",
            "S2325:Methods should be static",
            Justification = "Instance invocation through a null-conditional is the trace-off timestamp gate.")]
        internal long StartTiming() => Stopwatch.GetTimestamp();

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "Instance invocation through a null-conditional is the trace-off timestamp gate.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "SonarQube",
            "S2325:Methods should be static",
            Justification = "Instance invocation through a null-conditional is the trace-off timestamp gate.")]
        internal double ElapsedMilliseconds(long started) =>
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        internal PngExportTileTrace BeginTile(
            int visitOrdinal,
            int tileIndex,
            int row,
            int column,
            int imageWidth,
            int imageHeight,
            int gridX0,
            int gridX1,
            int gridY0,
            int gridY1,
            string artifactKind,
            bool terrainApplicable,
            bool overlayRequested) =>
            new(
                this,
                visitOrdinal,
                tileIndex,
                row,
                column,
                imageWidth,
                imageHeight,
                gridX0,
                gridX1,
                gridY0,
                gridY1,
                artifactKind,
                terrainApplicable,
                overlayRequested);

        internal void RecordSharedDataPrep(
            long started,
            string kind,
            int? bitmapWidth,
            int? bitmapHeight)
        {
            var elapsed = ElapsedMilliseconds(started);
            long? pixelBytes = bitmapWidth is int width && bitmapHeight is int height
                ? checked((long)width * height * 4L)
                : null;
            lock (_gate)
            {
                _sharedDataPrepKind = kind;
                _sharedDataPrepWallMs = elapsed;
                _sharedBitmapPixelBytes = pixelBytes;
            }
        }

        internal void RecordManifestSerialization(double? wallMilliseconds, long utf8Bytes)
        {
            lock (_gate)
            {
                _manifestSerializeWallMs = wallMilliseconds;
                _manifestUtf8Bytes = utf8Bytes;
            }
        }

        internal void RecordManifestTemporaryWrite(
            double? wallMilliseconds,
            long? byteCount,
            bool succeeded)
        {
            lock (_gate)
            {
                _manifestTemporaryWriteWallMs = wallMilliseconds;
                _manifestTemporaryWriteSucceeded = succeeded;
                _manifestFileBytes = byteCount;
            }
        }

        internal void ObserveManifestAtomicPhase(AtomicFileWritePhaseTiming timing)
        {
            if (timing.Phase != AtomicFileWritePhase.TargetPublishMove)
            {
                return;
            }

            lock (_gate)
            {
                _manifestPublishMoveWallMs = timing.WallMilliseconds;
                _manifestPublishSucceeded = timing.Succeeded;
            }
        }

        internal void RecordTile(PngExportTileAggregate sample)
        {
            lock (_gate)
            {
                _observedTiles++;
                switch (sample.Outcome)
                {
                    case "success":
                        _successfulTiles++;
                        break;
                    case "canceled":
                        _canceledTiles++;
                        break;
                    default:
                        _faultedTiles++;
                        break;
                }

                _marginCellCount += sample.MarginCellCount;
                _renderedCellCount += sample.RenderedCellCount;
                _terrainBitmapCount += sample.TerrainBitmapCount;
                _overlayAttempts += sample.OverlayAttempts;
                _terrainSourceBytes += sample.TerrainSourceBytes;
                _overlaySourceBytes += sample.OverlaySourceBytes ?? 0L;
                _tileTotalWallMs += sample.TileTotalWallMs;
                _tileUnattributedWallMs += sample.UnattributedWallMs;
                _tilePrepWallMs += sample.TilePrepWallMs ?? 0d;
                _terrainWorkerWallMs += sample.TerrainWorkerWallMs ?? 0d;
                _terrainAwaitWallMs += sample.TerrainAwaitWallMs ?? 0d;
                _terrainBitmapCreateWallMs += sample.TerrainBitmapCreateWallMs ?? 0d;
                _overlaySettleWallMs += sample.OverlaySettleWallMs ?? 0d;
                _overlayRenderCallsWallMs += sample.OverlayRenderCallsWallMs ?? 0d;
                _overlayDelayWallMs += sample.OverlayDelayWallMs ?? 0d;
                _overlayBitmapCreateWallMs += sample.OverlayBitmapCreateWallMs ?? 0d;
                _renderTargetCreateWallMs += sample.RenderTargetCreateWallMs ?? 0d;
                _composeDrawWallMs += sample.ComposeDrawWallMs ?? 0d;
                _win2DSaveWallMs += sample.Win2DSaveWallMs ?? 0d;
                _companionStageMoveWallMs += sample.CompanionStageMoveWallMs ?? 0d;
                _atomicPublishMoveWallMs += sample.AtomicPublishMoveWallMs ?? 0d;
                if (sample.CompanionStageSucceeded)
                {
                    _companionStageMoves++;
                }

                if (sample.PublishAttempted)
                {
                    _publishAttempts++;
                }

                if (sample.PublishSucceeded)
                {
                    _successfulPublishes++;
                    if (sample.EncodedPngBytes is long encodedPngBytes)
                    {
                        _publishedPngBytes += encodedPngBytes;
                        _publishedPngLengthKnownCount++;
                    }
                    else
                    {
                        _publishedPngLengthUnknownCount++;
                    }
                }
            }
        }

        internal void Complete(string outcome)
        {
            string detail;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;

                var exportTotalWallMs = ElapsedMilliseconds(_started);
                var primaryPhaseWallMs =
                    (_sharedDataPrepWallMs ?? 0d) +
                    _tilePrepWallMs +
                    _terrainAwaitWallMs +
                    _terrainBitmapCreateWallMs +
                    _overlaySettleWallMs +
                    _overlayBitmapCreateWallMs +
                    _renderTargetCreateWallMs +
                    _composeDrawWallMs +
                    _win2DSaveWallMs +
                    _companionStageMoveWallMs +
                    _atomicPublishMoveWallMs +
                    (_manifestSerializeWallMs ?? 0d) +
                    (_manifestTemporaryWriteWallMs ?? 0d) +
                    (_manifestPublishMoveWallMs ?? 0d);
                var exportUnattributedWallMs = Math.Max(0d, exportTotalWallMs - primaryPhaseWallMs);
                long? publishedPngBytes = _publishedPngLengthUnknownCount == 0
                    ? _publishedPngBytes
                    : null;

                detail = string.Concat(
                    string.Create(CultureInfo.InvariantCulture,
                        $"schemaVersion={SchemaVersion} runId={_runId} outcome={outcome} layer={_layer} " +
                        $"tiled={_tiled} plannedTiles={_plannedTiles} observedTiles={_observedTiles} " +
                        $"successfulTiles={_successfulTiles} canceledTiles={_canceledTiles} faultedTiles={_faultedTiles} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"columns={_columns} rows={_rows} pixelsPerCell={_pixelsPerCell} " +
                        $"marginCellCount={_marginCellCount} renderedCellCount={_renderedCellCount} " +
                        $"terrainBitmapCount={_terrainBitmapCount} overlayAttempts={_overlayAttempts} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"terrainSourceBytes={_terrainSourceBytes} overlaySourceBytes={_overlaySourceBytes} " +
                        $"publishedPngBytes={FormatLong(publishedPngBytes)} " +
                        $"publishedPngLengthKnownCount={_publishedPngLengthKnownCount} " +
                        $"publishedPngLengthUnknownCount={_publishedPngLengthUnknownCount} " +
                        $"companionStageMoves={_companionStageMoves} " +
                        $"publishAttempts={_publishAttempts} successfulPublishes={_successfulPublishes} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"sharedDataPrepKind={_sharedDataPrepKind} sharedDataPrepWallMs={FormatMilliseconds(_sharedDataPrepWallMs)} " +
                        $"sharedBitmapPixelBytes={FormatLong(_sharedBitmapPixelBytes)} tileTotalWallMs={_tileTotalWallMs:F3} " +
                        $"tileUnattributedWallMs={_tileUnattributedWallMs:F3} tilePrepWallMs={_tilePrepWallMs:F3} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"terrainCpuDataPrepWorkerWallMs={_terrainWorkerWallMs:F3} terrainAwaitWallMs={_terrainAwaitWallMs:F3} " +
                        $"terrainBitmapCreateSubmitWallMs={_terrainBitmapCreateWallMs:F3} " +
                        $"overlaySettleInclusiveWallMs={_overlaySettleWallMs:F3} " +
                        $"overlayRenderCallsWallMs={_overlayRenderCallsWallMs:F3} overlayDelayWallMs={_overlayDelayWallMs:F3} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"overlayBitmapCreateSubmitWallMs={_overlayBitmapCreateWallMs:F3} " +
                        $"renderTargetCreateWallMs={_renderTargetCreateWallMs:F3} composeDrawSubmitWallMs={_composeDrawWallMs:F3} " +
                        $"win2dSaveAsyncWallMs={_win2DSaveWallMs:F3} companionStageMoveWallMs={_companionStageMoveWallMs:F3} " +
                        $"atomicPublishMoveWallMs={_atomicPublishMoveWallMs:F3} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"manifestSerializeWallMs={FormatMilliseconds(_manifestSerializeWallMs)} " +
                        $"manifestUtf8Bytes={FormatLong(_manifestUtf8Bytes)} " +
                        $"manifestTemporaryWriteWallMs={FormatMilliseconds(_manifestTemporaryWriteWallMs)} " +
                        $"manifestTemporaryWriteSucceeded={FormatBoolean(_manifestTemporaryWriteSucceeded)} " +
                        $"manifestFileBytes={FormatLong(_manifestFileBytes)} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"manifestPublishMoveWallMs={FormatMilliseconds(_manifestPublishMoveWallMs)} " +
                        $"manifestPublishSucceeded={FormatBoolean(_manifestPublishSucceeded)} " +
                        $"exportTotalWallMs={exportTotalWallMs:F3} exportUnattributedWallMs={exportUnattributedWallMs:F3}"));
            }

            Emit("png-export-summary", detail);
        }

        internal static string FormatMilliseconds(double? value) =>
            value?.ToString("F3", CultureInfo.InvariantCulture) ?? "na";

        internal static string FormatLong(long? value) =>
            value?.ToString(CultureInfo.InvariantCulture) ?? "na";

        internal static string FormatBoolean(bool? value) =>
            value?.ToString().ToLowerInvariant() ?? "na";

        internal static long? TryGetFileLength(string path)
        {
            try
            {
                return new FileInfo(path).Length;
            }
            catch
            {
                // Byte counts are trace metadata; a failed length probe cannot fail publication.
                return null;
            }
        }

        internal static void Emit(string evt, string detail)
        {
            try
            {
                Map2DProfilerTrace.Event(evt, detail);
            }
            catch
            {
                // Trace-only logging must not turn a valid export into a failed export.
            }
        }
    }

    private readonly record struct PngExportTileAggregate(
        string Outcome,
        double TileTotalWallMs,
        double UnattributedWallMs,
        double? TilePrepWallMs,
        double? TerrainWorkerWallMs,
        double? TerrainAwaitWallMs,
        double? TerrainBitmapCreateWallMs,
        double? OverlaySettleWallMs,
        double? OverlayRenderCallsWallMs,
        double? OverlayDelayWallMs,
        double? OverlayBitmapCreateWallMs,
        double? RenderTargetCreateWallMs,
        double? ComposeDrawWallMs,
        double? Win2DSaveWallMs,
        double? CompanionStageMoveWallMs,
        double? AtomicPublishMoveWallMs,
        int MarginCellCount,
        int RenderedCellCount,
        int TerrainBitmapCount,
        int OverlayAttempts,
        long TerrainSourceBytes,
        long? OverlaySourceBytes,
        long? EncodedPngBytes,
        bool CompanionStageSucceeded,
        bool PublishAttempted,
        bool PublishSucceeded);

    private sealed class PngExportTileTrace
    {
        private readonly string _artifactKind;
        private readonly int _column;
        private readonly object _gate = new();
        private readonly int _gridX0;
        private readonly int _gridX1;
        private readonly int _gridY0;
        private readonly int _gridY1;
        private readonly int _imageHeight;
        private readonly int _imageWidth;
        private readonly int _row;
        private readonly PngExportTraceSession _session;
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly int _tileIndex;
        private readonly int _visitOrdinal;
        private double? _atomicPublishMoveWallMs;
        private bool _companionStageAttempted;
        private double? _companionStageMoveWallMs;
        private bool _companionStageSucceeded;
        private bool _completed;
        private bool? _composeDrawSucceeded;
        private double? _composeDrawWallMs;
        private long? _encodedPngBytes;
        private int _marginCellCount;
        private int _overlayAttempts;
        private bool? _overlayBitmapCreateSucceeded;
        private double? _overlayBitmapCreateWallMs;
        private double? _overlayDelayWallMs;
        private bool? _overlayFinalComplete;
        private bool? _overlayFinalFullySettled;
        private string _overlayOutcome = "not-recorded";
        private double? _overlayRenderCallsWallMs;
        private bool _overlayRequested;
        private double? _overlaySettleWallMs;
        private long? _overlaySourceBytes;
        private bool _overlayTimedOut;
        private bool _publishAttempted;
        private bool _publishSucceeded;
        private int _renderedCellCount;
        private bool? _renderTargetCreateSucceeded;
        private double? _renderTargetCreateWallMs;
        private bool _terrainApplicable;
        private double? _terrainAwaitWallMs;
        private int _terrainBitmapCount;
        private bool? _terrainBitmapCreateSucceeded;
        private double? _terrainBitmapCreateWallMs;
        private string _terrainDataPrepOutcome;
        private long _terrainSourceBytes;
        private double? _terrainWorkerWallMs;
        private double? _tilePrepWallMs;
        private bool? _win2DSaveSucceeded;
        private double? _win2DSaveWallMs;

        internal PngExportTileTrace(
            PngExportTraceSession session,
            int visitOrdinal,
            int tileIndex,
            int row,
            int column,
            int imageWidth,
            int imageHeight,
            int gridX0,
            int gridX1,
            int gridY0,
            int gridY1,
            string artifactKind,
            bool terrainApplicable,
            bool overlayRequested)
        {
            _session = session;
            _visitOrdinal = visitOrdinal;
            _tileIndex = tileIndex;
            _row = row;
            _column = column;
            _imageWidth = imageWidth;
            _imageHeight = imageHeight;
            _gridX0 = gridX0;
            _gridX1 = gridX1;
            _gridY0 = gridY0;
            _gridY1 = gridY1;
            _artifactKind = artifactKind;
            _terrainApplicable = terrainApplicable;
            _terrainDataPrepOutcome = terrainApplicable ? "not-started" : "not-applicable";
            _overlayRequested = overlayRequested;
            _overlayOutcome = overlayRequested ? "not-started" : "not-requested";
        }

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "Instance invocation through a null-conditional is the trace-off timestamp gate.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "SonarQube",
            "S2325:Methods should be static",
            Justification = "Instance invocation through a null-conditional is the trace-off timestamp gate.")]
        internal long StartTiming() => Stopwatch.GetTimestamp();

        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "Performance",
            "CA1822:Mark members as static",
            Justification = "Instance invocation through a null-conditional is the trace-off timestamp gate.")]
        [System.Diagnostics.CodeAnalysis.SuppressMessage(
            "SonarQube",
            "S2325:Methods should be static",
            Justification = "Instance invocation through a null-conditional is the trace-off timestamp gate.")]
        internal double ElapsedMilliseconds(long started) =>
            Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        internal void RecordTerrainDataPrep(
            double? tilePrepWallMs,
            double? terrainWorkerWallMs,
            double? terrainAwaitWallMs,
            int marginCellCount,
            int renderedCellCount,
            long sourceBytes,
            string outcome)
        {
            lock (_gate)
            {
                _terrainApplicable = true;
                _tilePrepWallMs = tilePrepWallMs;
                _terrainWorkerWallMs = terrainWorkerWallMs;
                _terrainAwaitWallMs = terrainAwaitWallMs;
                _marginCellCount = marginCellCount;
                _renderedCellCount = renderedCellCount;
                _terrainSourceBytes = sourceBytes;
                _terrainDataPrepOutcome = outcome;
            }
        }

        internal void RecordTerrainNotApplicable()
        {
            lock (_gate)
            {
                _terrainApplicable = false;
                _terrainDataPrepOutcome = "not-applicable";
            }
        }

        internal void RecordTerrainBitmapCreateSubmission(
            long started,
            int bitmapCount,
            long sourceBytes,
            bool succeeded)
        {
            var elapsed = ElapsedMilliseconds(started);
            lock (_gate)
            {
                _terrainBitmapCreateWallMs = elapsed;
                _terrainBitmapCreateSucceeded = succeeded;
                _terrainBitmapCount = bitmapCount;
                _terrainSourceBytes = sourceBytes;
            }
        }

        internal void RecordOverlayNotRequested()
        {
            lock (_gate)
            {
                _overlayRequested = false;
                _overlayOutcome = "not-requested";
            }
        }

        internal void RecordOverlaySettlement(
            long started,
            double renderCallsWallMs,
            double delayWallMs,
            int attempts,
            string outcome,
            bool timedOut,
            bool? finalComplete,
            bool? finalFullySettled,
            long? sourceBytes)
        {
            var elapsed = ElapsedMilliseconds(started);
            lock (_gate)
            {
                _overlayRequested = true;
                _overlaySettleWallMs = elapsed;
                _overlayRenderCallsWallMs = renderCallsWallMs;
                _overlayDelayWallMs = delayWallMs;
                _overlayAttempts = attempts;
                _overlayOutcome = outcome;
                _overlayTimedOut = timedOut;
                _overlayFinalComplete = finalComplete;
                _overlayFinalFullySettled = finalFullySettled;
                _overlaySourceBytes = sourceBytes;
            }
        }

        internal void RecordOverlayBitmapCreateSubmission(
            long started,
            long sourceBytes,
            bool succeeded)
        {
            var elapsed = ElapsedMilliseconds(started);
            lock (_gate)
            {
                _overlayBitmapCreateWallMs = elapsed;
                _overlayBitmapCreateSucceeded = succeeded;
                _overlaySourceBytes = sourceBytes;
            }
        }

        internal void ObserveWriterPhase(WorldMapPngWriterPhaseTiming timing)
        {
            lock (_gate)
            {
                switch (timing.Phase)
                {
                    case WorldMapPngWriterPhase.RenderTargetCreate:
                        _renderTargetCreateWallMs = timing.WallMilliseconds;
                        _renderTargetCreateSucceeded = timing.Succeeded;
                        break;
                    case WorldMapPngWriterPhase.ComposeDrawSubmission:
                        _composeDrawWallMs = timing.WallMilliseconds;
                        _composeDrawSucceeded = timing.Succeeded;
                        break;
                    case WorldMapPngWriterPhase.Win2DSaveAsync:
                        _win2DSaveWallMs = timing.WallMilliseconds;
                        _win2DSaveSucceeded = timing.Succeeded;
                        _encodedPngBytes = timing.ByteCount;
                        break;
                    case WorldMapPngWriterPhase.CompanionStagingMove:
                        _companionStageAttempted = true;
                        _companionStageSucceeded = timing.Succeeded;
                        _companionStageMoveWallMs = timing.WallMilliseconds;
                        break;
                    case WorldMapPngWriterPhase.AtomicPublishMove:
                        _publishAttempted = true;
                        _publishSucceeded = timing.Succeeded;
                        _atomicPublishMoveWallMs = timing.WallMilliseconds;
                        break;
                }
            }
        }

        internal void Complete(string outcome, string errorType = "none")
        {
            string detail;
            PngExportTileAggregate aggregate;
            lock (_gate)
            {
                if (_completed)
                {
                    return;
                }

                _completed = true;

                var tileTotalWallMs = ElapsedMilliseconds(_started);
                // Worker time is contained by terrainAwait; render-call and delay time are contained
                // by overlaySettle. Count only the inclusive parents in the unattributed remainder.
                var primaryPhaseWallMs =
                    (_tilePrepWallMs ?? 0d) +
                    (_terrainAwaitWallMs ?? 0d) +
                    (_terrainBitmapCreateWallMs ?? 0d) +
                    (_overlaySettleWallMs ?? 0d) +
                    (_overlayBitmapCreateWallMs ?? 0d) +
                    (_renderTargetCreateWallMs ?? 0d) +
                    (_composeDrawWallMs ?? 0d) +
                    (_win2DSaveWallMs ?? 0d) +
                    (_companionStageMoveWallMs ?? 0d) +
                    (_atomicPublishMoveWallMs ?? 0d);
                var unattributedWallMs = Math.Max(0d, tileTotalWallMs - primaryPhaseWallMs);

                aggregate = new PngExportTileAggregate(
                    outcome,
                    tileTotalWallMs,
                    unattributedWallMs,
                    _tilePrepWallMs,
                    _terrainWorkerWallMs,
                    _terrainAwaitWallMs,
                    _terrainBitmapCreateWallMs,
                    _overlaySettleWallMs,
                    _overlayRenderCallsWallMs,
                    _overlayDelayWallMs,
                    _overlayBitmapCreateWallMs,
                    _renderTargetCreateWallMs,
                    _composeDrawWallMs,
                    _win2DSaveWallMs,
                    _companionStageMoveWallMs,
                    _atomicPublishMoveWallMs,
                    _marginCellCount,
                    _renderedCellCount,
                    _terrainBitmapCount,
                    _overlayAttempts,
                    _terrainSourceBytes,
                    _overlaySourceBytes,
                    _encodedPngBytes,
                    _companionStageSucceeded,
                    _publishAttempted,
                    _publishSucceeded);

                detail = string.Concat(
                    string.Create(CultureInfo.InvariantCulture,
                        $"schemaVersion=1 runId={_session.RunId} outcome={outcome} errorType={errorType} " +
                        $"artifactKind={_artifactKind} visitOrdinal={_visitOrdinal} tileIndex={_tileIndex} " +
                        $"totalTiles={_session.PlannedTiles} row={_row} col={_column} layer={_session.Layer} tiled={_session.Tiled} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"imageWidth={_imageWidth} imageHeight={_imageHeight} pixelsPerCell={_session.PixelsPerCell} " +
                        $"gridX0={_gridX0} gridX1={_gridX1} gridY0={_gridY0} gridY1={_gridY1} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"terrainApplicable={_terrainApplicable} marginCellCount={_marginCellCount} " +
                        $"terrainDataPrepOutcome={_terrainDataPrepOutcome} " +
                        $"renderedCellCount={_renderedCellCount} terrainBitmapCount={_terrainBitmapCount} " +
                        $"terrainSourceBytes={_terrainSourceBytes} tilePrepWallMs={PngExportTraceSession.FormatMilliseconds(_tilePrepWallMs)} " +
                        $"terrainCpuDataPrepWorkerWallMs={PngExportTraceSession.FormatMilliseconds(_terrainWorkerWallMs)} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"terrainAwaitWallMs={PngExportTraceSession.FormatMilliseconds(_terrainAwaitWallMs)} " +
                        $"terrainBitmapCreateSubmitWallMs={PngExportTraceSession.FormatMilliseconds(_terrainBitmapCreateWallMs)} " +
                        $"terrainBitmapCreateSucceeded={PngExportTraceSession.FormatBoolean(_terrainBitmapCreateSucceeded)} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"overlayRequested={_overlayRequested} overlayOutcome={_overlayOutcome} overlayAttempts={_overlayAttempts} " +
                        $"overlayTimedOut={_overlayTimedOut} overlayFinalComplete={PngExportTraceSession.FormatBoolean(_overlayFinalComplete)} " +
                        $"overlayFinalFullySettled={PngExportTraceSession.FormatBoolean(_overlayFinalFullySettled)} " +
                        $"overlaySourceBytes={PngExportTraceSession.FormatLong(_overlaySourceBytes)} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"overlayRenderCallsWallMs={PngExportTraceSession.FormatMilliseconds(_overlayRenderCallsWallMs)} " +
                        $"overlayDelayWallMs={PngExportTraceSession.FormatMilliseconds(_overlayDelayWallMs)} " +
                        $"overlaySettleInclusiveWallMs={PngExportTraceSession.FormatMilliseconds(_overlaySettleWallMs)} " +
                        $"overlayBitmapCreateSubmitWallMs={PngExportTraceSession.FormatMilliseconds(_overlayBitmapCreateWallMs)} " +
                        $"overlayBitmapCreateSucceeded={PngExportTraceSession.FormatBoolean(_overlayBitmapCreateSucceeded)} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"renderTargetCreateWallMs={PngExportTraceSession.FormatMilliseconds(_renderTargetCreateWallMs)} " +
                        $"renderTargetCreateSucceeded={PngExportTraceSession.FormatBoolean(_renderTargetCreateSucceeded)} " +
                        $"composeDrawSubmitWallMs={PngExportTraceSession.FormatMilliseconds(_composeDrawWallMs)} " +
                        $"composeDrawSucceeded={PngExportTraceSession.FormatBoolean(_composeDrawSucceeded)} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"win2dSaveAsyncWallMs={PngExportTraceSession.FormatMilliseconds(_win2DSaveWallMs)} " +
                        $"win2dSaveSucceeded={PngExportTraceSession.FormatBoolean(_win2DSaveSucceeded)} " +
                        $"encodedPngBytes={PngExportTraceSession.FormatLong(_encodedPngBytes)} " +
                        $"companionStageAttempted={_companionStageAttempted} companionStaged={_companionStageSucceeded} " +
                        $"companionStageMoveWallMs={PngExportTraceSession.FormatMilliseconds(_companionStageMoveWallMs)} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"atomicPublishAttempted={_publishAttempted} atomicPublishSucceeded={_publishSucceeded} " +
                        $"atomicPublishMoveWallMs={PngExportTraceSession.FormatMilliseconds(_atomicPublishMoveWallMs)} " +
                        $"tileTotalWallMs={tileTotalWallMs:F3} unattributedWallMs={unattributedWallMs:F3}"));
            }

            _session.RecordTile(aggregate);
            PngExportTraceSession.Emit("png-export-tile", detail);
        }
    }
}
