using System.Diagnostics;
using System.Numerics;
using BethesdaMultitool;
using BethesdaMultitool.Core.Diagnostics;
using Microsoft.UI.Dispatching;

namespace BethesdaMap2DProfiler;

/// <summary>
///     Automated regression gate for the rendered-models overlay cache. It proves a converged static
///     viewport goes idle, the 25% render margin covers a small pan without a blank edge, and a pan
///     beyond that margin starts a new request sequence which converges to a covered, nonempty overlay.
/// </summary>
internal sealed class TopDownStillScenario : TopDownScenarioBase
{
    private static readonly TimeSpan StaticHold = TimeSpan.FromSeconds(3);

    internal TopDownStillScenario() : base("topdown-still")
    {
    }

    public override async Task RunAsync(WorldMapControl control, DispatcherQueue queue)
    {
        var log = Logger.Instance;

        await PrepareExteriorAsync(control, queue);

        var initial = await WaitForQuiescentAsync(
            control, queue,
            IsConverged,
            ConvergenceTimeout,
            "initial overlay convergence");
        AssertNonempty(initial, "initial overlay");
        LogSnapshot("initial-settled", initial);

        await AssertStaticAsync(control, queue, initial, StaticHold, "initial settled hold");

        // 64 screen pixels is safely inside both the 128px request quantum and the 25%-of-viewport
        // render margin at the profiler's minimum supported window size.
        var canvasWidth = await UiAsync(queue, () => control.Profiler_CanvasWidth);
        var smallPanPixels = Math.Clamp(canvasWidth * 0.05f, 32f, 64f);
        await UiAsync(queue, () => control.Profiler_PanBy(new Vector2(-smallPanPixels, 0f)));
        await Task.Delay(150);

        var smallPanWatch = Stopwatch.StartNew();
        TopDownProfilerSnapshot afterSmallPan = default;
        while (smallPanWatch.Elapsed < TimeSpan.FromSeconds(1))
        {
            afterSmallPan = await SnapshotAsync(control, queue);
            if (!afterSmallPan.OverlayPresent || !afterSmallPan.CoversViewport)
            {
                throw new InvalidOperationException(
                    "topdown-still: small pan exposed a blank edge outside the cached overlay margin. " +
                    Describe(afterSmallPan));
            }
            AssertNonempty(afterSmallPan, "small-pan overlay");
            await Task.Delay(100);
        }

        // A quantization boundary may legitimately schedule a refresh even for a sub-margin pan.
        // Let that optional refresh finish before establishing the large-pan sequence baseline.
        await Task.Delay(500);
        afterSmallPan = await WaitForQuiescentAsync(
            control, queue,
            IsConverged,
            ConvergenceTimeout,
            "small-pan overlay settlement");
        LogSnapshot("small-pan-covered", afterSmallPan);

        var beforeLargePan = afterSmallPan;
        var largePanPixels = MathF.Max(512f, canvasWidth * 0.60f);
        await UiAsync(queue, () => control.Profiler_PanBy(new Vector2(-largePanPixels, 0f)));

        var newSequence = await WaitForAsync(
            control, queue,
            snapshot => snapshot.RequestsStarted > beforeLargePan.RequestsStarted,
            TimeSpan.FromSeconds(30),
            "large-pan request start");
        log.Info(
            "Scenario(topdown-still): large pan started a new request sequence ({0} -> {1}).",
            beforeLargePan.RequestsStarted, newSequence.RequestsStarted);

        var converged = await WaitForQuiescentAsync(
            control, queue,
            snapshot => IsConverged(snapshot)
                && snapshot.OverlayRequestId > beforeLargePan.OverlayRequestId,
            ConvergenceTimeout,
            "large-pan overlay convergence");
        AssertNonempty(converged, "large-pan overlay");
        if (converged.RequestsStarted <= beforeLargePan.RequestsStarted)
        {
            throw new InvalidOperationException(
                "topdown-still: large pan converged without a new request sequence. " + Describe(converged));
        }
        LogSnapshot("large-pan-settled", converged);

        await AssertStaticAsync(control, queue, converged, StaticHold, "post-pan settled hold");
        log.Info("Scenario(topdown-still): PASS — static cache idle, margin covered, refresh converged.");
    }

    private async Task AssertStaticAsync(
        WorldMapControl control,
        DispatcherQueue queue,
        TopDownProfilerSnapshot baseline,
        TimeSpan hold,
        string phase)
    {
        var watch = Stopwatch.StartNew();
        while (watch.Elapsed < hold)
        {
            await Task.Delay(150);
            var current = await SnapshotAsync(control, queue);
            if (current.RequestsStarted != baseline.RequestsStarted)
            {
                throw new InvalidOperationException(
                    $"topdown-still: {phase} started an unexpected request while static " +
                    $"({baseline.RequestsStarted} -> {current.RequestsStarted}). {Describe(current)}");
            }
            if (!IsConverged(current))
            {
                throw new InvalidOperationException(
                    $"topdown-still: {phase} lost settled/covered overlay state. {Describe(current)}");
            }
            AssertNonempty(current, phase);
        }

        Logger.Instance.Info(
            "Scenario(topdown-still): {0} remained idle at {1} request(s) for {2:F1}s.",
            phase, baseline.RequestsStarted, hold.TotalSeconds);
    }

}
