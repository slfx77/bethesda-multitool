using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Downward ray versus a PLACED object-bounds box (the walk-mode cold-OBND ground fallback).
///     The previous fallback ignored placement rotation — it tested the sample point against the
///     unrotated local rectangle offset by the placement origin, so a yaw-rotated piece (sidewalk
///     medians, road strips) offered ground far outside its true footprint and none where the
///     object actually sits. The ray is transformed into the placement's local frame and slab-
///     intersected against the local AABB, mirroring the warm-mesh raycast's transform handling.
/// </summary>
internal static class WalkColdBoundsProbe
{
    /// <summary>
    ///     Casts a world-space down ray from (<paramref name="worldX" />, <paramref name="worldY" />,
    ///     <paramref name="rayOriginZ" />) against the placed box and returns the world Z of the entry
    ///     surface. False when the ray misses, starts inside the box (its top is above the probe
    ///     window's origin — never a step-up candidate), or the placement is degenerate.
    /// </summary>
    public static bool TryRaycastDownToTop(
        Vector3 localMin,
        Vector3 localMax,
        in Matrix4x4 world,
        in Matrix4x4 inverseWorld,
        float worldX,
        float worldY,
        float rayOriginZ,
        out float surfaceWorldZ)
    {
        surfaceWorldZ = 0f;
        var min = Vector3.Min(localMin, localMax);
        var max = Vector3.Max(localMin, localMax);

        var origin = Vector3.Transform(new Vector3(worldX, worldY, rayOriginZ), inverseWorld);
        var direction = Vector3.TransformNormal(new Vector3(0f, 0f, -1f), inverseWorld);
        if (!IsFinite(origin) || !IsFinite(direction) || direction.LengthSquared() <= 1e-12f)
        {
            return false;
        }

        var tEnter = float.MinValue;
        var tExit = float.MaxValue;
        if (!Slab(origin.X, direction.X, min.X, max.X, ref tEnter, ref tExit) ||
            !Slab(origin.Y, direction.Y, min.Y, max.Y, ref tEnter, ref tExit) ||
            !Slab(origin.Z, direction.Z, min.Z, max.Z, ref tEnter, ref tExit))
        {
            return false;
        }

        // tEnter <= 0 means the ray origin is already inside (or past) the box — the box surface is
        // above the probe origin, which the step window could never accept. Mirrors the old
        // behavior where an over-tall top was returned and then rejected by AllowsPlacedSurface.
        if (tEnter <= 0f || tEnter > tExit) return false;

        var hitLocal = origin + direction * tEnter;
        surfaceWorldZ = Vector3.Transform(hitLocal, world).Z;
        return float.IsFinite(surfaceWorldZ);
    }

    private static bool Slab(float origin, float direction, float min, float max, ref float tEnter, ref float tExit)
    {
        if (MathF.Abs(direction) <= 1e-9f)
        {
            return origin >= min && origin <= max;
        }

        var t0 = (min - origin) / direction;
        var t1 = (max - origin) / direction;
        if (t0 > t1) (t0, t1) = (t1, t0);
        tEnter = MathF.Max(tEnter, t0);
        tExit = MathF.Min(tExit, t1);
        return tEnter <= tExit;
    }

    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
}
