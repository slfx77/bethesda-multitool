using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

/// <summary>
///     Source contracts for the camera frame-delta path in WorldView3DControl.Frame.cs. The dt
///     computation lives in the App TFM (not built by the test target), so these pins read the
///     source text directly.
/// </summary>
public sealed class CameraFrameDtSourceContractTests
{
    /// <summary>The dt region: from the wall-clock sample through the controller update.</summary>
    private static string ReadDtRegion()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        return SourceContract.Extract(source,
            "var now = DateTime.UtcNow;",
            "_lastControllerUpdateMilliseconds");
    }

    [Fact]
    public void ControllerReceivesMedianFilteredDeltaWithClampAfterMedian()
    {
        var region = ReadDtRegion();

        // Order pins the pipeline: raw dt sampled -> raw recorded for the profiler -> median-of-3
        // (history shift + min/max median expression) -> 0.1s clamp AFTER the median -> the
        // filtered-and-clamped `deltaSeconds` is what _controller.Update consumes.
        SourceContract.AssertOrder(region,
            "var deltaSeconds = (float)(now - _lastFrameTime).TotalSeconds;",
            "_lastDeltaSeconds = deltaSeconds;",
            "_frameDtPrev2 = _frameDtPrev1;",
            "_frameDtPrev1 = deltaSeconds;",
            "deltaSeconds = MathF.Max(MathF.Min(deltaSeconds, dtPrev1),",
            "MathF.Min(MathF.Max(deltaSeconds, dtPrev1), dtPrev2));",
            "if (deltaSeconds > 0.1f) deltaSeconds = 0.1f;",
            "_controller.Update(deltaSeconds);");
    }

    [Fact]
    public void FirstFramesSubstituteRawForMissingHistory()
    {
        var region = ReadDtRegion();

        // Seed behaviour: with no history the missing median slots duplicate the current raw value,
        // so the first two frames pass raw through — no startup special-casing beyond substitution.
        Assert.Contains("var dtPrev1 = _frameDtSampleCount >= 1 ? _frameDtPrev1 : deltaSeconds;",
            region, StringComparison.Ordinal);
        Assert.Contains("var dtPrev2 = _frameDtSampleCount >= 2 ? _frameDtPrev2 : deltaSeconds;",
            region, StringComparison.Ordinal);
    }

    [Fact]
    public void DtRegionStaysLockFree()
    {
        var region = ReadDtRegion();

        // The camera-jitter threading-race theory was DISPROVED — the fix is dt filtering, not
        // synchronization. Guard against a lock/thread creeping back into the dt path.
        Assert.DoesNotContain("lock (", region, StringComparison.Ordinal);
        Assert.DoesNotContain("lock(", region, StringComparison.Ordinal);
        Assert.DoesNotContain("Monitor.", region, StringComparison.Ordinal);
        Assert.DoesNotContain("new Thread", region, StringComparison.Ordinal);
        Assert.DoesNotContain("Interlocked.", region, StringComparison.Ordinal);
    }
}
