using System.Globalization;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaRendererProfiler;

/// <summary>
///     Fail-closed duration-boundary gate for a deterministic profile-end capture. The retained
///     scored state must still describe the live renderer and the existing capture census must say
///     that no content work is in flight before the harness reasserts any state or records pixels.
/// </summary>
internal static class ProfileEndCaptureStateGuard
{
    // These are substantially below a visible camera change. At typical exterior coordinates one
    // float ULP is already larger than the position tolerance, so the gate effectively requires
    // bit identity there while allowing harmless near-zero arithmetic noise.
    internal const float PositionTolerance = 0.001f;
    internal const float AngleToleranceRadians = 0.000001f;
    internal const float RenderDistanceTolerance = 0.001f;
    internal const float FovToleranceDegrees = 0.0001f;

    internal static bool TryValidate(
        in RendererProfilerCameraPose retainedPose,
        in RendererProfilerCameraPose currentPose,
        float retainedFovDegrees,
        float currentFovDegrees,
        (int Width, int Height) retainedViewport,
        (int Width, int Height)? currentViewport,
        in CaptureSceneCensus currentCensus,
        out string error)
    {
        if (!Near(retainedPose.Position.X, currentPose.Position.X, PositionTolerance) ||
            !Near(retainedPose.Position.Y, currentPose.Position.Y, PositionTolerance) ||
            !Near(retainedPose.Position.Z, currentPose.Position.Z, PositionTolerance))
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"camera position drifted: retained=({retainedPose.Position.X:R},{retainedPose.Position.Y:R},{retainedPose.Position.Z:R}) current=({currentPose.Position.X:R},{currentPose.Position.Y:R},{currentPose.Position.Z:R}) tolerance={PositionTolerance:R}");
            return false;
        }

        if (!Near(retainedPose.Yaw, currentPose.Yaw, AngleToleranceRadians) ||
            !Near(retainedPose.Pitch, currentPose.Pitch, AngleToleranceRadians))
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"camera orientation drifted: retainedYawPitch=({retainedPose.Yaw:R},{retainedPose.Pitch:R}) currentYawPitch=({currentPose.Yaw:R},{currentPose.Pitch:R}) toleranceRadians={AngleToleranceRadians:R}");
            return false;
        }

        if (!Near(retainedPose.RenderDistance, currentPose.RenderDistance, RenderDistanceTolerance))
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"render distance drifted: retained={retainedPose.RenderDistance:R} current={currentPose.RenderDistance:R} tolerance={RenderDistanceTolerance:R}");
            return false;
        }

        if (!Near(retainedFovDegrees, currentFovDegrees, FovToleranceDegrees))
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"camera FOV drifted: retainedDegrees={retainedFovDegrees:R} currentDegrees={currentFovDegrees:R} toleranceDegrees={FovToleranceDegrees:R}");
            return false;
        }

        if (currentViewport is not { } viewport)
        {
            error = "current D3D12 viewport is unavailable";
            return false;
        }

        if (viewport != retainedViewport)
        {
            error = string.Create(
                CultureInfo.InvariantCulture,
                $"D3D12 viewport drifted: retained={retainedViewport.Width}x{retainedViewport.Height} current={viewport.Width}x{viewport.Height}");
            return false;
        }

        if (!currentCensus.IsClean)
        {
            var dirt = currentCensus.DescribeDirt(currentCensus);
            error = $"streaming/content census is not quiescent: {dirt}";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool Near(float retained, float current, float tolerance) =>
        float.IsFinite(retained) &&
        float.IsFinite(current) &&
        MathF.Abs(retained - current) <= tolerance;
}
