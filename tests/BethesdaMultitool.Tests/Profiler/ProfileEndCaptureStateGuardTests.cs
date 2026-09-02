using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.WorldData;
using BethesdaRendererProfiler;
using Xunit;

namespace BethesdaMultitool.Tests.Profiler;

public sealed class ProfileEndCaptureStateGuardTests
{
    private static readonly RendererProfilerCameraPose RetainedPose =
        new(new Vector3(100_000f, -20_000f, 3_000f), 1.25f, -0.2f, 61_440f);

    [Fact]
    public void TryValidate_AcceptsUnchangedScoredStateAndCleanAuthoritativeCensus()
    {
        var clean = default(CaptureSceneCensus);

        var accepted = ProfileEndCaptureStateGuard.TryValidate(
            RetainedPose,
            RetainedPose,
            60f,
            60f,
            (1920, 1080),
            (1920, 1080),
            clean,
            out var error);

        Assert.True(accepted);
        Assert.Equal(string.Empty, error);
    }

    [Fact]
    public void TryValidate_RejectsPoseOrFovDriftBeyondTightTolerance()
    {
        var clean = default(CaptureSceneCensus);
        var moved = RetainedPose with
        {
            Position = RetainedPose.Position + new Vector3(0.01f, 0f, 0f)
        };

        Assert.False(ProfileEndCaptureStateGuard.TryValidate(
            RetainedPose, moved, 60f, 60f, (1920, 1080), (1920, 1080), clean, out var poseError));
        Assert.Contains("camera position drifted", poseError, StringComparison.Ordinal);

        var rotated = RetainedPose with { Yaw = RetainedPose.Yaw + 0.00001f };
        Assert.False(ProfileEndCaptureStateGuard.TryValidate(
            RetainedPose, rotated, 60f, 60f, (1920, 1080), (1920, 1080), clean, out var angleError));
        Assert.Contains("camera orientation drifted", angleError, StringComparison.Ordinal);

        var distanceChanged = RetainedPose with
        {
            RenderDistance = RetainedPose.RenderDistance + 0.01f
        };
        Assert.False(ProfileEndCaptureStateGuard.TryValidate(
            RetainedPose,
            distanceChanged,
            60f,
            60f,
            (1920, 1080),
            (1920, 1080),
            clean,
            out var distanceError));
        Assert.Contains("render distance drifted", distanceError, StringComparison.Ordinal);

        Assert.False(ProfileEndCaptureStateGuard.TryValidate(
            RetainedPose, RetainedPose, 60f, 60.01f, (1920, 1080), (1920, 1080), clean, out var fovError));
        Assert.Contains("camera FOV drifted", fovError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidate_RejectsMissingOrChangedD3dViewport()
    {
        var clean = default(CaptureSceneCensus);

        Assert.False(ProfileEndCaptureStateGuard.TryValidate(
            RetainedPose, RetainedPose, 60f, 60f, (1920, 1080), null, clean, out var missingError));
        Assert.Contains("unavailable", missingError, StringComparison.Ordinal);

        Assert.False(ProfileEndCaptureStateGuard.TryValidate(
            RetainedPose, RetainedPose, 60f, 60f, (1920, 1080), (1919, 1080), clean, out var driftError));
        Assert.Contains("D3D12 viewport drifted", driftError, StringComparison.Ordinal);
    }

    [Fact]
    public void TryValidate_RejectsDirtyCaptureSceneCensusWithNamedAuthoritativeTerm()
    {
        var dirty = default(CaptureSceneCensus) with { TexturePendingResolves = 2 };

        var accepted = ProfileEndCaptureStateGuard.TryValidate(
            RetainedPose,
            RetainedPose,
            60f,
            60f,
            (1920, 1080),
            (1920, 1080),
            dirty,
            out var error);

        Assert.False(accepted);
        Assert.Contains("not quiescent", error, StringComparison.Ordinal);
        Assert.Contains("TexturePendingResolves=2", error, StringComparison.Ordinal);
    }
}
