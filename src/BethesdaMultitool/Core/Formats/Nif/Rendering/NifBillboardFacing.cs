using System.Numerics;
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
        var orientedArea = Vector3.Zero;
        var totalArea = 0f;

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
    ///     Yaw that rotates <paramref name="authoredFrontAxis" /> onto the horizontal camera
    ///     direction. For +Y this is exactly the historical <c>atan2(toCamera) - pi/2</c> route.
    /// </summary>
    internal static float ResolveYaw(Vector2 authoredFrontAxis, Vector2 toCamera)
    {
        var front = NormalizeOr(authoredFrontAxis, Vector2.UnitY);
        var facing = NormalizeOr(toCamera, Vector2.UnitX);
        return MathF.Atan2(facing.Y, facing.X) - MathF.Atan2(front.Y, front.X);
    }

    private static Vector2 NormalizeOr(Vector2 value, Vector2 fallback)
    {
        var lengthSquared = value.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > Epsilon
            ? value / MathF.Sqrt(lengthSquared)
            : fallback;
    }
}
