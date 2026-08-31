using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Behaviour tests for the camera timestep filter.
///     <para>
///         These replace <c>CameraFrameDtSourceContractTests</c>, which asserted that
///         <c>WorldView3DControl.Frame.cs</c> still contained the literal text
///         <c>"deltaSeconds = MathF.Max(MathF.Min(deltaSeconds, dtPrev1),"</c>. That pin broke on
///         reformatting, survived any change that kept the spelling, and could not check a single
///         numeric outcome. The logic now lives in <see cref="FrameDeltaFilter" /> under
///         <c>Core/</c>, so the contract can be stated as values instead of source text.
///     </para>
/// </summary>
public class FrameDeltaFilterTests
{
    /// <summary>A steady 60 Hz frame, comfortably under the clamp.</summary>
    private const float SteadyFrame = 1f / 60f;

    [Fact]
    public void Push_FirstSample_PassesRawThroughBecauseThereIsNoHistoryToMedian()
    {
        var filter = new FrameDeltaFilter();

        Assert.Equal(SteadyFrame, filter.Push(SteadyFrame));
    }

    [Fact]
    public void Push_SecondSample_StillPassesRawThrough()
    {
        var filter = new FrameDeltaFilter();
        filter.Push(SteadyFrame);

        // Only one real sample of history exists; the missing slot substitutes the current value,
        // so the median of {raw, prev, raw} is the raw value.
        Assert.Equal(0.02f, filter.Push(0.02f));
    }

    /// <summary>
    ///     The whole point of the filter: one spiked frame between two normal ones is rejected
    ///     outright rather than integrated as a camera jump.
    /// </summary>
    [Fact]
    public void Push_LoneSpikeBetweenSteadyFrames_IsRejected()
    {
        var filter = new FrameDeltaFilter();
        filter.Push(SteadyFrame);
        filter.Push(SteadyFrame);

        var filtered = filter.Push(0.09f); // a stall, but still under the clamp

        Assert.Equal(SteadyFrame, filtered);
    }

    /// <summary>
    ///     A median with zero steady-state lag: when the three samples agree, the output is the
    ///     current sample — the property that makes this preferable to an EMA.
    /// </summary>
    [Fact]
    public void Push_SteadySamples_ReturnsTheCurrentSampleExactly()
    {
        var filter = new FrameDeltaFilter();
        filter.Push(SteadyFrame);
        filter.Push(SteadyFrame);

        Assert.Equal(SteadyFrame, filter.Push(SteadyFrame));
    }

    /// <summary>
    ///     A sustained slowdown must reach the camera. A filter that rejected it would stall the
    ///     view instead of merely bounding it.
    /// </summary>
    [Fact]
    public void Push_SustainedSlowdown_PassesThroughOnceTheMedianAgrees()
    {
        var filter = new FrameDeltaFilter();
        filter.Push(SteadyFrame);
        filter.Push(0.05f);

        Assert.Equal(0.05f, filter.Push(0.05f));
    }

    /// <summary>
    ///     Order matters: the clamp runs AFTER the median. A genuine multi-frame stall therefore
    ///     still reaches the camera, bounded — rather than being median-ed away first.
    /// </summary>
    [Fact]
    public void Push_SustainedStallAboveTheCeiling_IsClampedNotDiscarded()
    {
        var filter = new FrameDeltaFilter();
        filter.Push(2f);
        filter.Push(2f);

        var filtered = filter.Push(2f);

        Assert.Equal(FrameDeltaFilter.MaxDeltaSeconds, filtered);
    }

    [Theory]
    [InlineData(0f, 0f, "a zero timestep is legitimate on a repeated presentation")]
    [InlineData(FrameDeltaFilter.MaxDeltaSeconds, FrameDeltaFilter.MaxDeltaSeconds, "exactly at the ceiling is not clamped")]
    [InlineData(0.5f, FrameDeltaFilter.MaxDeltaSeconds, "above the ceiling is clamped")]
    public void Push_SteadyInput_ClampsOnlyAboveTheCeiling(float raw, float expected, string because)
    {
        _ = because;

        var filter = new FrameDeltaFilter();
        filter.Push(raw);
        filter.Push(raw);

        Assert.Equal(expected, filter.Push(raw));
    }

    /// <summary>
    ///     Output can never exceed the ceiling regardless of input ordering — the property a
    ///     source-text pin could never establish.
    /// </summary>
    [Fact]
    public void Push_AnyInterleavingOfStallsAndSteadyFrames_NeverExceedsTheCeiling()
    {
        var filter = new FrameDeltaFilter();
        float[] samples = [SteadyFrame, 5f, SteadyFrame, 5f, 5f, 0f, 5f, SteadyFrame, 0.2f, 0.2f, 0.2f];

        foreach (var sample in samples)
        {
            var filtered = filter.Push(sample);

            Assert.InRange(filtered, 0f, FrameDeltaFilter.MaxDeltaSeconds);
        }
    }

    /// <summary>
    ///     Each control owns its own filter; the struct must carry no shared state.
    /// </summary>
    [Fact]
    public void Push_TwoFilters_DoNotShareHistory()
    {
        var primed = new FrameDeltaFilter();
        primed.Push(SteadyFrame);
        primed.Push(SteadyFrame);

        var fresh = new FrameDeltaFilter();

        // The primed filter rejects the spike; a fresh one has no history and passes it through.
        Assert.Equal(SteadyFrame, primed.Push(0.09f));
        Assert.Equal(0.09f, fresh.Push(0.09f));
    }

    [Fact]
    public void Reset_DiscardsSamplesFromThePreviousScene()
    {
        var filter = new FrameDeltaFilter();
        filter.Push(SteadyFrame);
        filter.Push(SteadyFrame);

        filter.Reset();

        // A fresh first sample passes through. If the old history survived, the median would
        // incorrectly turn this into SteadyFrame.
        Assert.Equal(0.09f, filter.Push(0.09f));
    }

    /// <summary>
    ///     The camera-jitter threading-race theory was DISPROVED — the fix is dt filtering, not
    ///     synchronization. This stays a source pin because "no lock crept back in" is a property
    ///     of the render-loop call site, not of the filter's return values.
    /// </summary>
    [Fact]
    public void RenderLoopDtPath_StaysLockFree()
    {
        var region = SourceContract.Extract(
            SourceContract.ReadAppSource("WorldView3DControl.Frame.cs"),
            "var now = Stopwatch.GetTimestamp();",
            "_lastControllerUpdateMilliseconds");

        Assert.DoesNotContain("lock (", region, StringComparison.Ordinal);
        Assert.DoesNotContain("lock(", region, StringComparison.Ordinal);
        Assert.DoesNotContain("Monitor.", region, StringComparison.Ordinal);
        Assert.DoesNotContain("new Thread", region, StringComparison.Ordinal);
        Assert.DoesNotContain("Interlocked.", region, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The profiler must see the RAW timestep: clamping first would hide exactly the long-frame
    ///     outliers a pacing investigation needs. Ordering at the call site, so still a source pin.
    /// </summary>
    [Fact]
    public void RenderLoopDtPath_RecordsTheRawSampleBeforeFiltering()
    {
        var region = SourceContract.Extract(
            SourceContract.ReadAppSource("WorldView3DControl.Frame.cs"),
            "var now = Stopwatch.GetTimestamp();",
            "_lastControllerUpdateMilliseconds");

        SourceContract.AssertOrder(region,
            "Stopwatch.GetElapsedTime(_lastFrameStartTimestamp, now).TotalSeconds;",
            "_lastDeltaSeconds = deltaSeconds;",
            "_frameDeltaFilter.Push(deltaSeconds)",
            "_controller.Update(deltaSeconds);");
    }
}
