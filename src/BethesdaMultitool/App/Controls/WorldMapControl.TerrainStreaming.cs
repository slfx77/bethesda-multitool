using System.Numerics;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

/// <summary>TerrainTextures live overview: viewport-throttled per-cell streaming, the throttle timer that
/// drains GPU uploads, and the zoomed-out aggregate-LOD build.</summary>
public sealed partial class WorldMapControl
{
    /// <summary>
    ///     Called from the draw callback every frame. For the live TerrainTextures overview this is
    ///     deliberately cheap: it does NOT query the spatial index, diff the cache, sort, or dispatch
    ///     a stream inline (all of which scale with visible-cell count and would stutter a zoomed-out
    ///     pan). It only compares a cheaply-computed viewport key and, when it changed, flags a
    ///     rebuild for the throttle timer (<see cref="RebuildTerrainTextureViewport" />). Other layers
    ///     keep the original synchronous single-bitmap build, gated on <see cref="_worldHeightmapDirty" />.
    /// </summary>
    private void EnsureHeightmapBitmap(float canvasW, float canvasH)
    {
        if (_data is null) return;

        var isTerrainTexturesViewport = _state.Mode == ViewMode.WorldOverview &&
            _currentLayer == WorldMapLayer.TerrainTextures;

        if (isTerrainTexturesViewport)
        {
            // Zoomed out far enough that a cell is tiny on screen → use the aggregate LOD (one
            // downscaled whole-worldspace bitmap) instead of streaming the whole worldspace per-cell.
            var screenPxPerCell = _cellSize * _zoom;
            var wantAggregate = screenPxPerCell < TerrainAggregateScreenPxThreshold && !_terrainAggregateUnavailable;
            _terrainTexturesAggregateActive = wantAggregate;

            if (wantAggregate)
            {
                var wsId = _state.SelectedWorldspace?.FormId;
                if (_terrainAggregateBitmap is null || _terrainAggregateWorldspaceFormId != wsId)
                {
                    EnsureTerrainAggregateBuild(wsId);
                }
                return;
            }

            // Per-cell hot path — cheap viewport-key compute only. The quantized key changes when the
            // visible cell-grid bounds (or resolution) change; that's the signal to schedule the
            // expensive rebuild on the timer. A pan smaller than a cell, or a pan back over the
            // same cells, leaves the key unchanged and costs nothing here.
            var key = ComputeTerrainViewportBounds(canvasW, canvasH, out _, out _, out _, out _, out _);
            if (_terrainTextureViewportKey != key)
            {
                _viewportRebuildPending = true;
                EnsureViewportTimerRunning();
            }

            return;
        }

        // Non-TerrainTextures layers build a single aggregate bitmap; per-cell cost is low so the
        // batch path is fine and the work is dirty-gated (rebuilt only on layer/data/toggle change).
        if (!_worldHeightmapDirty) return;

        var activeCells = GetActiveCells();
        if (activeCells.Count == 0) return;

        _worldHeightmapDirty = false;
        _terrainTextureViewportKey = null;

        var version = ++_worldHeightmapBuildVersion;
        ShowLayerBuildStatus($"Building {_currentLayer.DisplayName()}...", busy: true);

        var request = new LayerBuildRequest(
            version,
            activeCells.ToList(),
            _state.SelectedWorldspace,
            _data,
            _currentDefaultWaterHeight,
            _currentColorScheme,
            // ShowWater=false: live world-overview layers render water-FREE; the standalone world water
            // bitmap (_worldWaterBitmap) is the toggle-able water pass. Export keeps baking via
            // WorldMapHeightmapBuilder (a separate path).
            false,
            _currentLayer,
            _cachedGrayscale,
            _cachedWaterMask,
            _cachedHmWidth,
            _cachedHmHeight,
            _data.RenderCache,
            null,
            WorldMapLayerRenderer.TexturePixelsPerCell,
            HillshadeLightDir: CurrentHillshadeLightDir(),
            HillshadeZScale: CurrentHillshadeZScale());

        _ = BuildAndApplyWorldBitmapAsync(request);
    }

    /// <summary>
    ///     The heavy TerrainTextures viewport rebuild, run only from the throttle timer (≤~30×/s)
    ///     instead of every draw frame. Queries the spatial index, diffs the cell-bitmap cache,
    ///     sorts the to-build set urgent-first, and dispatches the background streaming build.
    /// </summary>
    private void RebuildTerrainTextureViewport(float canvasW, float canvasH)
    {
        if (_data is null) return;
        if (_state.Mode != ViewMode.WorldOverview || _currentLayer != WorldMapLayer.TerrainTextures) return;

        var activeCells = GetActiveCells();
        if (activeCells.Count == 0) return;

        var viewportRequest = BuildTerrainTextureViewportRequest(canvasW, canvasH, activeCells);
        var requestCells = viewportRequest.Cells;
        var texturePixelsPerCell = viewportRequest.PixelsPerCell;
        var terrainTextureKey = viewportRequest.Key;
        var viewportCellCount = viewportRequest.Cells.Count;

        // An empty request means the viewport is entirely off the populated cell region (mid
        // pan-stress sweep, or scrolled past the worldspace edge). That's "no viewport signal",
        // not a real resize: keep the cap and the cache as-is (the pan-back history is exactly
        // what the user is about to scroll back onto), don't cancel the trailing stream, and
        // don't dispatch a junk build.
        if (viewportCellCount == 0)
        {
            _terrainTextureViewportKey = terrainTextureKey;
            // Off-region time must not count against the cap's hold window, or a >holdMs
            // excursion would decay the cap on the first partial re-entry frame and mass-evict
            // the very pan-back history the hold protects.
            _layerCellBitmapCapPolicy.Touch(Environment.TickCount64);
            Map2DProfilerTrace.Event("viewport-rebuild",
                $"layer={_currentLayer} ppc={texturePixelsPerCell} viewport=0 cap={_layerCellBitmapCap} cacheSize={_layerCellBitmaps?.Count ?? 0} toBuild=0 kind=empty");
            HideLayerBuildStatus();
            MapCanvas.Invalidate();
            return;
        }

        // Scale the in-stream LRU cap to the current viewport BEFORE the stream starts.
        // Otherwise, with a large viewport (~6000 cells for zoomed-out WastelandNV), the
        // 256-entry default would evict cells we just rendered, in the same stream, before
        // the user ever sees them — "Loading" goes away with most of the view still blank.
        //
        // ×3 budget: 1× current viewport + 1× zoom-transition stand-ins at a stale resolution
        // + 1× pan-back history (cells the user just scrolled off-screen). The 1× pan slot
        // is what makes "pan off, pan back" a cache hit instead of a re-render — see the
        // LRU touch below. The policy adds shrink hysteresis: a transiently collapsed request
        // (sweep exiting the populated region) must not drag the cap down and mass-evict that
        // pan-back history.
        _layerCellBitmapCap = _layerCellBitmapCapPolicy.Update(viewportCellCount, Environment.TickCount64);

        // Byte clamp on top of the count cap: a tile is texturePixelsPerCell² × 4 bytes (4.5 MB at the
        // 1056 tier), so the count-only cap could retain >1 GB of GPU textures while panning zoomed in.
        // Clamp so count × current-tile-bytes stays under the resident-byte ceiling — auto-shrinks the
        // cap to ~85 at 1056 and leaves it at the full count for the small tiers (where bytes are tiny).
        var tileBytes = (long)texturePixelsPerCell * texturePixelsPerCell * 4;
        if (tileBytes > 0)
        {
            var byteCap = (int)Math.Min(int.MaxValue, Math.Max(MinLayerCellBitmapCap, MaxLayerCellResidentBytes / tileBytes));
            _layerCellBitmapCap = Math.Min(_layerCellBitmapCap, byteCap);
        }

        // Incremental rebuild for the TerrainTextures live view: keep already-built cell
        // bitmaps inside the new viewport, evict cells that scrolled off, and dispatch a
        // build only for the cells we don't have at the target resolution yet. With the
        // multi-resolution cache, old-resolution bitmaps remain as visual stand-ins
        // while target-resolution ones build — no blank during zoom transitions.
        var canReuseCache = _layerCellBitmaps is not null
            && _layerCellBitmapsLayer == _currentLayer;

        if (canReuseCache)
        {
            // Single pass over the viewport request (not the whole cache): for each requested
            // cell, either it's already cached at the target resolution (drop from the build set,
            // and LRU-touch it so it doesn't evict first) or it needs building. This replaces the
            // old full-cache `.ToList()` copy + HashSet + Remove/Add loop + `.Where().ToList()`,
            // which were all O(cache size) and ran on the UI thread (per the throttle, ≤~30×/s).
            //
            // LRU touch (Remove-then-Add to relocate to the MRU tail; the OrderedDictionary indexer
            // setter replaces IN PLACE and would NOT move the entry) only matters when the cache is
            // over cap and eviction can actually fire — `underPressure` skips the array-shuffling in
            // the common steady-state pan.
            var underPressure = _layerCellBitmaps!.Count > _layerCellBitmapCap;
            var toBuild = new List<CellRecord>(requestCells.Count);
            foreach (var c in requestCells)
            {
                if (c.GridX is not int gx || c.GridY is not int gy) continue;
                var key = (gx, gy, texturePixelsPerCell);
                if (_layerCellBitmaps.TryGetValue(key, out var bmp))
                {
                    if (underPressure)
                    {
                        _layerCellBitmaps.Remove(key);
                        _layerCellBitmaps.Add(key, bmp);
                    }
                }
                else if (!_inFlightCellKeys.ContainsKey(key))
                {
                    // Not cached AND not already queued/decoding — schedule it. Excluding in-flight
                    // keys stops successive rebuilds during a pan from re-requesting the same cells
                    // (which produced the ~3× re-decode/re-upload churn at far zoom).
                    toBuild.Add(c);
                }
            }

            requestCells = toBuild;
            _terrainTextureViewportKey = terrainTextureKey;

            Map2DProfilerTrace.Event("viewport-rebuild",
                $"layer={_currentLayer} ppc={texturePixelsPerCell} viewport={viewportCellCount} cap={_layerCellBitmapCap} cacheSize={_layerCellBitmaps.Count} toBuild={requestCells.Count}");

            if (requestCells.Count == 0)
            {
                // Viewport changed but no new cells need building — common when panning
                // by less than a cell or when reversing direction back over cached cells.
                HideLayerBuildStatus();
                MapCanvas.Invalidate();
                return;
            }
        }
        else
        {
            // Layer / resolution / worldspace changed: replace the whole cache.
            _terrainTextureViewportKey = terrainTextureKey;
            Map2DProfilerTrace.Event("viewport-rebuild",
                $"layer={_currentLayer} ppc={texturePixelsPerCell} viewport={viewportCellCount} cap={_layerCellBitmapCap} cacheSize=0 toBuild={requestCells.Count} kind=full");
        }

        if (LandscapeTexturePalette.GetOrCreate(_data) is not { } palette || requestCells.Count == 0)
        {
            return;
        }

        var version = ++_worldHeightmapBuildVersion;
        ShowLayerBuildStatus($"Building {_currentLayer.DisplayName()}...", busy: true);

        // Urgent-first ordering: sort by squared distance from viewport center so
        // the parallel worker pool picks central cells before margin/preload cells.
        // Cesium's Preload priority tier — Urgent (visible) drains before Normal/Preload.
        var (vpTl, vpBr) = WorldMapViewportHelper.GetVisibleWorldBounds(canvasW, canvasH, _zoom, _panOffset);
        var centerX = (vpTl.X + vpBr.X) * 0.5f;
        var centerY = (vpTl.Y + vpBr.Y) * 0.5f;
        requestCells.Sort((a, b) =>
        {
            var ax = (a.GridX!.Value + 0.5f) * _cellSize - centerX;
            var ay = -(a.GridY!.Value + 0.5f) * _cellSize - centerY;
            var bx = (b.GridX!.Value + 0.5f) * _cellSize - centerX;
            var by = -(b.GridY!.Value + 0.5f) * _cellSize - centerY;
            return (ax * ax + ay * ay).CompareTo(bx * bx + by * by);
        });

        // Cancel-only; the old stream disposes its own CTS once it unwinds (see the disposal
        // hop in StreamAndApplyTerrainTexturesAsync) — disposing here raced its registrations.
        _terrainStreamCts?.Cancel();
        var streamCts = new CancellationTokenSource();
        _terrainStreamCts = streamCts;
        // Capture cache-gen on the UI thread BEFORE the stream starts. Worker threads read
        // this value when enqueuing apply tasks; if cache-gen bumps after the stream begins
        // (worldspace switch, layer change), the captured generation goes stale and the
        // applies drop on the ApplyOne side. Per-stream `version` is kept for status-banner
        // messages but no longer controls apply-drop — pan-induced rebuilds are common
        // and would needlessly kill in-flight applies whose cache target is still valid.
        var cacheGen = _layerCellBitmapsCacheGen;
        Map2DProfilerTrace.Event("stream-start",
            $"v={version} cacheGen={cacheGen} ppc={texturePixelsPerCell} requested={requestCells.Count}");
        Interlocked.Increment(ref _activeTerrainStreams);
        _ = StreamAndApplyTerrainTexturesAsync(
            version, cacheGen, requestCells, palette, texturePixelsPerCell, CurrentTerrainShading(), streamCts);
    }

    /// <summary>Lazily creates and starts the viewport-rebuild throttle timer (UI thread only).</summary>
    private void EnsureViewportTimerRunning()
    {
        if (_viewportRebuildTimer is null)
        {
            _viewportRebuildTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(ViewportRebuildThrottleMs)
            };
            _viewportRebuildTimer.Tick += ViewportRebuildTimer_Tick;
        }

        if (!_viewportRebuildTimer.IsEnabled) _viewportRebuildTimer.Start();
    }

    private void StopViewportTimer() => _viewportRebuildTimer?.Stop();

    /// <summary>
    ///     Single timer tick: drain a bounded batch of completed cell bitmaps onto the GPU, then run
    ///     at most one throttled viewport rebuild. Self-stops when there's nothing left to do so we
    ///     don't keep a ~30 Hz wakeup alive forever.
    /// </summary>
    private void ViewportRebuildTimer_Tick(object? sender, object e)
    {
        // After unload the DispatcherQueue goes null; there's no UI left to update.
        if (DispatcherQueue is null)
        {
            StopViewportTimer();
            return;
        }

        // Diagnostic: this tick runs on the UI thread, so a long tick IS a visible stall/freeze.
        // Time it and emit a `slow-tick` trace when it exceeds the budget — unambiguous (unlike a
        // draw-gap, which idle pauses also produce) for attributing a freeze to the streaming path.
        var tickStart = System.Diagnostics.Stopwatch.GetTimestamp();

        // Same rationale as MapCanvas_Draw: a throw here (e.g. a per-cell GPU upload in
        // DrainPendingCellApplies, or a viewport rebuild) otherwise escapes to App.UnhandledException
        // and kills the window. Log with context + skip this tick; the timer fires again next interval.
        try
        {
            DrainPendingCellApplies();
            ApplyKeyboardPan();

            // While a zoom gesture is settling, hold off the rebuild (keep pending set) so we don't
            // re-stream throwaway intermediate zoom levels. Each wheel notch re-arms _zoomSettleTicks.
            if (_zoomSettleTicks > 0)
            {
                _zoomSettleTicks--;
            }
            else if (_viewportRebuildPending
                && _state.Mode == ViewMode.WorldOverview
                && _currentLayer == WorldMapLayer.TerrainTextures)
            {
                _viewportRebuildPending = false;
                RebuildTerrainTextureViewport((float)MapCanvas.ActualWidth, (float)MapCanvas.ActualHeight);
            }

            // Rendered-models overlay: request a fresh top-down render once the view settles (not mid
            // pan/zoom), or keep re-requesting while the last render was still streaming in. Gated to
            // TopDownMinIntervalMs so the incomplete-streaming re-request loop fires at a throttled
            // cadence (~3/sec) instead of every ~30 Hz tick — each request is a full scene render +
            // readback + CanvasBitmap upload, which otherwise starves pan/zoom (bug: overlay crawl).
            //
            // Defer the request while 2D terrain textures are still streaming/draining: a top-down
            // render is a full D3D12 scene render + readback + CanvasBitmap upload on the UI thread,
            // and firing it amid an active terrain stream starves the per-cell uploads (DrainPending
            // CellApplies, run first in this tick) — the reported "terrain re-renders very slowly when
            // rendered models is on". The overlay simply waits for the stream to drain; the idle
            // self-stop below keeps the timer alive while _topDownIncomplete, so nothing is dropped.
            if (_showRenderedObjects
                && _zoomSettleTicks == 0
                && !_isPanning
                && !_topDownInFlight
                && Volatile.Read(ref _activeTerrainStreams) == 0
                && _pendingCellApplies.IsEmpty
                && _topDownProvider?.CanRenderTopDown == true
                && (_topDownRequestPending || _topDownIncomplete)
                && IsTopDownEligible()
                && Environment.TickCount64 - _topDownLastRequestTick >= TopDownMinIntervalMs)
            {
                _topDownRequestPending = false;
                _topDownLastRequestTick = Environment.TickCount64;
                _ = RequestTopDownOverlayAsync();
            }
        }
        catch (Exception ex)
        {
            LogUiThreadFault("ViewportRebuildTimer_Tick", ex);
        }

        var tickMs = System.Diagnostics.Stopwatch.GetElapsedTime(tickStart).TotalMilliseconds;
        if (tickMs >= 40)
        {
            Map2DProfilerTrace.Event("slow-tick",
                $"ms={tickMs:F0} layer={_currentLayer} cacheSize={_layerCellBitmaps?.Count ?? 0} cap={_layerCellBitmapCap} ppc-tier={ChooseTerrainTexturePixelsPerCell(_zoom)}");
        }

        // Idle: no pending rebuild, not zoom-settling, not actively panning, nothing left to apply,
        // no stream still producing cells, and no top-down overlay work outstanding. Stop until the
        // next viewport change / pointer event.
        if (!_viewportRebuildPending
            && _zoomSettleTicks == 0
            && !_isPanning
            && _panKeysDown.Count == 0
            && _pendingCellApplies.IsEmpty
            && Volatile.Read(ref _activeTerrainStreams) == 0
            && !(_showRenderedObjects && (_topDownInFlight || _topDownRequestPending || _topDownIncomplete)))
        {
            StopViewportTimer();
        }
    }

    /// <summary>
    ///     Drains completed cells from <see cref="_pendingCellApplies" />. Genuine GPU uploads are
    ///     capped at <see cref="MaxCellAppliesPerTick" /> per tick so a burst can't starve input;
    ///     but cells that are stale (cache replaced) or ALREADY present at the target resolution are
    ///     skipped cheaply and do NOT count against the budget. Skipping already-cached cells is what
    ///     stops the "constant refresh" churn: overlapping streams re-produce cells the viewport
    ///     already shows, and re-uploading them (dispose + CreateFromBytes + invalidate) every frame
    ///     was the wasted work observed after the image was already complete. One coalesced
    ///     invalidate at the end, only if something new was actually uploaded.
    /// </summary>
    private void DrainPendingCellApplies()
    {
        var uploads = 0;
        long uploadBytes = 0;
        var applied = false;
        while (_pendingCellApplies.TryDequeue(out var item))
        {
            var key = (item.cell.GridX, item.cell.GridY, item.cell.PixelsPerCell);
            _inFlightCellKeys.TryRemove(key, out _);

            // Stale generation (layer/worldspace switch) — drop cheaply.
            if (item.cacheGen != _layerCellBitmapsCacheGen) continue;

            // Already displayed at this resolution — the bitmap would be byte-identical, so skip the
            // redundant dispose+upload+invalidate. Cheap skip, uncapped, so a large duplicate backlog
            // clears in one tick and the timer can idle instead of looping.
            if (_layerCellBitmaps is not null && _layerCellBitmaps.ContainsKey(key)) continue;

            ApplyOneTerrainTextureCell(item.cacheGen, item.cell);
            applied = true;
            // Stop on EITHER the count cap OR the byte budget. Without the byte budget a tick of big
            // (1056²≈4.5 MB) cells dumps >200 MB of synchronous GPU upload and freezes the UI on a zoom
            // transition; the byte check paces large tiers to ~5 cells/tick while small tiers still drain
            // 48/tick. The just-applied cell always counts, so progress is guaranteed (no starvation).
            var ppc = item.cell.PixelsPerCell;
            uploadBytes += (long)ppc * ppc * 4;
            if (item.cell.Water is not null) uploadBytes += (long)ppc * ppc * 4;
            if (++uploads >= MaxCellAppliesPerTick || uploadBytes >= MaxCellApplyBytesPerTick) break;
        }

        if (applied) MapCanvas.Invalidate();
    }

    /// <summary>
    ///     Consumes the per-cell streaming output from
    ///     <see cref="WorldMapLayerRenderer.StreamTerrainTexturesPerCell" /> and buffers each
    ///     completed cell into <see cref="_pendingCellApplies" />. The throttle timer drains the
    ///     buffer at a bounded rate (<see cref="DrainPendingCellApplies" />), so a burst of
    ///     thousands of completed cells can't flood the UI thread with GPU uploads and starve
    ///     pointer input. Cells still appear progressively, just paced.
    /// </summary>
    private async Task StreamAndApplyTerrainTexturesAsync(
        int version,
        int cacheGen,
        List<CellRecord> requestCells,
        LandscapeTexturePalette palette,
        int pixelsPerCell,
        TerrainShadingOptions shading,
        CancellationTokenSource streamCts)
    {
        var ct = streamCts.Token;
        var requestedCount = requestCells.Count;
        var producedCount = 0;
        try
        {
            await foreach (var cell in WorldMapLayerRenderer.StreamTerrainTexturesPerCell(
                    requestCells,
                    palette,
                    _currentDefaultWaterHeight,
                    _data?.RenderCache,
                    pixelsPerCell,
                    _currentWaterPalette,
                    shading,
                    ct).WithCancellation(ct).ConfigureAwait(false))
            {
                producedCount++;
                // Buffer for the throttle timer to drain on the UI thread (bounded per tick).
                // Enqueue is lock-free and never blocks the worker; the captured cacheGen lets a
                // stale apply (from before a cache replacement) be dropped at drain time. Mark the
                // key in-flight so concurrent rebuilds don't re-request a cell already queued here.
                _inFlightCellKeys[(cell.GridX, cell.GridY, cell.PixelsPerCell)] = 0;
                _pendingCellApplies.Enqueue((cacheGen, cell));
            }

            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Info(
                "TerrainTextures stream v{0} complete: requested={1}, produced={2}, skipped-by-worker={3}",
                version, requestedCount, producedCount, requestedCount - producedCount);

            Map2DProfilerTrace.Event("stream-end",
                $"v={version} cacheGen={cacheGen} requested={requestedCount} produced={producedCount}");

            var doneQueue = DispatcherQueue;
            doneQueue?.TryEnqueue(() =>
            {
                if (version == _worldHeightmapBuildVersion) HideLayerBuildStatus();
            });
        }
        catch (OperationCanceledException)
        {
            // Expected on viewport-change cancellation. Drop silently.
        }
        catch (Exception ex)
        {
            // Log the full exception (including AggregateException inner) to the diagnostic
            // file. The visible banner has limited width and can clip long stack traces.
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                "TerrainTextures streaming failed: {0}", ex.ToString());
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (version != _worldHeightmapBuildVersion) return;
                ShowLayerBuildStatus(
                    $"{WorldMapLayer.TerrainTextures.DisplayName()} failed: {ex.Message}",
                    busy: false);
                MapCanvas.Invalidate();
            });
        }
        finally
        {
            Interlocked.Decrement(ref _activeTerrainStreams);

            // The stream owns its CTS: dispose it on the UI thread AFTER all awaited work has
            // completed (no token registrations remain). The UI side only ever calls Cancel() —
            // disposing inline there raced the live registrations on these worker threads
            // (CancellationTokenSource.Dispose is not safe concurrent with Register/Cancel).
            // The UI hop serializes the dispose against the UI's Cancel, and clearing the field
            // first means CancelTerrainStream can never see a disposed CTS.
            var doneDispatcher = DispatcherQueue;
            if (doneDispatcher is null || !doneDispatcher.TryEnqueue(() =>
                {
                    if (ReferenceEquals(_terrainStreamCts, streamCts))
                    {
                        _terrainStreamCts = null;
                    }

                    streamCts.Dispose();
                }))
            {
                // Dispatcher gone (control torn down): no UI thread can Cancel anymore either,
                // so disposing here is race-free.
                streamCts.Dispose();
            }
        }
    }

    /// <summary>
    ///     UI-thread per-cell apply: turn the streamed RGBA bytes into a device-bound
    ///     <see cref="CanvasBitmap" />, slot it into the cell-bitmap cache (overwriting any
    ///     prior bitmap at the same key), and invalidate the canvas. Win2D coalesces
    ///     back-to-back invalidations so this scales to streaming many cells per frame.
    ///     <para>
    ///         Drop check uses <see cref="_layerCellBitmapsCacheGen" /> (bumped only on cache
    ///         replacement) instead of <see cref="_worldHeightmapBuildVersion" /> (bumped per
    ///         viewport rebuild). During a fast pan, the per-stream version increments on
    ///         every viewport change, and old applies queued behind newer ones would all drop
    ///         their work even though the cache they were targeting is still alive and valid.
    ///         Cache-gen only fires the drop when an apply would actually corrupt the cache
    ///         (different layer / cleared dict / worldspace switch).
    ///     </para>
    /// </summary>
    private void ApplyOneTerrainTextureCell(int cacheGen, WorldMapLayerRenderer.TerrainTextureCellResult cell)
    {
        if (cacheGen != _layerCellBitmapsCacheGen)
        {
            Map2DProfilerTrace.Event("cache-drop-by-gen",
                $"cell=({cell.GridX},{cell.GridY},{cell.PixelsPerCell}) applyGen={cacheGen} currentGen={_layerCellBitmapsCacheGen}");
            return;
        }

        var key = (cell.GridX, cell.GridY, cell.PixelsPerCell);
        _layerCellBitmaps ??= new OrderedDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>();
        var replacedStale = _layerCellBitmaps.TryGetValue(key, out var stale);
        if (replacedStale)
        {
            stale!.Dispose();
            // Remove so the re-add lands at the tail (LRU MRU end). The indexer setter on
            // OrderedDictionary preserves position and would leave a replaced entry near the
            // dict head, where it would be evicted first instead of last.
            _layerCellBitmaps.Remove(key);
        }
        _layerCellBitmaps.Add(key, CanvasBitmap.CreateFromBytes(
            MapCanvas,
            cell.Pixels,
            cell.PixelsPerCell,
            cell.PixelsPerCell,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized));
        _layerCellBitmapsLayer = WorldMapLayer.TerrainTextures;

        // Water tile travels in lockstep with the terrain tile under the SAME key (it is a subset —
        // dry cells carry no tile). Premultiplied bytes → default alpha mode, so source-over over the
        // water-free terrain reproduces the old baked overlay exactly.
        if (_layerWaterCellBitmaps is not null && _layerWaterCellBitmaps.TryGetValue(key, out var staleWater))
        {
            staleWater.Dispose();
            _layerWaterCellBitmaps.Remove(key);
        }
        if (cell.Water is { } waterBytes)
        {
            _layerWaterCellBitmaps ??= new OrderedDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>();
            _layerWaterCellBitmaps.Add(key, CanvasBitmap.CreateFromBytes(
                MapCanvas,
                waterBytes,
                cell.PixelsPerCell,
                cell.PixelsPerCell,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized));
        }

        Map2DProfilerTrace.Event("cache-add",
            $"cell=({cell.GridX},{cell.GridY},{cell.PixelsPerCell}) replaced={replacedStale} dictSize={_layerCellBitmaps.Count} cap={_layerCellBitmapCap}");

        // Memory cap. Once we exceed the soft limit, drop the LRU head — guaranteed to be the
        // genuinely oldest entry because OrderedDictionary preserves insertion order strictly
        // (see _layerCellBitmaps doc-comment for the pre-2026-06-05 Dictionary bug this fixed).
        // viewport-leave already evicts most of the stale ones in EnsureHeightmapBitmap; this
        // cap is the safety net for resolutions retained across zoom thresholds plus the
        // viewport×3 budget for pan-back stand-ins.
        while (_layerCellBitmaps.Count > _layerCellBitmapCap)
        {
            var oldest = _layerCellBitmaps.GetAt(0);
            oldest.Value.Dispose();
            _layerCellBitmaps.RemoveAt(0);
            // Evict the water tile for the same key (if any) so the water cache can never outgrow the
            // terrain cache — it's a subset keyed identically.
            if (_layerWaterCellBitmaps is not null && _layerWaterCellBitmaps.TryGetValue(oldest.Key, out var evictWater))
            {
                evictWater.Dispose();
                _layerWaterCellBitmaps.Remove(oldest.Key);
            }
            Map2DProfilerTrace.Event("cache-evict",
                $"cell=({oldest.Key.gx},{oldest.Key.gy},{oldest.Key.pixelsPerCell}) reason=cap dictSize={_layerCellBitmaps.Count} cap={_layerCellBitmapCap}");
        }

        // Canvas invalidation is hoisted to DrainPendingCellApplies (one per drained batch)
        // so a 48-cell tick coalesces into a single redraw instead of 48.
    }

    /// <summary>
    ///     Computes the quantized TerrainTextures viewport key and its world-space bounds (with the
    ///     pan-direction preload bias) WITHOUT touching the spatial index. Shared by the cheap
    ///     per-frame key compare in <see cref="EnsureHeightmapBitmap" /> and the heavy
    ///     <see cref="BuildTerrainTextureViewportRequest" /> so both derive the key from identical
    ///     math — if they diverged, rebuilds would fire spuriously or never.
    /// </summary>
    private TerrainTextureViewportKey ComputeTerrainViewportBounds(
        float canvasW, float canvasH,
        out float minCanvasX, out float maxCanvasX, out float minCanvasY, out float maxCanvasY,
        out int pixelsPerCell)
    {
        pixelsPerCell = ChooseTerrainTexturePixelsPerCell(_zoom);
        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasW, canvasH, _zoom, _panOffset);

        // Preload-direction bias. Cesium "Preload" priority tier: extend the viewport
        // margin in the direction of pan motion so cells about to scroll into view are
        // already decoded by the time they arrive. Screen-space pan velocity is positive
        // when the user drags the map content right/down, which means new content is
        // entering from the LEFT/TOP — extend the margin on the opposite side from velocity.
        // World Y is inverted from screen Y, so the Y bias gets flipped too.
        const int baseMarginCells = 1;
        const int preloadMarginCells = 3;
        const float velocityThresholdPx = 6f;
        var marginWorld = _cellSize * baseMarginCells;
        var preloadWorld = _cellSize * preloadMarginCells;

        var biasLeft = _panVelocity.X > velocityThresholdPx ? preloadWorld : marginWorld;
        var biasRight = _panVelocity.X < -velocityThresholdPx ? preloadWorld : marginWorld;
        var biasTopWorld = _panVelocity.Y > velocityThresholdPx ? preloadWorld : marginWorld;
        var biasBottomWorld = _panVelocity.Y < -velocityThresholdPx ? preloadWorld : marginWorld;

        minCanvasX = Math.Min(tlWorld.X, brWorld.X) - biasLeft;
        maxCanvasX = Math.Max(tlWorld.X, brWorld.X) + biasRight;
        // World Y grows northward, so the screen-top edge is the WORLD MAX-Y and screen-bottom is WORLD MIN-Y.
        minCanvasY = Math.Min(tlWorld.Y, brWorld.Y) - biasBottomWorld;
        maxCanvasY = Math.Max(tlWorld.Y, brWorld.Y) + biasTopWorld;

        var minGx = (int)MathF.Floor(minCanvasX / _cellSize);
        var maxGx = (int)MathF.Floor(maxCanvasX / _cellSize);
        var minGy = (int)MathF.Floor(-maxCanvasY / _cellSize);
        var maxGy = (int)MathF.Floor(-minCanvasY / _cellSize);
        return new TerrainTextureViewportKey(minGx, maxGx, minGy, maxGy, pixelsPerCell);
    }

    private TerrainTextureViewportRequest BuildTerrainTextureViewportRequest(
        float canvasW, float canvasH, List<CellRecord> activeCells)
    {
        var key = ComputeTerrainViewportBounds(
            canvasW, canvasH,
            out var minCanvasX, out var maxCanvasX, out var minCanvasY, out var maxCanvasY,
            out var pixelsPerCell);

        var visibleCells = new List<CellRecord>();
        if (_spatialIndex is not null)
        {
            _spatialIndex.QueryCellsInViewport(
                new Vector2(minCanvasX, minCanvasY),
                new Vector2(maxCanvasX, maxCanvasY),
                visibleCells);
        }
        else
        {
            foreach (var cell in activeCells)
            {
                if (cell.GridX is not int gx || cell.GridY is not int gy)
                {
                    continue;
                }

                if (gx >= key.MinGx && gx <= key.MaxGx && gy >= key.MinGy && gy <= key.MaxGy)
                {
                    visibleCells.Add(cell);
                }
            }
        }

        // An empty result is a valid signal (viewport entirely off the populated region);
        // RebuildTerrainTextureViewport treats it as "no viewport" and keeps cap/cache/stream
        // untouched. The old Take(1) fallback here dispatched a junk 1-cell off-screen build
        // and canceled the live stream at every pan-stress sweep boundary.
        return new TerrainTextureViewportRequest(key, visibleCells, pixelsPerCell);
    }

    private int ChooseTerrainTexturePixelsPerCell(float zoom)
    {
        var screenPixelsPerCell = _cellSize * zoom;

        // Low tiers (33/66) for the aggregate→per-cell transition zone: rendering cells at ~display
        // resolution keeps minification to ≤~1.4× (vs ~5× at a fixed 132), which — with the
        // HighQualityCubic composite — removes the moire there, and is cheaper (smaller tiles). The
        // 132/264/528 upper tiers (zoomed-in detail) are unchanged.
        if (screenPixelsPerCell <= WorldMapLayerRenderer.HeightmapPixelsPerCell)
        {
            return WorldMapLayerRenderer.HeightmapPixelsPerCell; // 33
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.HeightmapPixelsPerCell * 2)
        {
            return WorldMapLayerRenderer.HeightmapPixelsPerCell * 2; // 66
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.TexturePixelsPerCell * 1.25f)
        {
            return WorldMapLayerRenderer.TexturePixelsPerCell; // 132
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.TexturePixelsPerCell * 2.5f)
        {
            return WorldMapLayerRenderer.TexturePixelsPerCell * 2; // 264
        }

        if (screenPixelsPerCell <= WorldMapLayerRenderer.TexturePixelsPerCell * 5f)
        {
            return WorldMapLayerRenderer.TexturePixelsPerCell * 4; // 528
        }

        // Zoomed in past ~660 screen px/cell: render at 1056 so the composite carries ~132 px per
        // tile-repeat (sampled from the 256 mip, downscaled) instead of magnifying the 528 tier. Few
        // cells are visible at this zoom, so the larger per-cell bitmaps stay memory-bounded.
        return WorldMapLayerRenderer.MaxTexturePixelsPerCell; // 1056
    }

    /// <summary>
    ///     Kicks a background build of the whole-worldspace TerrainTextures aggregate (one downscaled
    ///     bitmap), used as the zoomed-out LOD. Built once per worldspace + cached; on completion the
    ///     bitmap is composited via the single-bitmap path. If the worldspace is too large for one
    ///     bitmap, flags <see cref="_terrainAggregateUnavailable" /> so the view falls back to per-cell.
    /// </summary>
    private void EnsureTerrainAggregateBuild(uint? worldspaceFormId)
    {
        if (_terrainAggregateBuilding || _data is null) return;
        var activeCells = GetActiveCells();
        if (activeCells.Count == 0) return;
        if (LandscapeTexturePalette.GetOrCreate(_data) is not { } palette) return;

        _terrainAggregateBuilding = true;
        var version = ++_terrainAggregateVersion;
        ShowLayerBuildStatus("Building terrain overview...", busy: true);

        var request = new LayerBuildRequest(
            version,
            activeCells.ToList(),
            _state.SelectedWorldspace,
            _data,
            _currentDefaultWaterHeight,
            _currentColorScheme,
            // ShowWater=false: the zoomed-out terrain aggregate renders water-FREE; the world water
            // bitmap is drawn over it (scaled) as the toggle-able water pass.
            false,
            WorldMapLayer.TerrainTextures,
            _cachedGrayscale,
            _cachedWaterMask,
            _cachedHmWidth,
            _cachedHmHeight,
            _data.RenderCache,
            palette,
            WorldMapLayerRenderer.TexturePixelsPerCell,
            PreferAggregate: true,
            WaterPalette: _showWater ? _currentWaterPalette : null,
            TerrainShading: CurrentTerrainShading());

        _ = BuildTerrainAggregateAsync(request, worldspaceFormId);
    }

    private async Task BuildTerrainAggregateAsync(LayerBuildRequest request, uint? worldspaceFormId)
    {
        try
        {
            var result = await WorldLayerBuildService.BuildAsync(request).ConfigureAwait(false);
            _ = DispatcherQueue.TryEnqueue(() => ApplyTerrainAggregateResult(result, worldspaceFormId));
        }
        catch (Exception ex)
        {
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn("Terrain aggregate build failed: {0}", ex.ToString());
            _ = DispatcherQueue.TryEnqueue(() =>
            {
                _terrainAggregateBuilding = false;
                HideLayerBuildStatus();
            });
        }
    }

    private void ApplyTerrainAggregateResult(LayerBuildResult result, uint? worldspaceFormId)
    {
        _terrainAggregateBuilding = false;
        if (result.Version != _terrainAggregateVersion) return; // superseded by a newer aggregate build

        if (result.Bitmap is { } bmp)
        {
            _terrainAggregateBitmap?.Dispose();
            _terrainAggregateBitmap = CanvasBitmap.CreateFromBytes(
                MapCanvas, bmp.Pixels, bmp.Width, bmp.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            _terrainAggMinX = bmp.MinCellX;
            _terrainAggMaxY = bmp.MaxCellY;
            _terrainAggPixelWidth = bmp.Width;
            _terrainAggPixelHeight = bmp.Height;
            // CellPixelsPerCell carries the aggregate's adaptive px/cell (< 33 for large worldspaces) —
            // the composite scales by CellWorldSize / this so the bitmap lands on the right world rect.
            _terrainAggPixelsPerCell = result.CellPixelsPerCell > 0
                ? result.CellPixelsPerCell
                : WorldMapLayerRenderer.HeightmapPixelsPerCell;
            _terrainAggregateWorldspaceFormId = worldspaceFormId;
            _terrainAggregateUnavailable = false;
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Info(
                "TerrainTextures aggregate LOD built: {0}x{1}px @ {2}px/cell ws=0x{3:X8}",
                bmp.Width, bmp.Height, _terrainAggPixelsPerCell, worldspaceFormId ?? 0);
        }
        else
        {
            // Worldspace too large for a single aggregate bitmap — fall back to per-cell streaming.
            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Info(
                "TerrainTextures aggregate LOD unavailable (worldspace too large) — using per-cell.");
            _terrainAggregateUnavailable = true;
            _terrainTexturesAggregateActive = false;
            _viewportRebuildPending = true;
            EnsureViewportTimerRunning();
        }

        HideLayerBuildStatus();
        MapCanvas.Invalidate();
    }
}
