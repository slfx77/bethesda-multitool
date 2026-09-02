using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerAnimationValidatorTests
{
    [Fact]
    public void RejectsNonFiniteTrackClock()
    {
        var clip = Clip(Track(frequency: float.NaN));

        Assert.False(BethesdaViewerAnimationValidator.TryValidate(clip, 1, 0, out var error));
        Assert.NotNull(error);
        Assert.Contains("malformed key data", error);
    }

    [Fact]
    public void RejectsDescendingKeyTimes()
    {
        var track = Track() with
        {
            TranslationKeys =
            [
                new BethesdaViewerVector3Key(1f, Vector3.One),
                new BethesdaViewerVector3Key(0f, Vector3.Zero)
            ]
        };

        Assert.False(BethesdaViewerAnimationValidator.TryValidate(Clip(track), 1, 0, out var error));
        Assert.NotNull(error);
        Assert.Contains("malformed key data", error);
    }

    [Fact]
    public void RejectsDuplicateNodeTargets()
    {
        var track = Track();
        var clip = new BethesdaViewerAnimationClip(
            "Duplicate",
            0f,
            1f,
            true,
            [track, track],
            [],
            []);

        Assert.False(BethesdaViewerAnimationValidator.TryValidate(clip, 1, 0, out var error));
        Assert.NotNull(error);
        Assert.Contains("duplicated", error);
    }

    [Fact]
    public void RejectsXyzEulerLabelWithoutEulerChannels()
    {
        var track = Track() with
        {
            RotationInterpolation = BethesdaViewerKeyInterpolation.XyzEuler
        };

        Assert.False(BethesdaViewerAnimationValidator.TryValidate(Clip(track), 1, 0, out var error));
        Assert.NotNull(error);
        Assert.Contains("malformed key data", error);
    }

    [Fact]
    public void RejectsQuaternionWhoseFiniteComponentsOverflowItsLength()
    {
        var track = Track() with
        {
            RotationKeys =
            [
                new BethesdaViewerQuaternionKey(
                    0f,
                    new Quaternion(float.MaxValue, float.MaxValue, 0f, 0f))
            ],
            TranslationKeys = []
        };

        Assert.False(BethesdaViewerAnimationValidator.TryValidate(Clip(track), 1, 0, out var error));
        Assert.NotNull(error);
        Assert.Contains("malformed key data", error);
    }

    [Fact]
    public void RejectsFiniteEndpointsWhoseDurationOverflows()
    {
        var clip = Clip(Track()) with
        {
            StartTime = -float.MaxValue,
            EndTime = float.MaxValue
        };

        Assert.False(BethesdaViewerAnimationValidator.TryValidate(clip, 1, 0, out var error));
        Assert.NotNull(error);
        Assert.Contains("play window", error);
    }

    private static BethesdaViewerAnimationClip Clip(BethesdaViewerNodeAnimationTrack track)
    {
        return new BethesdaViewerAnimationClip(
            "Idle",
            0f,
            1f,
            true,
            [track],
            [],
            []);
    }

    private static BethesdaViewerNodeAnimationTrack Track(float frequency = 1f)
    {
        return new BethesdaViewerNodeAnimationTrack(
            0,
            frequency,
            0f,
            BethesdaViewerKeyInterpolation.Linear,
            [],
            BethesdaViewerKeyInterpolation.Linear,
            [new BethesdaViewerVector3Key(0f, Vector3.Zero)],
            BethesdaViewerKeyInterpolation.Linear,
            []);
    }
}
