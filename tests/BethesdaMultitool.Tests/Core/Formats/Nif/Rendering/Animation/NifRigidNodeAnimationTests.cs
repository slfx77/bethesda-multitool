using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Animation;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Animation;

/// <summary>
///     Pure playback math for baked rigid-node tracks: sample interpolation, loop wrap, the
///     non-looping clamp, and identity behavior for degenerate tracks.
/// </summary>
public sealed class NifRigidNodeAnimationTests
{
    private static NifRigidNodeAnimation QuarterTurnTrack(bool loops) => new(
        ClipStart: 0f,
        ClipLength: 1f,
        Loops: loops,
        SamplesPerSecond: 2f,
        Rotations:
        [
            Quaternion.Identity,
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 4f),
            Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f),
        ],
        Translations: [Vector3.Zero, new Vector3(0f, 0f, 5f), new Vector3(0f, 0f, 10f)],
        Scales: [Vector3.One, Vector3.One, Vector3.One]);

    [Fact]
    public void SampleBoundaries_ReproduceTheBakedTransforms()
    {
        var track = QuarterTurnTrack(loops: true);

        Assert.Equal(Matrix4x4.Identity, track.Evaluate(0.0), MatrixComparer.Instance);

        var mid = track.Evaluate(0.5);
        Assert.Equal(5f, mid.Translation.Z, 3);

        var toleranceEnd = track.Evaluate(0.999999);
        Assert.Equal(10f, toleranceEnd.Translation.Z, 2);
    }

    [Fact]
    public void MidSampleInterpolation_IsSmooth()
    {
        var track = QuarterTurnTrack(loops: true);

        var quarter = track.Evaluate(0.25);
        Assert.Equal(2.5f, quarter.Translation.Z, 3);

        // Rotation slerps: at t=0.25 the accumulated angle is ~22.5° about Z.
        var rotated = Vector3.Transform(Vector3.UnitX, quarter);
        Assert.Equal(MathF.Cos(MathF.PI / 8f), rotated.X, 3);
        Assert.Equal(MathF.Sin(MathF.PI / 8f), rotated.Y, 3);
    }

    [Fact]
    public void LoopingTrack_WrapsPastTheClipLength()
    {
        var track = QuarterTurnTrack(loops: true);

        var wrapped = track.Evaluate(2.5);
        Assert.Equal(5f, wrapped.Translation.Z, 3);
    }

    [Fact]
    public void NonLoopingTrack_ClampsToTheFinalSample()
    {
        var track = QuarterTurnTrack(loops: false);

        var clamped = track.Evaluate(9.0);
        Assert.Equal(10f, clamped.Translation.Z, 3);
    }

    [Fact]
    public void DegenerateTracks_EvaluateToIdentity()
    {
        var empty = new NifRigidNodeAnimation(0f, 1f, true, 30f, [], [], []);
        Assert.Equal(Matrix4x4.Identity, empty.Evaluate(0.5), MatrixComparer.Instance);

        var zeroLength = new NifRigidNodeAnimation(
            0f, 0f, true, 30f, [Quaternion.Identity], [Vector3.Zero], [Vector3.One]);
        Assert.Equal(Matrix4x4.Identity, zeroLength.Evaluate(0.5), MatrixComparer.Instance);
    }

    private sealed class MatrixComparer : IEqualityComparer<Matrix4x4>
    {
        internal static readonly MatrixComparer Instance = new();

        public bool Equals(Matrix4x4 x, Matrix4x4 y)
        {
            for (var row = 0; row < 4; row++)
            {
                for (var col = 0; col < 4; col++)
                {
                    if (MathF.Abs(x[row, col] - y[row, col]) > 1e-4f) return false;
                }
            }

            return true;
        }

        public int GetHashCode(Matrix4x4 obj) => 0;
    }
}
