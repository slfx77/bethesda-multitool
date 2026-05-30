using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     View frustum represented as 6 oriented planes. Built from a view·projection matrix.
///     Used at cell granularity to skip rendering cells outside the camera's view.
/// </summary>
internal readonly record struct Frustum(
    Plane Left,
    Plane Right,
    Plane Bottom,
    Plane Top,
    Plane Near,
    Plane Far)
{
    /// <summary>
    ///     Extracts the 6 frustum planes from a combined view·projection matrix.
    ///     Standard Gribb–Hartmann technique: each plane is a linear combination of two
    ///     rows of the matrix. Plane normals point INTO the frustum (so a point is inside
    ///     when <c>Dot(plane.Normal, p) + plane.D &gt;= 0</c>).
    /// </summary>
    public static Frustum FromViewProjection(Matrix4x4 m)
    {
        var left   = NormalizePlane(new Plane(m.M14 + m.M11, m.M24 + m.M21, m.M34 + m.M31, m.M44 + m.M41));
        var right  = NormalizePlane(new Plane(m.M14 - m.M11, m.M24 - m.M21, m.M34 - m.M31, m.M44 - m.M41));
        var bottom = NormalizePlane(new Plane(m.M14 + m.M12, m.M24 + m.M22, m.M34 + m.M32, m.M44 + m.M42));
        var top    = NormalizePlane(new Plane(m.M14 - m.M12, m.M24 - m.M22, m.M34 - m.M32, m.M44 - m.M42));
        var near   = NormalizePlane(new Plane(m.M13, m.M23, m.M33, m.M43));
        var far    = NormalizePlane(new Plane(m.M14 - m.M13, m.M24 - m.M23, m.M34 - m.M33, m.M44 - m.M43));
        return new Frustum(left, right, bottom, top, near, far);
    }

    /// <summary>
    ///     Tests whether an axis-aligned bounding box intersects (or lies inside) the frustum.
    ///     Uses the "positive-vertex / negative-vertex" test: for each plane, find the AABB
    ///     corner furthest along the plane's normal. If that corner is behind the plane the
    ///     entire box is outside.
    /// </summary>
    public bool IntersectsAabb(Vector3 min, Vector3 max)
    {
        return TestPlane(Left, min, max)
            && TestPlane(Right, min, max)
            && TestPlane(Bottom, min, max)
            && TestPlane(Top, min, max)
            && TestPlane(Near, min, max)
            && TestPlane(Far, min, max);
    }

    private static bool TestPlane(Plane plane, Vector3 min, Vector3 max)
    {
        // Positive vertex: the box corner furthest along the plane normal.
        var positive = new Vector3(
            plane.Normal.X >= 0 ? max.X : min.X,
            plane.Normal.Y >= 0 ? max.Y : min.Y,
            plane.Normal.Z >= 0 ? max.Z : min.Z);

        return Vector3.Dot(plane.Normal, positive) + plane.D >= 0f;
    }

    private static Plane NormalizePlane(Plane p)
    {
        var length = p.Normal.Length();
        if (length <= 0f) return p;
        var inv = 1f / length;
        return new Plane(p.Normal * inv, p.D * inv);
    }
}
