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
    public void Scale_GrowsLinearlyWithFrameDuration_BelowTheCap()
    {
        // A 30fps frame earns exactly 2× the 60fps allowance, 20fps earns 3×.
        Assert.Equal(2.0, StreamingFrameBudgetScaler.Scale(2.0 / 60.0), 10);
        Assert.Equal(3.0, StreamingFrameBudgetScaler.Scale(3.0 / 60.0), 10);
    }

    [Fact]
    public void Scale_CapsAtMaxScale()
    {
        Assert.Equal(StreamingFrameBudgetScaler.MaxScale, StreamingFrameBudgetScaler.Scale(1.0));
        Assert.Equal(StreamingFrameBudgetScaler.MaxScale, StreamingFrameBudgetScaler.Scale(10.0));
    }

    [Fact]
    public void MaxScale_KeepsTheFeedbackLoopGainBelowOne()
    {
        // The scale is derived from the previous frame and then licenses more render-thread work on
        // the next one, so it is a feedback loop and MaxScale is its gain. At the old cap of 30 a
        // single 300 ms hitch granted the following frame ~18x its budget, which kept that frame long
        // — the measured signature was serially-correlated upload time (lag-1 autocorrelation +0.36)
        // rather than an isolated spike. Keep the cap low enough that a hitch decays.
        Assert.True(
            StreamingFrameBudgetScaler.MaxScale <= 6.0,
            $"MaxScale {StreamingFrameBudgetScaler.MaxScale} is high enough to sustain a hitch");

        // ...but still generous enough to serve the reason the scaler exists: a slow viewer must not
        // collapse to a frame-rate-proportional share of its loading throughput.
        Assert.True(StreamingFrameBudgetScaler.MaxScale >= 2.0);
    }

    [Fact]
    public void SmoothFrameSeconds_SeedsFromTheFirstSampleThenDampensOutliers()
    {
        // First sample seeds the average outright.
        Assert.Equal(0.05, StreamingFrameBudgetScaler.SmoothFrameSeconds(0.0, 0.05), 10);

        // A single 450 ms outlier (measured on a streaming sweep) must move the average by only a
        // fraction of its size, so one hitch cannot set the next frame's budget.
        var smoothed = StreamingFrameBudgetScaler.SmoothFrameSeconds(0.060, 0.450);
        Assert.True(smoothed < 0.12, $"outlier moved the average to {smoothed:F4}s");
        Assert.True(smoothed > 0.060, "the average must still respond to a slower frame");
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    public void SmoothFrameSeconds_IgnoresInvalidSamples(double sample)
    {
        Assert.Equal(0.030, StreamingFrameBudgetScaler.SmoothFrameSeconds(0.030, sample), 10);
    }

    [Fact]
    public void TimeBudget_IsCappedAtAShareOfTheFrameItRunsInside()
    {
        // 200 ms frame, scale at the cap: the scaled allowance (2 x 4 = 8 ms) is under 15% of the
        // frame, so the scale governs.
        Assert.Equal(8.0, StreamingFrameBudgetScaler.TimeBudgetMilliseconds(2.0, 4.0, 200.0), 6);

        // 20 ms frame: 15% is 3 ms, which is tighter than the 8 ms scaled allowance, so streaming
        // cannot take a disproportionate slice of a fast frame.
        Assert.Equal(3.0, StreamingFrameBudgetScaler.TimeBudgetMilliseconds(2.0, 4.0, 20.0), 6);
    }

    [Fact]
    public void TimeBudget_NeverDropsBelowTheUnscaledBase()
    {
        // A very fast frame must still permit the base allowance, or a scene could stop loading and
        // never recover: less streaming -> faster frames -> an even smaller budget.
        Assert.Equal(2.0, StreamingFrameBudgetScaler.TimeBudgetMilliseconds(2.0, 1.0, 1.0), 6);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(0.0)]
    [InlineData(-5.0)]
    public void TimeBudget_FallsBackToTheScaledAllowanceWithoutAFrameTime(double frameMs)
    {
        Assert.Equal(8.0, StreamingFrameBudgetScaler.TimeBudgetMilliseconds(2.0, 4.0, frameMs), 6);
    }

    [Fact]
    public void SmoothFrameSeconds_ConvergesOnASustainedFrameRate()
    {
        // A genuinely slow scene must still reach its full allowance — the smoothing delays the
        // response, it does not cap it.
        var average = 1.0 / 60.0;
        for (var i = 0; i < 200; i++)
        {
            average = StreamingFrameBudgetScaler.SmoothFrameSeconds(average, 0.100);
        }

        Assert.Equal(0.100, average, 3);
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