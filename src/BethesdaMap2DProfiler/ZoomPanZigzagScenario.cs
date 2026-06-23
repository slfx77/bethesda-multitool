using BethesdaMultitool;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core;
using System.Numerics;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

/// <summary>
///     Default repro scenario for the disappearing-cells bug. Switches to TerrainTextures,
///     zooms to ~3 cells across (high pixel density), then pans the viewport in a zigzag
///     path that crosses 100+ cells while the cap = viewport×3 budget is constantly
///     thrashed. Goal: surface cells that appear in the cache, get drawn, then evict.
/// </summary>
internal sealed class ZoomPanZigzagScenario : Map2DScenario
{
    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        var log = Logger.Instance;

        // 1. Switch to TerrainTextures (the only layer with the per-cell streaming cache).
        await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
        log.Info("Scenario: switched to TerrainTextures.");
        await Task.Delay(2000);

        // 2. Zoom in. Each wheel tick is 1.15× per real-app convention. Walk it up so the
        //    viewport request key crosses each resolution threshold (132 → 264 → 528 ppc),
        //    matching how a user reaches "high zoom" interactively.
        for (var i = 0; i < 30; i++)
        {
            await UiAsync(queue, () =>
            {
                var w = control.Profiler_CanvasWidth;
                var h = control.Profiler_CanvasHeight;
                var screenCenter = new Vector2(w * 0.5f, h * 0.5f);
                var worldBefore = (screenCenter - control.Profiler_PanOffset) / control.Profiler_Zoom;
                var newZoom = Math.Clamp(control.Profiler_Zoom * 1.15f, 0.001f, 50f);
                var newPan = screenCenter - worldBefore * newZoom;
                control.Profiler_SetView(newZoom, newPan);
            });
            await Task.Delay(80);
        }

        log.Info("Scenario: zoomed in to {0:F4}.", await UiAsync(queue, () => control.Profiler_Zoom));
        await Task.Delay(2000);

        // 3. Zigzag pan. Each step is one cell-equivalent worth of screen pixels. The
        //    Preload margin reacts to velocity, so a deliberate directional pan loads
        //    cells ahead of the viewport — which is the regime where the disappear bug
        //    has been reported.
        var directions = new[]
        {
            new Vector2(-1f, 0f),
            new Vector2(0f, -1f),
            new Vector2(1f, 0f),
            new Vector2(0f, -1f),
            new Vector2(-1f, 0f),
            new Vector2(0f, 1f),
            new Vector2(1f, 0f),
            new Vector2(0f, 1f)
        };

        var panSteps = 120;
        var panStepPx = 80f;
        for (var i = 0; i < panSteps; i++)
        {
            var dir = directions[i % directions.Length];
            await UiAsync(queue, () => control.Profiler_PanBy(dir * panStepPx));
            await Task.Delay(40);
            if (i % 16 == 0)
            {
                log.Info("Scenario: pan step {0}/{1} (cacheSize={2}, cap={3}, cacheGen={4}, buildVer={5})",
                    i, panSteps,
                    await UiAsync(queue, () => control.Profiler_CacheCount),
                    await UiAsync(queue, () => control.Profiler_CacheCap),
                    await UiAsync(queue, () => control.Profiler_CacheGen),
                    await UiAsync(queue, () => control.Profiler_BuildVersion));
            }
        }

        log.Info("Scenario: zoom-pan-zigzag complete.");
    }
}
