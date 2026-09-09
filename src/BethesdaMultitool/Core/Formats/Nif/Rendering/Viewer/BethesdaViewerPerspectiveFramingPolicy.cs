using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;

/// <summary>
///     Perspective fit policy for assembled actor previews. Raw NIF inspection deliberately retains
///     the legacy bounding-sphere camera: its arbitrary model orientations make a stable orbit radius
///     more useful than a tightly composed initial view. Actor appearances, by contrast, have a known
///     initial bearing and benefit from fitting their projected bounds to the actual viewport aspect.
///     The projected fit is especially important for long creatures: a tail extending mostly in camera
///     depth should not shrink the whole creature as much as a three-dimensional bounding sphere does.
/// </summary>
internal static class BethesdaViewerPerspectiveFramingPolicy
{
    internal const float ActorFramingMargin = 1.1f;

    internal static bool ShouldUseProjectedBoundsFit(BethesdaViewerScenePurpose purpose)
    {
        return purpose is BethesdaViewerScenePurpose.NpcAppearance or
            BethesdaViewerScenePurpose.CreatureAppearance;
    }

    /// <summary>
    ///     Finds the shortest eye-to-target distance that keeps every AABB corner inside a
    ///     perspective frustum. <paramref name="eyeDirection" /> points from target to eye; the
    ///     remaining basis vectors are the camera's screen-right and screen-up directions.
    /// </summary>
    internal static bool TryResolveProjectedBoundsDistance(
        BethesdaViewerBounds bounds,
        Vector3 eyeDirection,
        Vector3 right,
        Vector3 up,
        float verticalFieldOfViewRadians,
        float aspectRatio,
        out float distance,
        float framingMargin = ActorFramingMargin)
    {
        distance = 0f;
        if (!bounds.IsFinite ||
            bounds.Maximum.X < bounds.Minimum.X ||
            bounds.Maximum.Y < bounds.Minimum.Y ||
            bounds.Maximum.Z < bounds.Minimum.Z ||
            !TryNormalize(eyeDirection, out eyeDirection) ||
            !TryNormalize(right, out right) ||
            !TryNormalize(up, out up) ||
            !float.IsFinite(verticalFieldOfViewRadians) ||
            verticalFieldOfViewRadians <= 0.1f ||
            verticalFieldOfViewRadians >= MathF.PI - 0.1f ||
            !float.IsFinite(aspectRatio) ||
            aspectRatio <= 0f ||
            !float.IsFinite(framingMargin) ||
            framingMargin < 1f)
        {
            return false;
        }

        var tanVerticalHalfFov = MathF.Tan(verticalFieldOfViewRadians * 0.5f);
        var tanHorizontalHalfFov = tanVerticalHalfFov * aspectRatio;
        if (!(tanVerticalHalfFov > 0f) ||
            !float.IsFinite(tanVerticalHalfFov) ||
            !(tanHorizontalHalfFov > 0f) ||
            !float.IsFinite(tanHorizontalHalfFov))
        {
            return false;
        }

        var halfExtents = bounds.Size * 0.5f;
        // Keep even a corner lying exactly on the eye axis in front of the near plane. This is a
        // depth guard, not visible framing padding; screen-space padding comes from framingMargin.
        var depthClearance = MathF.Max(halfExtents.Length() * 0.01f, 0.0001f);
        var requiredDistance = depthClearance;
        for (var x = -1; x <= 1; x += 2)
        {
            for (var y = -1; y <= 1; y += 2)
            {
                for (var z = -1; z <= 1; z += 2)
                {
                    var offset = new Vector3(
                        halfExtents.X * x,
                        halfExtents.Y * y,
                        halfExtents.Z * z);
                    var towardEye = Vector3.Dot(offset, eyeDirection);
                    var horizontalDistance = MathF.Abs(Vector3.Dot(offset, right)) / tanHorizontalHalfFov;
                    var verticalDistance = MathF.Abs(Vector3.Dot(offset, up)) / tanVerticalHalfFov;
                    var projectedDistance = MathF.Max(horizontalDistance, verticalDistance);
                    requiredDistance = MathF.Max(
                        requiredDistance,
                        towardEye + projectedDistance * framingMargin + depthClearance);
                }
            }
        }

        if (!(requiredDistance > 0f) || !float.IsFinite(requiredDistance))
        {
            return false;
        }

        distance = requiredDistance;
        return true;
    }

    private static bool TryNormalize(Vector3 value, out Vector3 normalized)
    {
        var lengthSquared = value.LengthSquared();
        if (!float.IsFinite(lengthSquared) || lengthSquared <= 1e-8f)
        {
            normalized = default;
            return false;
        }

        normalized = value / MathF.Sqrt(lengthSquared);
        return true;
    }
}
