using System.Diagnostics;
using BethesdaMultitool;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

/// <summary>
///     Measures the real "fully render the world map" user scenario: TerrainTextures layer,
///     toolbar Zoom-to-Fit (the WHOLE worldspace in view — 16,397 cells for WastelandNV, not the
///     ~40-cell close-up <c>topdown-still</c> frames), then the "Rendered models" overlay from cold,
///     timing every convergence pass until the overlay settles. Reports total wall-clock, request
///     count, and per-request duration statistics so regressions in whole-map convergence are
///     measurable instead of anecdotal.
/// </summary>
internal sealed class TopDownWorldMapScenario : TopDownScenarioBase
{
    private static readonly TimeSpan WholeMapConvergenceTimeout = TimeSpan.FromMinutes(15);

    internal TopDownWorldMapScenario() : base("topdown-worldmap")
    {
    }

    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        var log = Logger.Instance;

        await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
        var zoom = await UiAsync(queue, control.Profiler_ZoomToFitWorldspace);
        var cellSize = await UiAsync(queue, () => control.Profiler_CellWorldSize);
        log.Info(
            "Scenario(topdown-worldmap): zoom-to-fit applied at {0:F6} zoom ({1:F1} px/cell).",
            zoom, zoom * cellSize);

        // Let the 2D terrain layer (aggregate LOD at this zoom) settle first so the overlay
        // measurement below isn't paying the one-time terrain aggregate build.
        await Task.Delay(500);

        await WaitForProviderAndEnableAsync(control, queue);
        var overlayEnabledAt = Stopwatch.StartNew();

        var converged = await WaitForQuiescentAsync(
            control, queue,
            IsConverged,
            WholeMapConvergenceTimeout,
            "whole-map overlay convergence");
        overlayEnabledAt.Stop();
        AssertNonempty(converged, "whole-map overlay");
        AssertReferenceGeometry(converged, "whole-map overlay");
        LogSnapshot("whole-map-settled", converged);

        log.Info(
            "Scenario(topdown-worldmap): PASS — whole-map overlay converged in {0:F1}s over " +
            "{1} request(s) (last request {2:F0}ms, refs {3}/{4}).",
            overlayEnabledAt.Elapsed.TotalSeconds,
            converged.RequestsStarted,
            converged.LastDurationMs,
            converged.ReferenceDrawn,
            converged.ReferenceInstances);
    }
}
