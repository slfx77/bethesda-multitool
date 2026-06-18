using FalloutXbox360Utils;
using FalloutXbox360Utils.Core;
using Microsoft.UI.Dispatching;

namespace FalloutMap2DProfiler;

/// <summary>
///     Repro for the "zoom in then out freezes the app" report. Switches to TerrainTextures, centers on
///     the worldspace cell centroid, zooms STRAIGHT IN past the per-cell tiers to the 1056 px/cell detail
///     tier (where each tile is ~4.5 MB), holds, then zooms STRAIGHT BACK OUT to the aggregate — the
///     transition where a count-based (byte-unaware) apply/eviction budget dumped a single giant GPU
///     upload and stalled the UI thread. Runs a few cycles; a freeze shows as a large max/p99 frame time.
/// </summary>
internal sealed class ZoomInOutScenario : Map2DScenario
{
    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        var log = Logger.Instance;
        const float cellWorld = 4096f;

        await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
        log.Info("Scenario(zoom-in-out): switched to TerrainTextures.");
        await Task.Delay(1500);

        // Aggregate-active starting zoom (16 px/cell), centered on the dense centroid.
        var lowZoom = 16f / cellWorld;
        await UiAsync(queue, () => control.Profiler_CenterOnActiveCells(lowZoom));
        await Task.Delay(1500);

        for (var cycle = 0; cycle < 3; cycle++)
        {
            // Zoom IN: 16 → ~2000 px/cell (past the 660-px 1056-tier threshold). Re-center each step
            // so a packed viewport keeps streaming high-res cells.
            var zoom = lowZoom;
            for (var i = 0; i < 28; i++)
            {
                zoom *= 1.18f;
                await UiAsync(queue, () => control.Profiler_CenterOnActiveCells(zoom));
                await Task.Delay(90);
            }
            log.Info("Scenario(zoom-in-out): cycle {0} IN done zoom={1:F5} pxPerCell={2:F0} cacheSize={3} cap={4}",
                cycle,
                await UiAsync(queue, () => control.Profiler_Zoom),
                await UiAsync(queue, () => control.Profiler_Zoom) * cellWorld,
                await UiAsync(queue, () => control.Profiler_CacheCount),
                await UiAsync(queue, () => control.Profiler_CacheCap));
            // Dwell at full zoom-in so the 1056-tier cells finish streaming + applying before zooming out.
            await Task.Delay(3500);

            // Zoom OUT: back to the aggregate. THIS is the reported freeze point — the high-res tiles
            // built on the way in are still resident and their applies/evictions hit the UI thread.
            for (var i = 0; i < 28; i++)
            {
                zoom /= 1.18f;
                await UiAsync(queue, () => control.Profiler_CenterOnActiveCells(zoom));
                await Task.Delay(90);
            }
            log.Info("Scenario(zoom-in-out): cycle {0} OUT done zoom={1:F5} cacheSize={2} cap={3}",
                cycle,
                await UiAsync(queue, () => control.Profiler_Zoom),
                await UiAsync(queue, () => control.Profiler_CacheCount),
                await UiAsync(queue, () => control.Profiler_CacheCap));
            // Long dwell AFTER zoom-out: this is where the reported freeze fires (the heavy post-zoom-out
            // work — wide-viewport streaming, 1056-cell eviction, aggregate re-activation — runs during
            // the settle; a 1.2 s dwell let the next cycle's zoom-in cancel it before it could surface).
            await Task.Delay(7000);
        }

        log.Info("Scenario(zoom-in-out): complete.");
        await Task.Delay(1000);
    }
}
