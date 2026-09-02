using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerAnimationClockPolicyTests
{
    [Fact]
    public void Resolve_NonUnitClockUsesAuthoredBoundaryTimesForClamp()
    {
        var clip = Clip(loops: false, Track(frequency: 0.5f, phase: 0f));

        var window = BethesdaViewerAnimationClockPolicy.Resolve(clip);

        Assert.Equal(4f, window.RawOriginSeconds);
        Assert.Equal(8f, window.PresentationDurationSeconds);
        Assert.Equal(12f, window.ClampEndClockSeconds);
        Assert.Equal(8f, BethesdaViewerAnimationClockPolicy.GetDisplayTime(clip, window, 12f));
    }

    [Fact]
    public void Resolve_MixedSignedClocksCoversEveryTrackWithoutDoubleWrapping()
    {
        var clip = Clip(
            loops: false,
            Track(frequency: 0.5f, phase: 0f),
            Track(frequency: -2f, phase: 12f, nodeIndex: 1));

        var window = BethesdaViewerAnimationClockPolicy.Resolve(clip);

        Assert.Equal(3f, window.RawOriginSeconds);
        Assert.Equal(9f, window.PresentationDurationSeconds);
        Assert.Equal(12f, window.ClampEndClockSeconds);
        Assert.Equal(12f, BethesdaViewerAnimationClockPolicy.GetSeekClock(window, 99f));
    }

    [Fact]
    public void Resolve_LoopUsesLongestTrackPeriodOnlyAsDisplayHorizon()
    {
        var clip = Clip(
            loops: true,
            Track(frequency: 0.5f, phase: 0f),
            Track(frequency: 2f, phase: 0f, nodeIndex: 1));

        var window = BethesdaViewerAnimationClockPolicy.Resolve(clip);

        Assert.Equal(1f, window.RawOriginSeconds);
        Assert.Equal(8f, window.PresentationDurationSeconds);
        Assert.Equal(1f, BethesdaViewerAnimationClockPolicy.GetDisplayTime(clip, window, 10f));
    }

    [Fact]
    public void Resolve_ZeroFrequencyNodeClockIsDormant()
    {
        var clip = Clip(loops: true, Track(frequency: 0f, phase: 3f));

        var window = BethesdaViewerAnimationClockPolicy.Resolve(clip);

        Assert.Equal(0f, window.PresentationDurationSeconds);
        Assert.Equal(clip.StartTime, window.RawOriginSeconds);
        Assert.Equal(clip.StartTime, window.ClampEndClockSeconds);
    }

    private static BethesdaViewerAnimationClip Clip(
        bool loops,
        params BethesdaViewerNodeAnimationTrack[] tracks)
    {
        return new BethesdaViewerAnimationClip(
            "Clock",
            2f,
            6f,
            loops,
            tracks,
            [],
            []);
    }

    private static BethesdaViewerNodeAnimationTrack Track(
        float frequency,
        float phase,
        int nodeIndex = 0)
    {
        return new BethesdaViewerNodeAnimationTrack(
            nodeIndex,
            frequency,
            phase,
            BethesdaViewerKeyInterpolation.Linear,
            [new BethesdaViewerQuaternionKey(2f, Quaternion.Identity)],
            BethesdaViewerKeyInterpolation.Linear,
            [],
            BethesdaViewerKeyInterpolation.Linear,
            []);
    }
}
