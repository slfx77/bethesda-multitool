using System.Diagnostics;
using BethesdaMultitool;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

/// <summary>
///     Shared live-verification support for top-down scenarios. Every capture is taken from one
///     UI-thread snapshot, and every mutation must produce a newer request ID before it can pass.
/// </summary>
internal abstract class TopDownScenarioBase : Map2DScenario
{
    protected static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(60);
    protected static readonly TimeSpan ConvergenceTimeout = TimeSpan.FromMinutes(3);

    private readonly string _scenarioName;

    protected TopDownScenarioBase(string scenarioName)
    {
        _scenarioName = scenarioName;
    }

    protected async Task PrepareExteriorAsync(WorldMapControl control, DispatcherQueue queue)
    {
        await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
        var cellSize = await UiAsync(queue, () => control.Profiler_CellWorldSize);
        var zoom = 180f / MathF.Max(cellSize, 1f);
        await UiAsync(queue, () => control.Profiler_CenterOnActiveCells(zoom));
        Logger.Instance.Info(
            "Scenario({0}): TerrainTextures centered at {1:F6} zoom ({2:F0} world units/cell).",
            _scenarioName, zoom, cellSize);
        await WaitForProviderAndEnableAsync(control, queue);
    }

    protected async Task WaitForProviderAndEnableAsync(WorldMapControl control, DispatcherQueue queue)
    {
        await WaitForAsync(
            control, queue,
            snapshot => snapshot.ProviderReady,
            ProviderTimeout,
            "3D top-down provider readiness");

        // Route through the real checkbox handler. Top-down scenarios imply --rendered-models, but
        // doing this here also makes them independent of MainWindow's readiness-poll cadence.
        await UiAsync(queue, () => control.Profiler_ShowRenderedObjects = true);
    }

    protected static bool IsConverged(TopDownProfilerSnapshot snapshot) =>
        snapshot.ProviderReady
        && snapshot.Enabled
        && snapshot.OverlayPresent
        && snapshot.CoversViewport
        && snapshot.RenderComplete
        && snapshot.Settled;

    protected void AssertNonempty(TopDownProfilerSnapshot snapshot, string phase)
    {
        if (snapshot.PixelCount <= 0
            || snapshot.NonTransparentPixels <= 0
            || snapshot.NonZeroPixels <= 0)
        {
            throw new InvalidOperationException(
                $"{_scenarioName}: {phase} settled to an empty readback. {Describe(snapshot)}");
        }
    }

    protected void AssertReferenceGeometry(TopDownProfilerSnapshot snapshot, string phase)
    {
        if (snapshot.ReferenceInstances <= 0 || snapshot.ReferenceDrawn <= 0)
        {
            throw new InvalidOperationException(
                $"{_scenarioName}: {phase} contained no drawn reference geometry " +
                $"({snapshot.ReferenceDrawn}/{snapshot.ReferenceInstances}). {Describe(snapshot)}");
        }
    }

    protected void AssertStableGeometryCoverage(
        TopDownProfilerSnapshot expected,
        TopDownProfilerSnapshot actual,
        string phase)
    {
        if (actual.PixelCount != expected.PixelCount)
        {
            throw new InvalidOperationException(
                $"{_scenarioName}: {phase} changed readback dimensions/pixel count " +
                $"({expected.PixelCount} -> {actual.PixelCount}). {Describe(actual)}");
        }

        var coverageDelta = Math.Abs(actual.NonTransparentPixels - expected.NonTransparentPixels);
        var coverageTolerance = Math.Max(8, (int)Math.Ceiling(expected.NonTransparentPixels * 0.0025));
        if (coverageDelta > coverageTolerance)
        {
            throw new InvalidOperationException(
                $"{_scenarioName}: {phase} changed geometry coverage by {coverageDelta} pixels; " +
                $"tolerance={coverageTolerance}, expected={expected.NonTransparentPixels}, " +
                $"actual={actual.NonTransparentPixels}. {Describe(actual)}");
        }

        if (actual.ReferenceInstances != expected.ReferenceInstances
            || actual.ReferenceDrawn != expected.ReferenceDrawn)
        {
            throw new InvalidOperationException(
                $"{_scenarioName}: {phase} changed reference geometry counts " +
                $"({expected.ReferenceDrawn}/{expected.ReferenceInstances} -> " +
                $"{actual.ReferenceDrawn}/{actual.ReferenceInstances}). {Describe(actual)}");
        }
    }

    protected void AssertMaterialColorChange(
        TopDownProfilerSnapshot expected,
        TopDownProfilerSnapshot actual,
        string phase)
    {
        if (actual.PixelHash == expected.PixelHash)
        {
            throw new InvalidOperationException(
                $"{_scenarioName}: {phase} produced the same pixel hash " +
                $"(0x{actual.PixelHash:X16}). {Describe(actual)}");
        }

        var lumaDelta = Math.Abs(actual.MeanLuma - expected.MeanLuma);
        var materialDelta = Math.Max(0.5d, Math.Abs(expected.MeanLuma) * 0.005d);
        if (lumaDelta < materialDelta)
        {
            throw new InvalidOperationException(
                $"{_scenarioName}: {phase} changed the hash but not mean luma materially " +
                $"(delta={lumaDelta:F3}, required={materialDelta:F3}, " +
                $"{expected.MeanLuma:F3} -> {actual.MeanLuma:F3}). {Describe(actual)}");
        }
    }

    protected async Task<TopDownProfilerSnapshot> WaitForNewConvergedRenderAsync(
        WorldMapControl control,
        DispatcherQueue queue,
        TopDownProfilerSnapshot baseline,
        string description)
    {
        await WaitForAsync(
            control, queue,
            snapshot => snapshot.RequestsStarted > baseline.RequestsStarted,
            TimeSpan.FromSeconds(30),
            $"{description} request start");

        return await WaitForQuiescentAsync(
            control, queue,
            snapshot => IsConverged(snapshot)
                && snapshot.OverlayRequestId > baseline.OverlayRequestId,
            ConvergenceTimeout,
            $"{description} convergence");
    }

    protected async Task<TopDownProfilerSnapshot> WaitForAsync(
        WorldMapControl control,
        DispatcherQueue queue,
        Func<TopDownProfilerSnapshot, bool> predicate,
        TimeSpan timeout,
        string description)
    {
        var watch = Stopwatch.StartNew();
        var nextProgress = TimeSpan.FromSeconds(5);
        var last = await SnapshotAsync(control, queue);
        while (!predicate(last) && watch.Elapsed < timeout)
        {
            await Task.Delay(200);
            last = await SnapshotAsync(control, queue);
            if (watch.Elapsed >= nextProgress)
            {
                Logger.Instance.Info(
                    "Scenario({0}): waiting for {1} ({2:F0}s): {3}",
                    _scenarioName, description, watch.Elapsed.TotalSeconds, Describe(last));
                nextProgress += TimeSpan.FromSeconds(5);
            }
        }

        if (!predicate(last))
        {
            throw new TimeoutException(
                $"{_scenarioName}: timed out after {timeout.TotalSeconds:F0}s waiting for {description}. " +
                Describe(last));
        }
        return last;
    }

    /// <summary>
    ///     A request may complete just before its invalidate-driven draw discovers the final bounds key.
    ///     Require an additional idle window so scenarios do not mistake that normal warm-up refresh for
    ///     post-settlement cache churn.
    /// </summary>
    protected async Task<TopDownProfilerSnapshot> WaitForQuiescentAsync(
        WorldMapControl control,
        DispatcherQueue queue,
        Func<TopDownProfilerSnapshot, bool> predicate,
        TimeSpan timeout,
        string description)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < timeout)
        {
            var remaining = timeout - watch.Elapsed;
            var candidate = await WaitForAsync(control, queue, predicate, remaining, description);
            await Task.Delay(750);
            var confirmed = await SnapshotAsync(control, queue);
            if (predicate(confirmed) && confirmed.RequestsStarted == candidate.RequestsStarted)
            {
                return confirmed;
            }
        }

        var last = await SnapshotAsync(control, queue);
        throw new TimeoutException(
            $"{_scenarioName}: timed out after {timeout.TotalSeconds:F0}s waiting for quiescent " +
            $"{description}. {Describe(last)}");
    }

    protected static Task<TopDownProfilerSnapshot> SnapshotAsync(
        WorldMapControl control,
        DispatcherQueue queue) =>
        UiAsync(queue, control.Profiler_TopDownSnapshot);

    protected void LogSnapshot(string phase, TopDownProfilerSnapshot snapshot)
    {
        Logger.Instance.Info(
            "Scenario({0}): {1}: {2} rgb=({3:F2},{4:F2},{5:F2}) luma={6:F2} " +
            "refs={7}/{8} speedTree={9}/{10}/{11}.",
            _scenarioName, phase, Describe(snapshot), snapshot.MeanRed, snapshot.MeanGreen,
            snapshot.MeanBlue, snapshot.MeanLuma, snapshot.ReferenceDrawn,
            snapshot.ReferenceInstances, snapshot.SpeedTreeBranchInstances,
            snapshot.SpeedTreeLeafInstances, snapshot.SpeedTreeBillboardInstances);
    }

    protected static string Describe(TopDownProfilerSnapshot snapshot) =>
        $"provider={snapshot.ProviderReady} enabled={snapshot.Enabled} overlay={snapshot.OverlayPresent} " +
        $"covered={snapshot.CoversViewport} settled={snapshot.Settled} inFlight={snapshot.InFlight} " +
        $"pending={snapshot.Pending} incomplete={snapshot.Incomplete} " +
        $"requests=start:{snapshot.RequestsStarted}/complete:{snapshot.RequestsCompleted} " +
        $"overlayId={snapshot.OverlayRequestId} complete={snapshot.RenderComplete} " +
        $"fullySettled={snapshot.RenderFullySettled} pixels={snapshot.NonTransparentPixels}/{snapshot.PixelCount} " +
        $"hash=0x{snapshot.PixelHash:X16}";
}
