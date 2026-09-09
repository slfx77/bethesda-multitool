using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Viewer;

public sealed class BethesdaViewerAnimationPoseEvaluatorTests
{
    [Fact]
    public void SparseTrackPreservesRestChannelsAndComposesThroughParent()
    {
        var rootRest = Matrix4x4.CreateTranslation(10f, 0f, 0f);
        var childRest = Matrix4x4.CreateTranslation(0f, 2f, 0f);
        var clip = new BethesdaViewerAnimationClip(
            "Idle",
            0f,
            2f,
            true,
            [
                new BethesdaViewerNodeAnimationTrack(
                    1,
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Linear,
                    [],
                    BethesdaViewerKeyInterpolation.Linear,
                    [
                        new BethesdaViewerVector3Key(0f, new Vector3(0f, 2f, 0f)),
                        new BethesdaViewerVector3Key(2f, new Vector3(0f, 6f, 0f))
                    ],
                    BethesdaViewerKeyInterpolation.Linear,
                    [])
            ],
            [],
            []);
        var evaluator = new BethesdaViewerAnimationPoseEvaluator(
            [rootRest, childRest],
            [null, 0],
            clip);
        var worlds = new Matrix4x4[2];

        evaluator.EvaluateNodeWorlds(1f, worlds);

        AssertClose(new Vector3(10f, 0f, 0f), worlds[0].Translation);
        AssertClose(new Vector3(10f, 4f, 0f), worlds[1].Translation);
    }

    [Fact]
    public void ConstantInterpolationHoldsLowerKeyAndNonLoopingTimeClamps()
    {
        var clip = new BethesdaViewerAnimationClip(
            "Hold",
            1f,
            3f,
            false,
            [
                new BethesdaViewerNodeAnimationTrack(
                    0,
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Linear,
                    [],
                    BethesdaViewerKeyInterpolation.Constant,
                    [
                        new BethesdaViewerVector3Key(1f, Vector3.One),
                        new BethesdaViewerVector3Key(3f, new Vector3(9f))
                    ],
                    BethesdaViewerKeyInterpolation.Linear,
                    [])
            ],
            [],
            []);
        var evaluator = new BethesdaViewerAnimationPoseEvaluator(
            [Matrix4x4.Identity],
            [null],
            clip);
        var worlds = new Matrix4x4[1];

        evaluator.EvaluateNodeWorlds(2.5f, worlds);
        AssertClose(Vector3.One, worlds[0].Translation);

        evaluator.EvaluateNodeWorlds(20f, worlds);
        AssertClose(new Vector3(9f), worlds[0].Translation);
    }

    [Fact]
    public void BsplineTrackSamplesCubicChannelsAndNormalizesDefaultRotation()
    {
        var transform = new NifBsplineTransformData(
            0f,
            1f,
            null,
            new Quaternion(0f, 0f, 0f, 2f),
            null,
            [Vector3.Zero, Vector3.Zero, Vector3.Zero, new Vector3(8f, 0f, 0f)],
            null,
            [1f, 1f, 1f, 5f]);
        var clip = new BethesdaViewerAnimationClip(
            "Cubic",
            0f,
            1f,
            false,
            [
                new BethesdaViewerNodeAnimationTrack(
                    0,
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BsplineTransform: transform)
            ],
            [],
            []);
        var evaluator = new BethesdaViewerAnimationPoseEvaluator(
            [Matrix4x4.Identity],
            [null],
            clip);
        var worlds = new Matrix4x4[1];

        evaluator.EvaluateNodeWorlds(0.5f, worlds);

        AssertClose(new Vector3(1f, 0f, 0f), worlds[0].Translation);
        Assert.Equal(1.5f, worlds[0].M11, 5);
        Assert.Equal(1.5f, worlds[0].M22, 5);
        Assert.Equal(1.5f, worlds[0].M33, 5);
    }

    [Fact]
    public void BsplineTrackWithMismatchedControlPointCountsIsRejected()
    {
        var malformed = new NifBsplineTransformData(
            0f,
            1f,
            null,
            null,
            null,
            Enumerable.Repeat(Vector3.Zero, 4).ToArray(),
            Enumerable.Repeat(Quaternion.Identity, 5).ToArray(),
            null);
        var clip = new BethesdaViewerAnimationClip(
            "Malformed",
            0f,
            1f,
            false,
            [
                new BethesdaViewerNodeAnimationTrack(
                    0,
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BsplineTransform: malformed)
            ],
            [],
            []);

        Assert.Throws<InvalidDataException>(() => new BethesdaViewerAnimationPoseEvaluator(
            [Matrix4x4.Identity],
            [null],
            clip));
    }

    [Fact]
    public void BsplineTrackWithOverflowingQuaternionLengthIsRejected()
    {
        var malformed = new NifBsplineTransformData(
            0f,
            1f,
            null,
            new Quaternion(1e29f, 1e29f, 1e29f, 1e29f),
            1f,
            null,
            null,
            null);
        var clip = new BethesdaViewerAnimationClip(
            "Malformed",
            0f,
            1f,
            false,
            [
                new BethesdaViewerNodeAnimationTrack(
                    0,
                    1f,
                    0f,
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BethesdaViewerKeyInterpolation.Constant,
                    [],
                    BsplineTransform: malformed)
            ],
            [],
            []);

        Assert.Throws<InvalidDataException>(() => new BethesdaViewerAnimationPoseEvaluator(
            [Matrix4x4.Identity],
            [null],
            clip));
    }

    [Fact]
    public void MapTime_ExtremeFiniteClockAndFrequencyStillReturnFiniteLoopTime()
    {
        var mapped = BethesdaViewerAnimationPoseEvaluator.MapTime(
            float.MaxValue,
            float.MaxValue,
            0f,
            0f,
            2f,
            true);

        Assert.True(float.IsFinite(mapped));
        Assert.InRange(mapped, 0f, 2f);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(1f, 1f)]
    [InlineData(4f, 4f)]
    [InlineData(5f, 3f)]
    [InlineData(8f, 0f)]
    [InlineData(-1f, 1f)]
    public void MapTime_PingPongTraversesForwardThenBackward(float clock, float expected)
    {
        var mapped = BethesdaViewerAnimationPoseEvaluator.MapTime(
            clock,
            1f,
            0f,
            0f,
            4f,
            true,
            true);

        Assert.Equal(expected, mapped, 4);
    }

    private static void AssertClose(Vector3 expected, Vector3 actual)
    {
        Assert.InRange(Vector3.Distance(expected, actual), 0f, 1e-5f);
    }
}