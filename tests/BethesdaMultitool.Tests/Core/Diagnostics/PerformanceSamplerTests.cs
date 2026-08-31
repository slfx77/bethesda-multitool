using BethesdaMultitool.Core.Diagnostics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Diagnostics;

/// <summary>
///     Pins the frame-pacing arithmetic the HUD and the headless profiler both report. It exists in
///     Core rather than inline in the renderer specifically so it can be tested: the previous median
///     lived inside <c>App/</c>, which is compiled out of the test TFM, so every frame-timing figure
///     this project quoted was unverified.
/// </summary>
public sealed class PerformanceSamplerTests
{
    private static PerformanceSampler WithSamples(params double[] milliseconds)
    {
        var sampler = new PerformanceSampler(Math.Max(1, milliseconds.Length));
        foreach (var value in milliseconds)
        {
            sampler.Add(value);
        }

        return sampler;
    }

    [Fact]
    public void An_empty_window_reports_zeroes_rather_than_dividing_by_zero()
    {
        var snapshot = new PerformanceSampler().Snapshot();

        Assert.Equal(0, snapshot.SampleCount);
        Assert.Equal(0, snapshot.AverageFps);
        Assert.Equal(0, snapshot.OnePercentLowFps);
    }

    [Fact]
    public void A_steady_sixty_fps_window_reads_as_sixty()
    {
        // 16.666… ms is the target this whole exercise is aimed at, so it is worth pinning that the
        // instrument actually reports 60 for it rather than 59.9 through some rounding slip.
        var sampler = new PerformanceSampler();
        for (var i = 0; i < 200; i++)
        {
            sampler.Add(1000.0 / 60.0);
        }

        var snapshot = sampler.Snapshot();

        Assert.Equal(60.0, snapshot.AverageFps, 6);
        Assert.Equal(60.0, snapshot.MedianFps, 6);
        Assert.Equal(60.0, snapshot.OnePercentLowFps, 6);
    }

    [Fact]
    public void Median_and_average_disagree_when_the_window_is_skewed()
    {
        // The reason both are reported. Nine fast frames and one very slow one is a hitch, and an
        // average alone would describe it as a uniformly mediocre 28 FPS.
        var sampler = WithSamples(10, 10, 10, 10, 10, 10, 10, 10, 10, 250);

        var snapshot = sampler.Snapshot();

        Assert.Equal(10, snapshot.MedianMilliseconds);
        Assert.Equal(34, snapshot.AverageMilliseconds);
        Assert.Equal(250, snapshot.MaxMilliseconds);
    }

    [Fact]
    public void The_one_percent_low_exposes_a_hitch_the_median_hides()
    {
        // 100 frames: 99 at a steady 10 ms, one at 200 ms. The median says 100 FPS and looks
        // perfect; the 1% low says 5 FPS, which is what the hitch actually feels like.
        var sampler = new PerformanceSampler(100);
        for (var i = 0; i < 99; i++)
        {
            sampler.Add(10);
        }

        sampler.Add(200);

        var snapshot = sampler.Snapshot();

        Assert.Equal(100.0, snapshot.MedianFps, 6);
        Assert.Equal(5.0, snapshot.OnePercentLowFps, 6);
    }

    [Fact]
    public void Percentile99_returns_a_frame_that_actually_occurred()
    {
        // Nearest-rank, not interpolated: quoting a p99 no frame ever hit invites chasing a number
        // that does not correspond to anything observable.
        var sampler = new PerformanceSampler(100);
        for (var i = 1; i <= 100; i++)
        {
            sampler.Add(i);
        }

        var snapshot = sampler.Snapshot();

        Assert.Equal(99, snapshot.Percentile99Milliseconds);
        Assert.Equal(100, snapshot.MaxMilliseconds);
    }

    [Fact]
    public void The_window_keeps_only_the_most_recent_frames()
    {
        // A ring, so a hitch ages out instead of pinning the HUD's worst-frame figure forever.
        var sampler = new PerformanceSampler(4);
        sampler.Add(1000); // ages out
        foreach (var value in new double[] { 10, 10, 10, 10 })
        {
            sampler.Add(value);
        }

        var snapshot = sampler.Snapshot();

        Assert.Equal(4, snapshot.SampleCount);
        Assert.Equal(10, snapshot.MaxMilliseconds);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Nonsense_durations_are_ignored_rather_than_poisoning_the_window(double bad)
    {
        // A zero would read as infinite FPS and a NaN would make every percentile in the window NaN
        // — both would be reported on screen as fact.
        var sampler = WithSamples(10, 10, 10);
        sampler.Add(bad);

        var snapshot = sampler.Snapshot();

        Assert.Equal(3, snapshot.SampleCount);
        Assert.Equal(100.0, snapshot.MedianFps, 6);
    }

    [Fact]
    public void Reset_clears_the_window()
    {
        var sampler = WithSamples(10, 20, 30);

        sampler.Reset();

        Assert.Equal(0, sampler.Count);
        Assert.Equal(0, sampler.Snapshot().SampleCount);
    }
}
