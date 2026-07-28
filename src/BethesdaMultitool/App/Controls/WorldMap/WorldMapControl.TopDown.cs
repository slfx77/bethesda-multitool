using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;

namespace BethesdaMultitool;

/// <summary>"Rendered models" top-down overlay: requests a 3D top-down render of the visible world rect
/// and composites the readback over the terrain layer in place of the dot/box markers.</summary>
public sealed partial class WorldMapControl
{
    private void RenderedObjectsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;
        _showRenderedObjects = RenderedObjectsCheckBox?.IsChecked == true
            && _topDownProvider?.CanRenderTopDown == true;
        if (_showRenderedObjects)
        {
            // Re-evaluate the viewport + kick a request on the next settle.
            _topDownContext = null;
            _topDownBoundsKey = null;
            _topDownRequestPending = true;
            _topDownLastRequestTick = 0; // first request after enabling fires immediately (no 300ms stall)
            EnsureViewportTimerRunning();
        }
        else
        {
            // Off: drop the overlay so its VRAM is reclaimed and the dot/box markers return.
            CancelTopDownOverlay();
        }
        MapCanvas?.Invalidate();
    }

    /// <summary>Enables the "Rendered models" toggle only when the 3D top-down provider is ready
    /// (D3D12 backend + terrain + reference pipeline up). Disables + unchecks it otherwise.</summary>
    private void UpdateRenderedObjectsAvailability()
    {
        if (RenderedObjectsCheckBox is null) return;
        var available = _topDownProvider?.CanRenderTopDown == true;
        RenderedObjectsCheckBox.IsEnabled = available;
        if (!available && (RenderedObjectsCheckBox.IsChecked == true || _showRenderedObjects))
        {
            RenderedObjectsCheckBox.IsChecked = false;
            _showRenderedObjects = false;
            CancelTopDownOverlay();
        }
    }

    /// <summary>Top-down sprites apply to World Overview or any CellDetail view (exterior OR interior).
    /// Interiors render through the provider's ceiling-clip path (the roof is removed so the floor plan
    /// shows) — see <see cref="ITopDownSceneRenderer.RenderTopDownAsync" /> interiorCellFormId.</summary>
    private bool IsTopDownEligible() =>
        _state.Mode == ViewMode.WorldOverview || _state.SelectedCell is not null;

    private void DisposeTopDownOverlay()
    {
        _topDownOverlay?.Dispose();
        _topDownOverlay = null;
        _topDownIncomplete = false;
    }

    /// <summary>Cancels any in-flight top-down request, drops the overlay, and resets request state.
    /// Idempotent; safe to call on teardown / toggle-off / worldspace switch.</summary>
    private void CancelTopDownOverlay()
    {
        _topDownGen++;
        _topDownRequestPending = false;
        _topDownContext = null;
        _topDownBoundsKey = null;
        _topDownLastRequestTick = 0; // next enable/request fires immediately rather than waiting out the gate
        if (_topDownCts is not null)
        {
            try { _topDownCts.Cancel(); }
            catch (ObjectDisposedException) { /* already disposed by a concurrent reset — nothing to cancel */ }
            _topDownCts.Dispose();
            _topDownCts = null;
        }
        DisposeTopDownOverlay();
    }

    /// <summary>
    ///     Cheap per-draw check: when the rendered-models overlay is on, detect whether the view
    ///     context (mode / cell / worldspace / disabled filter / eligibility) or the visible bounds
    ///     have changed enough to warrant a fresh top-down render, and if so flag a request for the
    ///     throttle timer. A context change also drops the stale overlay (its world coords no longer
    ///     apply). Mirrors the terrain viewport-key approach — no heavy work on the draw path.
    /// </summary>
    private void MaybeScheduleTopDownRequest(float canvasW, float canvasH)
    {
        if (!_showRenderedObjects || _topDownProvider?.CanRenderTopDown != true) return;

        var eligible = IsTopDownEligible();
        var ctx = (_state.Mode, _state.SelectedCell?.FormId ?? 0u,
            _state.SelectedWorldspace?.FormId ?? 0u, _hideDisabledActors, eligible);
        if (!ctx.Equals(_topDownContext))
        {
            _topDownContext = ctx;
            _topDownBoundsKey = null;
            // Context changed — the old overlay's world coords no longer match the current view.
            DisposeTopDownOverlay();
            if (eligible)
            {
                _topDownRequestPending = true;
                EnsureViewportTimerRunning();
            }
            return;
        }

        if (!eligible) return;

        var (tl, br) = WorldMapViewportHelper.GetVisibleWorldBounds(canvasW, canvasH, _zoom, _panOffset);
        // Quantum ≈ 128 screen px in world units: re-request after a pan of that much. Plus a zoom
        // bucket so a zoom change also refreshes. The 25% render margin (> the quantum) keeps the
        // edges covered between refreshes.
        var q = 128f / MathF.Max(_zoom, 1e-6f);
        var boundsKey = (
            (int)MathF.Round(MathF.Min(tl.X, br.X) / q),
            (int)MathF.Round(MathF.Min(tl.Y, br.Y) / q),
            (int)MathF.Round(MathF.Max(tl.X, br.X) / q),
            (int)MathF.Round(MathF.Max(tl.Y, br.Y) / q),
            (int)MathF.Round(MathF.Log2(MathF.Max(_zoom, 1e-6f)) * 8f));
        if (!boundsKey.Equals(_topDownBoundsKey))
        {
            _topDownBoundsKey = boundsKey;
            _topDownRequestPending = true;
            EnsureViewportTimerRunning();
        }
    }

    /// <summary>
    ///     Drops the cached "Rendered models" overlay and schedules a fresh render. Called when the
    ///     legend's category filter changes (the overlay shares that filter via RenderTopDownAsync, but
    ///     the per-draw request key doesn't track categories, so a toggle wouldn't otherwise refresh it).
    /// </summary>
    private void InvalidateTopDownOverlay()
    {
        if (!_showRenderedObjects || _topDownProvider?.CanRenderTopDown != true) return;
        DisposeTopDownOverlay();
        _topDownBoundsKey = null;
        if (IsTopDownEligible())
        {
            _topDownRequestPending = true;
            EnsureViewportTimerRunning();
        }
    }

    /// <summary>
    ///     Requests a top-down render of the visible world rect (+ a margin) from the 3D provider,
    ///     uploads the BGRA readback as a <see cref="CanvasBitmap" />, and stores it as the overlay.
    ///     Runs on the UI thread (the provider records D3D12 on the device thread and offloads only
    ///     the readback). Single-flighted via <see cref="_topDownInFlight" />; stale results (after a
    ///     teardown that bumped <see cref="_topDownGen" />) are discarded.
    /// </summary>
    private async Task RequestTopDownOverlayAsync()
    {
        var provider = _topDownProvider;
        if (provider is null || _data is null) return;
        var canvasW = (float)MapCanvas.ActualWidth;
        var canvasH = (float)MapCanvas.ActualHeight;
        if (canvasW < 1f || canvasH < 1f) return;

        _topDownInFlight = true;
        var gen = ++_topDownGen;
        CancellationToken ct;
        try
        {
            _topDownCts?.Dispose();
            _topDownCts = new CancellationTokenSource();
            ct = _topDownCts.Token;
        }
        catch (ObjectDisposedException)
        {
            _topDownInFlight = false;
            return;
        }

        var collectProfilerMetrics = Map2DProfilerTrace.IsEnabled;
        long requestId = 0;
        long requestStartTimestamp = 0;
#pragma warning disable S1854 // definite-assignment seed for the finally-block read; every reachable path overwrites it
        var outcome = "not-started";
#pragma warning restore S1854
        TopDownPixelMetrics? pixelMetrics = null;
        TopDownRender? completedRender = null;
        try
        {
            // Visible bounds are in map space (X = world X, Y = -worldNorthY). Add a margin, then
            // convert to world north-Y for the renderer.
            var (tl, br) = WorldMapViewportHelper.GetVisibleWorldBounds(canvasW, canvasH, _zoom, _panOffset);
            var mapMinX = MathF.Min(tl.X, br.X);
            var mapMaxX = MathF.Max(tl.X, br.X);
            var mapMinY = MathF.Min(tl.Y, br.Y);
            var mapMaxY = MathF.Max(tl.Y, br.Y);
            var marginX = (mapMaxX - mapMinX) * TopDownMarginFraction;
            var marginY = (mapMaxY - mapMinY) * TopDownMarginFraction;
            mapMinX -= marginX; mapMaxX += marginX;
            mapMinY -= marginY; mapMaxY += marginY;

            var worldMinX = mapMinX;
            var worldMaxX = mapMaxX;
            var worldMinY = -mapMaxY; // map Y = -worldNorthY
            var worldMaxY = -mapMinY;
            var pxW = (int)MathF.Round(canvasW * (1f + 2f * TopDownMarginFraction));
            var pxH = (int)MathF.Round(canvasH * (1f + 2f * TopDownMarginFraction));

            // A CellDetail view of an interior cell renders that interior top-down (ceiling-clipped);
            // World Overview and exterior cells render the worldspace. Interiors have no grid coords —
            // their objects are drawn at absolute world coords in cell-detail, the same frame the
            // provider renders them in, so the world rect lines up.
            var selectedCell = _state.SelectedCell;
            var interiorCellFormId = selectedCell is { IsInterior: true } ? selectedCell.FormId : (uint?)null;

            if (collectProfilerMetrics)
            {
                requestId = ++_topDownRequestSequence;
                _topDownRequestsStarted++;
                requestStartTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
                var requestDetail = string.Concat(
                    string.Create(CultureInfo.InvariantCulture,
                        $"id={requestId} gen={gen} mode={_state.Mode} cell=0x{selectedCell?.FormId ?? 0u:X8} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"interior=0x{interiorCellFormId ?? 0u:X8} ws=0x{_state.SelectedWorldspace?.FormId ?? 0u:X8} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"bounds=({worldMinX:F3},{worldMinY:F3})-({worldMaxX:F3},{worldMaxY:F3}) "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"pixels={pxW}x{pxH} showDisabled={!_hideDisabledActors} water={_showWater} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"lighting={_hillshadeLightingEnabled} hour={_gameHour:F2}"));
                Map2DProfilerTrace.Event("topdown-request-start", requestDetail);
            }

            var render = await provider.RenderTopDownAsync(
                worldMinX, worldMaxX, worldMinY, worldMaxY, pxW, pxH,
                showDisabled: !_hideDisabledActors,
                showWater: _showWater,
                worldspaceFormId: _state.SelectedWorldspace?.FormId,
                // Apply the legend's category filter to the rendered-models overlay so category
                // toggles drive the 3D meshes too (snapshot — the render awaits off the UI thread).
                hiddenCategories: new HashSet<PlacedObjectCategory>(_hiddenCategories),
                // Drive the overlay's directional lighting from the 2D map's lighting control so it
                // matches the hillshade (off ⇒ flat shade).
                enableLighting: _hillshadeLightingEnabled, gameHour: _gameHour,
                interiorCellFormId: interiorCellFormId,
                ct);

            if (gen != _topDownGen)
            {
                outcome = "superseded";
                return; // teardown / toggle off
            }
            if (render is null)
            {
                outcome = "null";
                _topDownIncomplete = false;
                return;
            }

            if (collectProfilerMetrics)
            {
                completedRender = render;
                pixelMetrics = MeasureTopDownPixels(render.Bgra, render.Width, render.Height);
            }

            var bmp = CanvasBitmap.CreateFromBytes(
                MapCanvas, render.Bgra, render.Width, render.Height,
                Windows.Graphics.DirectX.DirectXPixelFormat.B8G8R8A8UIntNormalized);
            _topDownOverlay?.Dispose();
            _topDownOverlay = bmp;
            _topDownWorldMinX = render.WorldMinX;
            _topDownWorldMaxX = render.WorldMaxX;
            _topDownWorldMinY = render.WorldMinY;
            _topDownWorldMaxY = render.WorldMaxY;
            _topDownIncomplete = !render.IsComplete;
            if (pixelMetrics is { } appliedMetrics)
            {
                _topDownOverlayRequestId = requestId;
                _topDownLastRenderComplete = render.IsComplete;
                _topDownLastRenderFullySettled = render.IsFullySettled;
                _topDownLastPixelHash = appliedMetrics.Hash;
                _topDownLastPixelCount = appliedMetrics.PixelCount;
                _topDownLastNonTransparentPixels = appliedMetrics.NonTransparentPixels;
                _topDownLastNonZeroPixels = appliedMetrics.NonZeroPixels;
                _topDownLastMeanRed = appliedMetrics.MeanRed;
                _topDownLastMeanGreen = appliedMetrics.MeanGreen;
                _topDownLastMeanBlue = appliedMetrics.MeanBlue;
                _topDownLastMeanLuma = appliedMetrics.MeanLuma;
                _topDownLastReferenceInstances = render.ReferenceInstances;
                _topDownLastReferenceDrawn = render.ReferenceDrawn;
                _topDownLastSpeedTreeBranchInstances = render.SpeedTreeBranchInstances;
                _topDownLastSpeedTreeLeafInstances = render.SpeedTreeLeafInstances;
                _topDownLastSpeedTreeBillboardInstances = render.SpeedTreeBillboardInstances;
            }
            outcome = "applied";

            if (_dumpTopDown && !_topDownDumpWritten)
            {
                _topDownDumpWritten = true;
                try
                {
                    var path = Path.Combine(Path.GetTempPath(), "fallout-map2d-topdown.png");
                    await bmp.SaveAsync(path, Microsoft.Graphics.Canvas.CanvasBitmapFileFormat.Png);
                    Map2DProfilerTrace.Event("topdown-dump", path);
                }
                catch (Exception ex) { Map2DProfilerTrace.Event("topdown-dump-error", ex.Message); }
            }

            MapCanvas.Invalidate();
        }
        catch (OperationCanceledException)
        {
            outcome = "canceled"; // request superseded by a newer top-down render — expected
        }
        catch (Exception ex)
        {
            outcome = $"error:{ex.GetType().Name}";
            Map2DProfilerTrace.Event("topdown-error", ex.Message);
        }
        finally
        {
            _topDownInFlight = false;
            if (requestId != 0)
            {
                _topDownRequestsCompleted++;
                _topDownLastCompletedRequestId = requestId;
                _topDownLastRequestDurationMs =
                    System.Diagnostics.Stopwatch.GetElapsedTime(requestStartTimestamp).TotalMilliseconds;
                var metrics = pixelMetrics;
                var metricText = metrics is { } m
                    ? string.Concat(
                        string.Create(CultureInfo.InvariantCulture,
                            $"pixelCount={m.PixelCount} nontransparent={m.NonTransparentPixels} "),
                        string.Create(CultureInfo.InvariantCulture,
                            $"nonzero={m.NonZeroPixels} meanRgb=({m.MeanRed:F3},{m.MeanGreen:F3},{m.MeanBlue:F3}) "),
                        string.Create(CultureInfo.InvariantCulture,
                            $"meanLuma={m.MeanLuma:F3} hash=0x{m.Hash:X16}"))
                    : "pixelCount=0 nontransparent=0 nonzero=0 meanRgb=(0,0,0) meanLuma=0 hash=n/a";
                var referenceText = completedRender is { } rendered
                    ? $"references={rendered.ReferenceDrawn}/{rendered.ReferenceInstances} " +
                      $"speedTree=(branch:{rendered.SpeedTreeBranchInstances},leaf:{rendered.SpeedTreeLeafInstances},billboard:{rendered.SpeedTreeBillboardInstances})"
                    : "references=n/a speedTree=n/a";
                var convergenceText = completedRender is { } convergedRender
                    ? $"complete={convergedRender.IsComplete} fullySettled={convergedRender.IsFullySettled}"
                    : "complete=n/a fullySettled=n/a";
                var completionDetail = string.Concat(
                    string.Create(CultureInfo.InvariantCulture,
                        $"id={requestId} gen={gen} outcome={outcome} durationMs={_topDownLastRequestDurationMs:F1} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"{convergenceText} settled={IsTopDownOverlaySettled()} pending={_topDownRequestPending} "),
                    string.Create(CultureInfo.InvariantCulture,
                        $"incomplete={_topDownIncomplete} {referenceText} {metricText}"));
                Map2DProfilerTrace.Event("topdown-request-complete", completionDetail);
            }

            // While the render reported streaming still in flight, keep the viewport timer alive so it
            // re-issues the next render. Single-flight + the timer ticking faster than a render
            // completes already makes these effectively back-to-back; the real load-speed lever is the
            // unthrottled per-render mesh budget (see WorldView3DControl.RenderTopDownAsync), which lets
            // each render drain the whole decode backlog — so cadence is not the bottleneck.
            if (_topDownIncomplete) EnsureViewportTimerRunning();
        }
    }

    /// <summary>True once a usable overlay is resident and no refresh/convergence work remains.</summary>
    private bool IsTopDownOverlaySettled() =>
        _showRenderedObjects
        && _topDownProvider?.CanRenderTopDown == true
        && _topDownOverlay is not null
        && !_topDownInFlight
        && !_topDownRequestPending
        && !_topDownIncomplete;

    /// <summary>Whether the resident margin-backed overlay fully covers the CURRENT visible viewport.</summary>
    private bool DoesTopDownOverlayCoverViewport()
    {
        if (_topDownOverlay is null) return false;
        var canvasW = (float)MapCanvas.ActualWidth;
        var canvasH = (float)MapCanvas.ActualHeight;
        if (canvasW < 1f || canvasH < 1f) return false;

        var (tl, br) = WorldMapViewportHelper.GetVisibleWorldBounds(canvasW, canvasH, _zoom, _panOffset);
        var visibleMinX = MathF.Min(tl.X, br.X);
        var visibleMaxX = MathF.Max(tl.X, br.X);
        var visibleWorldMinY = -MathF.Max(tl.Y, br.Y);
        var visibleWorldMaxY = -MathF.Min(tl.Y, br.Y);
        return _topDownWorldMinX <= visibleMinX
            && _topDownWorldMaxX >= visibleMaxX
            && _topDownWorldMinY <= visibleWorldMinY
            && _topDownWorldMaxY >= visibleWorldMaxY;
    }

    /// <summary>
    ///     One deterministic pass over the returned BGRA buffer. The hash is FNV-1a over packed BGRA
    ///     pixels (seeded with dimensions); color means exclude fully-transparent pixels so a clear
    ///     background cannot dilute lighting comparisons.
    /// </summary>
    private static TopDownPixelMetrics MeasureTopDownPixels(byte[] bgra, int width, int height)
    {
        const ulong fnvOffset = 14695981039346656037UL;
        const ulong fnvPrime = 1099511628211UL;
        var expectedPixels = Math.Max(0, width) * (long)Math.Max(0, height);
        var pixelCount = (int)Math.Min(int.MaxValue, Math.Min(expectedPixels, bgra.LongLength / 4L));
        var hash = fnvOffset;
        hash = unchecked((hash ^ (uint)width) * fnvPrime);
        hash = unchecked((hash ^ (uint)height) * fnvPrime);
        long sumRed = 0, sumGreen = 0, sumBlue = 0;
        var nonTransparent = 0;
        var nonZero = 0;

        for (var pixel = 0; pixel < pixelCount; pixel++)
        {
            var i = pixel * 4;
            var blue = bgra[i];
            var green = bgra[i + 1];
            var red = bgra[i + 2];
            var alpha = bgra[i + 3];
            var packed = (uint)(blue | (green << 8) | (red << 16) | (alpha << 24));
            hash = unchecked((hash ^ packed) * fnvPrime);
            if (packed != 0) nonZero++;
            if (alpha == 0) continue;
            nonTransparent++;
            sumRed += red;
            sumGreen += green;
            sumBlue += blue;
        }

        if (nonTransparent == 0)
        {
            return new TopDownPixelMetrics(
                hash, pixelCount, nonTransparent, nonZero, 0d, 0d, 0d, 0d);
        }

        var meanRed = sumRed / (double)nonTransparent;
        var meanGreen = sumGreen / (double)nonTransparent;
        var meanBlue = sumBlue / (double)nonTransparent;
        var meanLuma = 0.2126d * meanRed + 0.7152d * meanGreen + 0.0722d * meanBlue;
        return new TopDownPixelMetrics(
            hash, pixelCount, nonTransparent, nonZero, meanRed, meanGreen, meanBlue, meanLuma);
    }

    private readonly record struct TopDownPixelMetrics(
        ulong Hash,
        int PixelCount,
        int NonTransparentPixels,
        int NonZeroPixels,
        double MeanRed,
        double MeanGreen,
        double MeanBlue,
        double MeanLuma);
}
