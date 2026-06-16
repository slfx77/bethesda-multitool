using FalloutXbox360Utils;
using FalloutXbox360Utils.Core;
using System.Numerics;
using Microsoft.UI.Dispatching;

namespace FalloutMap2DProfiler;

/// <summary>
///     Sustained wide-viewport streaming-churn repro. Switches to TerrainTextures, centers on the
///     active worldspace's cell centroid at the aggregate→per-cell transition zoom (~40 px/cell, so
///     hundreds of cells sit in the viewport), then sweeps the camera back and forth across a distance
///     wider than the cell-bitmap cache. That forces cells to stream IN and evict OUT continuously, so
///     all <c>StreamTerrainTexturesPerCell</c> workers stay saturated for the whole run — the regime
///     the WastelandNV crash needs (the zoom-into-cells scenario settles on a few cells and goes idle,
///     so it never reproduces). Pair with <c>DOTNET_GCgen0size=0x800000</c> so a transient gen0 GC
///     hole is caught close to its source.
/// </summary>
internal sealed class PanStressScenario : Map2DScenario
{
    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        var log = Logger.Instance;

        await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
        log.Info("Scenario(pan-stress): switched to TerrainTextures.");
        await Task.Delay(1500);

        const float cellWorld = 4096f;
        var zoom = 40f / cellWorld; // ~40 px/cell -> packed viewport, many cells streaming at once
        await UiAsync(queue, () => control.Profiler_CenterOnActiveCells(zoom));
        log.Info("Scenario(pan-stress): centered on centroid at ~40 px/cell; beginning sustained churn.");
        await Task.Delay(1500);

        // Each sweep travels ~55 cells (wider than the viewport + cache), so by the time the camera
        // reverses, the cells it started over have evicted and must re-stream. Serpentine vertical
        // drift + periodic re-center keep it roaming the dense region without wandering off the map.
        const float stepPx = 200f;
        const int sweepSteps = 28;
        const int sweeps = 80;
        for (var s = 0; s < sweeps; s++)
        {
            var dir = s % 2 == 0 ? 1f : -1f;
            for (var c = 0; c < sweepSteps; c++)
            {
                await UiAsync(queue, () => control.Profiler_PanBy(new Vector2(-dir * stepPx, 0f)));
                await Task.Delay(28);
            }

            if (s % 6 == 5)
            {
                await UiAsync(queue, () => control.Profiler_CenterOnActiveCells(zoom));
            }
            else
            {
                await UiAsync(queue, () => control.Profiler_PanBy(new Vector2(0f, -stepPx)));
            }

            if (s % 8 == 0)
            {
                log.Info("Scenario(pan-stress): sweep {0}/{1} (cacheSize={2}, cap={3})",
                    s, sweeps,
                    await UiAsync(queue, () => control.Profiler_CacheCount),
                    await UiAsync(queue, () => control.Profiler_CacheCap));
            }
        }

        // Settle, then report viewport coverage: with cap hysteresis holding the pan-back
        // history, cached should ≈ visible (the numeric form of "no permanently blank cells").
        await Task.Delay(2000);
        var (visible, cached) = await UiAsync(queue, control.Profiler_VisibleCellCoverage);
        log.Info("Scenario(pan-stress): final coverage visible={0} cached={1}", visible, cached);

        log.Info("Scenario(pan-stress): complete.");
        await Task.Delay(1000);
    }
}
