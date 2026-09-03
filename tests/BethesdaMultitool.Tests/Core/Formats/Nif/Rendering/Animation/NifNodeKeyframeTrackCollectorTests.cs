using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

public sealed class NifNodeKeyframeTrackCollectorTests
{
    [Fact]
    public void CompatibleActiveMovingReverseControllersRetainTheirExactClock()
    {
        var tracks = new Dictionary<int, NifNodeTrack>
        {
            [10] = MovingTrack("Rock_1"),
            [20] = MovingTrack("Rock_2"),
            // A static controller cannot affect the presentation period and is deliberately ignored.
            [30] = StaticTrack("Static")
        };
        var headers = new Dictionary<int, NifTimeControllerHeader>
        {
            [10] = ReverseHeader(),
            [20] = ReverseHeader(),
            [30] = ReverseHeader() with { Frequency = 7f, StopTime = 2f }
        };

        var cycle = NifNodeKeyframeTrackCollector.ResolveCompatibleReverseCycle(tracks, headers);

        Assert.NotNull(cycle);
        Assert.Equal(1f, cycle.Value.Frequency);
        Assert.Equal(0f, cycle.Value.Phase);
        Assert.Equal(0f, cycle.Value.StartTime);
        Assert.Equal(49.06667f, cycle.Value.StopTime);
        Assert.Equal(NifCycleType.Reverse, cycle.Value.Cycle);
    }

    [Theory]
    [InlineData("frequency")]
    [InlineData("phase")]
    [InlineData("start")]
    [InlineData("stop")]
    [InlineData("cycle")]
    [InlineData("inactive")]
    [InlineData("non-finite")]
    public void IncompatibleMovingControllerGraphWithholdsTheFullCycle(string mismatch)
    {
        var first = ReverseHeader();
        var second = mismatch switch
        {
            "frequency" => first with { Frequency = 2f },
            "phase" => first with { Phase = 1f },
            "start" => first with { StartTime = 1f },
            "stop" => first with { StopTime = 48f },
            "cycle" => first with { Flags = 0x0008 },
            "inactive" => first with { Flags = 0x0002 },
            "non-finite" => first with { StopTime = float.PositiveInfinity },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatch))
        };
        var tracks = new Dictionary<int, NifNodeTrack>
        {
            [10] = MovingTrack("Rock_1"),
            [20] = MovingTrack("Rock_2")
        };
        var headers = new Dictionary<int, NifTimeControllerHeader>
        {
            [10] = first,
            [20] = second
        };

        Assert.Null(NifNodeKeyframeTrackCollector.ResolveCompatibleReverseCycle(tracks, headers));
    }

    [Fact]
    public void MissingControllerForAMovingTrackWithholdsTheFullCycle()
    {
        var tracks = new Dictionary<int, NifNodeTrack>
        {
            [10] = MovingTrack("Rock_1"),
            [20] = MovingTrack("Rock_2")
        };
        var headers = new Dictionary<int, NifTimeControllerHeader>
        {
            [10] = ReverseHeader()
        };

        Assert.Null(NifNodeKeyframeTrackCollector.ResolveCompatibleReverseCycle(tracks, headers));
    }

    private static NifTimeControllerHeader ReverseHeader()
    {
        return new NifTimeControllerHeader(
            NextControllerRef: -1,
            Flags: 0x000A,
            Frequency: 1f,
            Phase: 0f,
            StartTime: 0f,
            StopTime: 49.06667f,
            TargetRef: 0);
    }

    private static NifNodeTrack MovingTrack(string name)
    {
        return new NifNodeTrack(
            name,
            1f,
            0f,
            NifKeyInterpolation.Linear,
            [],
            NifKeyInterpolation.Linear,
            [new NifVec3Key(0f, Vector3.Zero), new NifVec3Key(49.06667f, Vector3.One)],
            NifKeyInterpolation.Linear,
            []);
    }

    private static NifNodeTrack StaticTrack(string name)
    {
        return new NifNodeTrack(
            name,
            1f,
            0f,
            NifKeyInterpolation.Linear,
            [],
            NifKeyInterpolation.Linear,
            [new NifVec3Key(0f, Vector3.Zero)],
            NifKeyInterpolation.Linear,
            []);
    }
}
