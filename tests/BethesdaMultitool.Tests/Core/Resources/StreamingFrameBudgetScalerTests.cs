using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Resources;

/// <summary>
///     Pins the time-based streaming pace: per-frame allowances calibrated for 60fps must grow
///     linearly with the actual frame duration (so loading throughput survives GPU contention from
///     another app) and never shrink below the 60fps baseline, with a bounded catch-up burst.
/// </summary>
public sealed class StreamingFrameBudgetScalerTests
{
    [Theory]
    [InlineData(1.0 / 60.0)]
    [InlineData(1.0 / 120.0)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Scale_AtOrBelowReferenceOrInvalid_IsOne(double frameSeconds)
    {
        // Infinity is "invalid input" (a clock anomaly), not "a very slow frame" — it must fall
        // back to 1.0, never to the max burst.
        Assert.Equal(1.0, StreamingFrameBudgetScaler.Scale(frameSeconds));
    }

    [Fact]
    public void Scale_GrowsLinearlyWithFrameDuration()
    {
        // A 30fps frame earns exactly 2× the 60fps allowance, 6fps earns 10×.
        Assert.Equal(2.0, StreamingFrameBudgetScaler.Scale(2.0 / 60.0), precision: 10);
        Assert.Equal(10.0, StreamingFrameBudgetScaler.Scale(10.0 / 60.0), precision: 10);
    }

    [Fact]
    public void Scale_CapsAtMaxScale()
    {
        Assert.Equal(StreamingFrameBudgetScaler.MaxScale, StreamingFrameBudgetScaler.Scale(1.0));
        Assert.Equal(StreamingFrameBudgetScaler.MaxScale, StreamingFrameBudgetScaler.Scale(10.0));
    }

    [Fact]
    public void ScaleCount_ScalesAndSaturates()
    {
        Assert.Equal(32, StreamingFrameBudgetScaler.ScaleCount(16, 2.0));
        // The unthrottled sentinel must stay unbounded instead of overflowing negative.
        Assert.Equal(int.MaxValue, StreamingFrameBudgetScaler.ScaleCount(int.MaxValue, 30.0));
        // Sub-1.0 scales never shrink an allowance.
        Assert.Equal(16, StreamingFrameBudgetScaler.ScaleCount(16, 0.5));
        // Non-positive allowances (disabled budgets) pass through untouched.
        Assert.Equal(0, StreamingFrameBudgetScaler.ScaleCount(0, 8.0));
    }

    [Fact]
    public void ScaleBytes_ScalesAndSaturates()
    {
        Assert.Equal(8L * 1024 * 1024, StreamingFrameBudgetScaler.ScaleBytes(4L * 1024 * 1024, 2.0));
        Assert.Equal(long.MaxValue, StreamingFrameBudgetScaler.ScaleBytes(long.MaxValue, 30.0));
        Assert.Equal(4096L, StreamingFrameBudgetScaler.ScaleBytes(4096L, 0.25));
    }
}
