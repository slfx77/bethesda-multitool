using BethesdaMultitool.Core;
using BethesdaMultitool;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

internal abstract class Map2DScenario
{
    public abstract Task RunAsync(WorldMapControl control, DispatcherQueue queue);

    public static Map2DScenario? Resolve(string name, string? artifactOutputPath = null)
    {
        return name switch
        {
            "zoom-pan-zigzag" => new ZoomPanZigzagScenario(),
            "terrain-aggregate" => new TerrainAggregateScenario(),
            "zoom-into-cells" => new ZoomIntoCellsScenario(true),
            "zoom-into-cells-heightmap" => new ZoomIntoCellsScenario(false),
            "zoom-in-out" => new ZoomInOutScenario(),
            "pan-stress" => new PanStressScenario(),
            "topdown-still" => new TopDownStillScenario(),
            "topdown-interior" => new TopDownInteriorScenario(
                artifactOutputPath ?? Path.Combine(
                    Path.GetTempPath(), "BethesdaMap2DProfiler", "topdown-interior-settled.png")),
            "topdown-lighting" => new TopDownLightingScenario(),
            "topdown-speedtree" => new TopDownSpeedTreeScenario(
                artifactOutputPath ?? Path.Combine(
                    Path.GetTempPath(), "BethesdaMap2DProfiler", "topdown-speedtree-settled.png")),
            _ => null
        };
    }

    protected static Task UiAsync(DispatcherQueue queue, Action action)
    {
        var tcs = new TaskCompletionSource();
        var ok = queue.TryEnqueue(() =>
        {
            try
            {
                action();
                tcs.SetResult();
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!ok) tcs.SetException(new InvalidOperationException("DispatcherQueue.TryEnqueue rejected scenario step."));
        return tcs.Task;
    }

    protected static Task<T> UiAsync<T>(DispatcherQueue queue, Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>();
        var ok = queue.TryEnqueue(() =>
        {
            try
            {
                tcs.SetResult(action());
            }
            catch (Exception ex)
            {
                tcs.SetException(ex);
            }
        });
        if (!ok) tcs.SetException(new InvalidOperationException("DispatcherQueue.TryEnqueue rejected scenario step."));
        return tcs.Task;
    }
}
