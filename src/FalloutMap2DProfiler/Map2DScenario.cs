using FalloutXbox360Utils.Core;
using FalloutXbox360Utils;
using Microsoft.UI.Dispatching;

namespace FalloutMap2DProfiler;

internal abstract class Map2DScenario
{
    public abstract Task RunAsync(WorldMapControl control, DispatcherQueue queue);

    public static Map2DScenario? Resolve(string name)
    {
        return name switch
        {
            "zoom-pan-zigzag" => new ZoomPanZigzagScenario(),
            "terrain-aggregate" => new TerrainAggregateScenario(),
            "zoom-into-cells" => new ZoomIntoCellsScenario(true),
            "zoom-into-cells-heightmap" => new ZoomIntoCellsScenario(false),
            "pan-stress" => new PanStressScenario(),
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
