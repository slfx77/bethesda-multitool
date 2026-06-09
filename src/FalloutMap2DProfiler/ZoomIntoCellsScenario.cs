using FalloutXbox360Utils;
using FalloutXbox360Utils.Core;
using Microsoft.UI.Dispatching;

namespace FalloutMap2DProfiler;

/// <summary>
///     Faithful repro of "zoom in until individual cells appear, then it crashes": switches to
///     TerrainTextures, centers the camera on the active worldspace's cell CENTROID (a dense point),
///     and zooms straight in while holding that center — so the aggregate→per-cell transition streams
///     a packed viewport (hundreds of cells) instead of the near-empty corner the zigzag scenario
///     drifted into on WastelandNV. Re-centers every step so the camera never leaves the dense region.
/// </summary>
internal sealed class ZoomIntoCellsScenario(bool useTerrainTextures) : Map2DScenario
{
    private readonly bool _useTerrainTextures = useTerrainTextures;

    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        var log = Logger.Instance;

        if (_useTerrainTextures)
        {
            await UiAsync(queue, () => control.Profiler_Layer = WorldMapLayer.TerrainTextures);
            log.Info("Scenario(zoom-into-cells): switched to TerrainTextures.");
        }
        else
        {
            // Stay on the default Heightmap layer — the single-bitmap background + cell grid +
            // per-cell labels + placed-object boxes path the user reported crashing on.
            log.Info("Scenario(zoom-into-cells): staying on default layer {0}.",
                await UiAsync(queue, () => control.Profiler_Layer));
        }

        await Task.Delay(1500);

        // Start just below the aggregate→per-cell threshold (24 screen px/cell) so the first frames
        // show the aggregate LOD, then walk zoom up past it. 4096 world units/cell.
        const float cellWorld = 4096f;
        var startPxPerCell = 16f; // aggregate active
        var zoom = startPxPerCell / cellWorld;

        await UiAsync(queue, () => control.Profiler_CenterOnActiveCells(zoom));
        log.Info("Scenario(zoom-into-cells): centered on cell centroid at zoom={0:F5} ({1:F0} px/cell).",
            await UiAsync(queue, () => control.Profiler_Zoom), startPxPerCell);
        await Task.Delay(1500);

        // 28 steps × 1.18 ≈ ×130 — carries 16 px/cell up through the 24-px transition and the
        // 132/264/528 ppc tiers to deep zoom, re-centering each step so the dense viewport persists.
        for (var i = 0; i < 28; i++)
        {
            zoom *= 1.18f;
            await UiAsync(queue, () => control.Profiler_CenterOnActiveCells(zoom));
            await Task.Delay(140);
            if (i % 4 == 0)
            {
                log.Info("Scenario(zoom-into-cells): step {0}/28 zoom={1:F5} pxPerCell={2:F1} cacheSize={3} cap={4}",
                    i,
                    await UiAsync(queue, () => control.Profiler_Zoom),
                    await UiAsync(queue, () => control.Profiler_Zoom) * cellWorld,
                    await UiAsync(queue, () => control.Profiler_CacheCount),
                    await UiAsync(queue, () => control.Profiler_CacheCap));
            }
        }

        log.Info("Scenario(zoom-into-cells): complete zoom={0:F5} cacheSize={1} cap={2}.",
            await UiAsync(queue, () => control.Profiler_Zoom),
            await UiAsync(queue, () => control.Profiler_CacheCount),
            await UiAsync(queue, () => control.Profiler_CacheCap));
        await Task.Delay(2000);
    }
}
