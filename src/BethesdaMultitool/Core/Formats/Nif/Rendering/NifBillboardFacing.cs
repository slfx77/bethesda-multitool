using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Resolves the authored horizontal front axis of a rigid <c>NiBillboardNode</c> submesh from
///     its indexed winding. Bethesda effects do not use one universal local-facing convention:
///     many cards face +Y, while FNV's <c>FXFireMeshSmall</c> / <c>FireBall09</c> faces -Y. The
///     rasterizer culls by winding, so re-aiming the wrong side at the camera can suppress every
///     triangle even though the renderer issued the draw.
/// </summary>
internal static class NifBillboardFacing
{
    // Only override the historical +Y convention when the mesh has a coherent horizontal front.
    // Closed/symmetric meshes have cancelling face normals and deliberately retain the old route.
    private const float MinHorizontalCoherence = 0.25f;
    private const float Epsilon = 1e-8f;

    /// <summary>
    ///     Returns the area-weighted horizontal front direction established by triangle winding.
    ///     Degenerate, closed, or predominantly horizontal geometry falls back to +Y, preserving
    ///     the renderer's pre-existing billboard convention.
    /// </summary>
    internal static Vector2 ResolveFrontAxis(
        ReadOnlySpan<GpuMeshUploader.GpuVertex> vertices,
        ReadOnlySpan<ushort> indices)
    {
        ResolveOrientedArea(vertices, indices, out var orientedArea, out var totalArea);

        var horizontal = new Vector2(orientedArea.X, orientedArea.Y);
        var horizontalLength = horizontal.Length();
        if (!float.IsFinite(horizontalLength) || totalArea <= Epsilon ||
            horizontalLength < totalArea * MinHorizontalCoherence)
        {
            return Vector2.UnitY;
        }

        return horizontal / horizontalLength;
    }

    /// <summary>
    ///     Returns the coherent three-dimensional visible front. Unlike <see cref="ResolveFrontAxis" />,
    ///     this preserves a horizontal card's ±Z normal so Skyrim's face-center flame planes can pitch
    ///     toward the eye instead of being forced through the rotate-about-up path.
    /// </summary>
    internal static Vector3 ResolveFrontNormal(
        ReadOnlySpan<GpuMeshUploader.GpuVertex> vertices,
        ReadOnlySpan<ushort> indices)
    {
        ResolveOrientedArea(vertices, indices, out var orientedArea, out var totalArea);
        var length = orientedArea.Length();
        return float.IsFinite(length) && totalArea > Epsilon &&
               length >= totalArea * MinHorizontalCoherence
            ? orientedArea / length
            : Vector3.UnitY;
    }

    /// <summary>
    ///     Yaw that rotates <paramref name="authoredFrontAxis" /> onto the horizontal camera
    ///     direction. For +Y this is exactly the historical <c>atan2(toCamera) - pi/2</c> route.
    /// </summary>
    internal static float ResolveYaw(Vector2 authoredFrontAxis, Vector2 toCamera)
    {
        var front = NormalizeOr(authoredFrontAxis, Vector2.UnitY);
        var facing = NormalizeOr(toCamera, Vector2.UnitX);
        return MathF.Atan2(facing.Y, facing.X) - MathF.Atan2(front.Y, front.X);
    }

    /// <summary>
    ///     Resolves the authored billboard mode to a camera-facing rotation. Face-camera modes use
    ///     the inverse camera-forward direction; face-center modes use the per-object eye vector.
    ///     Rigid and minimized variants share the minimum-arc orientation until roll-controller data
    ///     is carried by the renderer, while every rotate-about-up spelling retains the historical yaw.
    /// </summary>
    internal static Matrix4x4 ResolveRotation(
        NifBillboardMode mode,
        Vector3 authoredFrontNormal,
        Vector3 toCamera,
        Vector3 cameraForward)
    {
        var target = mode is NifBillboardMode.AlwaysFaceCamera or NifBillboardMode.RigidFaceCamera
            ? NormalizeOr(-cameraForward, NormalizeOr(toCamera, Vector3.UnitY))
            : NormalizeOr(toCamera, Vector3.UnitY);

        if (mode is NifBillboardMode.RotateAboutUp or NifBillboardMode.BsRotateAboutUp or
            NifBillboardMode.RotateAboutUp2)
        {
            var authoredHorizontal = new Vector2(authoredFrontNormal.X, authoredFrontNormal.Y);
            var targetHorizontal = new Vector2(target.X, target.Y);
            return Matrix4x4.CreateRotationZ(ResolveYaw(authoredHorizontal, targetHorizontal));
        }

        return ResolveMinimumArc(
            NormalizeOr(authoredFrontNormal, Vector3.UnitY),
            target);
    }

    private static Matrix4x4 ResolveMinimumArc(Vector3 from, Vector3 to)
    {
        var dot = Math.Clamp(Vector3.Dot(from, to), -1f, 1f);
        if (dot >= 1f - 1e-6f)
        {
            return Matrix4x4.Identity;
        }

        var axis = Vector3.Cross(from, to);
        if (axis.LengthSquared() <= Epsilon)
        {
            // Anti-parallel vectors have infinitely many valid half-turn axes. Pick the most stable
            // perpendicular cardinal axis so the result is deterministic across tiny camera drift.
            var helper = MathF.Abs(from.Z) < 0.9f ? Vector3.UnitZ : Vector3.UnitY;
            axis = Vector3.Cross(from, helper);
        }

        axis = Vector3.Normalize(axis);
        return Matrix4x4.CreateFromAxisAngle(axis, MathF.Acos(dot));
    }

    private static void ResolveOrientedArea(
        ReadOnlySpan<GpuMeshUploader.GpuVertex> vertices,
        ReadOnlySpan<ushort> indices,
        out Vector3 orientedArea,
        out float totalArea)
    {
        orientedArea = Vector3.Zero;
        totalArea = 0f;
        for (var i = 0; i + 2 < indices.Length; i += 3)
        {
            var i0 = indices[i];
            var i1 = indices[i + 1];
            var i2 = indices[i + 2];
            if (i0 >= vertices.Length || i1 >= vertices.Length || i2 >= vertices.Length)
            {
                continue;
            }

            var cross = Vector3.Cross(
                vertices[i1].Position - vertices[i0].Position,
                vertices[i2].Position - vertices[i0].Position);
            var area = cross.Length();
            if (!float.IsFinite(area) || area <= Epsilon)
            {
                continue;
            }

            orientedArea += cross;
            totalArea += area;
        }
    }

    private static Vector2 NormalizeOr(Vector2 value, Vector2 fallback)
    {
        var lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > Epsilon
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }

    private static Vector3 NormalizeOr(Vector3 value, Vector3 fallback)
    {
        var lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > Epsilon
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }
}
