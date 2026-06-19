using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class RendererCameraMotionTests
{
    [Theory]
    [InlineData(null, "static")]
    [InlineData("", "static")]
    [InlineData("static", "static")]
    [InlineData("FORWARD", "forward")]
    [InlineData("orbit", "orbit")]
    [InlineData("sweep", "sweep")]
    public void TryParseKind_AcceptsBuiltInModes(string? value, string expectedName)
    {
        Assert.True(RendererCameraMotion.TryParseKind(expectedName, out var expected));
        Assert.True(RendererCameraMotion.TryParseKind(value, out var actual));
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryParseKind_RejectsUnknownMode()
    {
        Assert.False(RendererCameraMotion.TryParseKind("camera-path", out _));
    }

    [Fact]
    public void Forward_MovesAlongGroundPlaneAndPreservesZPitchAndRenderDistance()
    {
        var initial = new RendererProfilerCameraPose(
            new Vector3(10f, 20f, 30f),
            MathF.PI / 2f,
            0.35f,
            4096f);

        var pose = RendererCameraMotion.Evaluate(RendererCameraMotionKind.Forward, initial, 100f, 2.0);

        AssertClose(210f, pose.Position.X);
        AssertClose(20f, pose.Position.Y);
        AssertClose(30f, pose.Position.Z);
        AssertClose(initial.Yaw, pose.Yaw);
        AssertClose(initial.Pitch, pose.Pitch);
        AssertClose(initial.RenderDistance, pose.RenderDistance);
    }

    [Fact]
    public void Orbit_AtZeroElapsedStartsAtInitialPose()
    {
        var initial = new RendererProfilerCameraPose(
            new Vector3(128f, -256f, 768f),
            0.72f,
            -0.2f,
            8192f);

        var pose = RendererCameraMotion.Evaluate(RendererCameraMotionKind.Orbit, initial, 2048f, 0.0);

        AssertClose(initial.Position.X, pose.Position.X);
        AssertClose(initial.Position.Y, pose.Position.Y);
        AssertClose(initial.Position.Z, pose.Position.Z);
        AssertClose(initial.Yaw, pose.Yaw);
        AssertClose(initial.Pitch, pose.Pitch);
        AssertClose(initial.RenderDistance, pose.RenderDistance);
    }

    [Fact]
    public void Sweep_FirstLegMovesEastAndFacesEast()
    {
        var initial = new RendererProfilerCameraPose(
            Vector3.Zero,
            0f,
            -0.15f,
            4096f);

        var pose = RendererCameraMotion.Evaluate(RendererCameraMotionKind.Sweep, initial, 512f, 1.0);

        AssertClose(512f, pose.Position.X);
        AssertClose(0f, pose.Position.Y);
        AssertClose(0f, pose.Position.Z);
        AssertClose(MathF.PI / 2f, pose.Yaw);
        AssertClose(initial.Pitch, pose.Pitch);
    }

    [Theory]
    [InlineData(0f, 0f, 0f, 1f, 0f)]
    [InlineData(1.5707964f, 0f, 1f, 0f, 0f)]
    [InlineData(0f, 1.5707964f, 0f, 0f, 1f)]
    public void ForwardFromYawPitch_UsesRendererCameraConvention(
        float yaw,
        float pitch,
        float expectedX,
        float expectedY,
        float expectedZ)
    {
        var forward = RendererCameraMotion.ForwardFromYawPitch(yaw, pitch);

        AssertClose(expectedX, forward.X);
        AssertClose(expectedY, forward.Y);
        AssertClose(expectedZ, forward.Z);
    }

    private static void AssertClose(float expected, float actual)
    {
        Assert.True(MathF.Abs(expected - actual) < 0.0005f, $"Expected {expected}, got {actual}.");
    }
}