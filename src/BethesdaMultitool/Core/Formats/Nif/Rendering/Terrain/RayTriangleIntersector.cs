using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Single-precision Möller–Trumbore ray/triangle intersection. Used by the 3D viewer's walk-mode
///     ground snap (<c>WorldView3DControl.RaycastObjectGround</c>) to ride the camera on the real
///     surface of placed meshes instead of their axis-aligned bounding box.
///     <para>
///         Both faces are tested (no back-face cull): a placed floor/walkway may be authored with
///         either winding, and the camera must land on it regardless of which way its normals point.
///     </para>
/// </summary>
internal static class RayTriangleIntersector
{
    /// <summary>Parallel-ray rejection threshold on the determinant (mesh-local units²).</summary>
    public const float Epsilon = 1e-7f;

    /// <summary>
    ///     Tests <paramref name="dir" />-ray from <paramref name="origin" /> against triangle
    ///     <paramref name="v0" />/<paramref name="v1" />/<paramref name="v2" />. On a forward hit
    ///     (<c>t &gt;= 0</c>) returns <c>true</c> and sets <paramref name="t" /> to the ray parameter
    ///     (the hit point is <c>origin + t · dir</c>; <paramref name="dir" /> need not be normalized).
    /// </summary>
    public static bool Intersect(
        Vector3 origin, Vector3 dir,
        Vector3 v0, Vector3 v1, Vector3 v2,
        out float t)
    {
        t = 0f;
        var edge1 = v1 - v0;
        var edge2 = v2 - v0;
        var pvec = Vector3.Cross(dir, edge2);
        var det = Vector3.Dot(edge1, pvec);
        // |det| ~ 0 → ray lies in the triangle plane; treat as a miss (no well-defined single hit).
        if (det > -Epsilon && det < Epsilon) return false;

        var invDet = 1f / det;
        var tvec = origin - v0;
        var u = Vector3.Dot(tvec, pvec) * invDet;
        if (u < 0f || u > 1f) return false;

        var qvec = Vector3.Cross(tvec, edge1);
        var v = Vector3.Dot(dir, qvec) * invDet;
        if (v < 0f || u + v > 1f) return false;

        var hit = Vector3.Dot(edge2, qvec) * invDet;
        if (hit < 0f) return false; // behind the ray origin

        t = hit;
        return true;
    }
}
