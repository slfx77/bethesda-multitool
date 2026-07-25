using System.Numerics;
using BethesdaMultitool;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

/// <summary>
///     Validates the TerrainTextures aggregate LOD (Issue 2 fix): logs the worldspace list, switches
///     to TerrainTextures, then zooms WAY out so the whole worldspace is visible. Pre-fix this fired
///     a single huge per-cell stream (e.g. requested=13455) → app crawl; post-fix it builds one
///     aggregate bitmap (look for "aggregate LOD built" + no large "TerrainTextures stream requested").
/// </summary>
internal sealed class TerrainAggregateScenario : Map2DScenario
{
    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        var log = Logger.Instance;

        var labels = await UiAsync(queue, () => control.Profiler_WorldspaceLabels);
        for (var i = 0; i < labels.Count; i++)
        {
            log.Info("Scenario: worldspace[{0}] = {1}", i, labels[i]);
        }

        await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
        log.Info("Scenario: switched to TerrainTextures on worldspace[{0}].",
            await UiAsync(queue, () => control.Profiler_WorldspaceSelectedIndex));
        await Task.Delay(1500);

        // Zoom out hard: center the (whole) worldspace and drop zoom below the aggregate threshold
        // (cell < 24 px on screen). 0.002 → 4096*0.002 ≈ 8 px/cell.
        for (var pass = 0; pass < 3; pass++)
        {
            await UiAsync(queue, () =>
            {
                var w = control.Profiler_CanvasWidth;
                var h = control.Profiler_CanvasHeight;
                control.Profiler_SetView(0.002f, new Vector2(w * 0.5f, h * 0.5f));
            });
            await Task.Delay(2500);
            log.Info("Scenario: zoom={0:F4} aggregateActive={1} perCellCache={2}",
                await UiAsync(queue, () => control.Profiler_Zoom),
                await UiAsync(queue, () => control.Profiler_TerrainAggregateActive),
                await UiAsync(queue, () => control.Profiler_CacheCount));
        }

        log.Info("Scenario: terrain-aggregate complete.");
    }
}
