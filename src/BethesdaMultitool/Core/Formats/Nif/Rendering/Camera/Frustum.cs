using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

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
    ///     Returns this frustum with its four side planes rotated outward by
    ///     <paramref name="radians" />, so the result contains every rotation of the original about its
    ///     apex by up to that angle.
    ///     <para>
    ///         Why angular rather than a distance margin: rotating by θ displaces the frustum boundary
    ///         by ~d·sin θ, which GROWS with distance d. A constant margin large enough to cover the far
    ///         field would be enormous up close — and because the broadphase applies its margin by
    ///         inflating each bucket's AABB isotropically, that margin would also blow the effective
    ///         half-angle wide open and defeat the bucket rejection entirely.
    ///     </para>
    ///     <para>
    ///         Proof of the superset property: the side planes of a perspective frustum all pass through
    ///         the apex. For inward normal <c>n</c> and view forward <c>f</c>, <c>t = normalize(f - (f·n)n)</c>
    ///         is the boundary ray direction in the (f, n) plane, so <c>n' = cos θ·n + sin θ·t</c> is
    ///         <c>n</c> rotated to open the half-angle by exactly θ. Any direction inside the original
    ///         frustum, rotated by ψ ≤ θ, sits at angle ≥ -θ from every original plane and therefore
    ///         inside the widened one.
    ///     </para>
    ///     <para>
    ///         <paramref name="widened" /> is false — and the frustum returned unchanged — for an
    ///         ORTHOGRAPHIC projection, whose side planes are parallel and have no apex to rotate about.
    ///         Callers must then fall back to an exact orientation compare. The top-down and headless
    ///         capture paths depend on this.
    ///     </para>
    /// </summary>
    /// <param name="radians">Angular slack to open each side plane by.</param>
    /// <param name="maxReach">Furthest distance the cull can admit; the near/far planes are pushed out
    /// by <c>maxReach·sin θ</c> so they cannot clip content the rotation sweeps into range.</param>
    /// <param name="widened">True when the widening was applied.</param>
    public Frustum WidenAngular(float radians, float maxReach, out bool widened)
    {
        widened = false;
        if (radians <= 0f || !float.IsFinite(radians))
        {
            return this;
        }

        // Parallel opposing side planes ⇒ orthographic ⇒ no apex. Decline rather than produce garbage.
        if (Vector3.Dot(Left.Normal, Right.Normal) <= -0.9999f)
        {
            return this;
        }

        if (!TrySolveApex(Left, Right, Top, out var apex))
        {
            return this;
        }

        // Frustum axis, derived from the SIDE planes rather than Near.Normal. The scene camera
        // post-multiplies a reversed-Z remap onto its projection, which swaps the roles of the
        // extracted near/far planes — Near.Normal there points BACKWARD along the view. Using it as
        // the axis rotated every side plane inward and silently NARROWED the frustum by the slack,
        // culling geometry at the edges of the viewport. Opposing side normals have equal and
        // opposite lateral components, so their sum lies along the axis under any depth convention.
        var axisSum = Left.Normal + Right.Normal + Bottom.Normal + Top.Normal;
        if (axisSum.LengthSquared() < 1e-8f)
        {
            return this;
        }

        var forward = Vector3.Normalize(axisSum);
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);

        Plane Widen(Plane p)
        {
            // Component of the view direction perpendicular to this plane's normal, i.e. the boundary
            // ray. Degenerate only if the normal is parallel to forward, which no side plane is.
            var perpendicular = forward - (Vector3.Dot(forward, p.Normal) * p.Normal);
            var length = perpendicular.Length();
            if (length < 1e-5f)
            {
                return p;
            }

            var rotated = Vector3.Normalize((cos * p.Normal) + (sin * (perpendicular / length)));
            return new Plane(rotated, -Vector3.Dot(rotated, apex));
        }

        var depthSlack = MathF.Max(maxReach, 0f) * sin;
        widened = true;
        return new Frustum(
            Widen(Left),
            Widen(Right),
            Widen(Bottom),
            Widen(Top),
            new Plane(Near.Normal, Near.D + depthSlack),
            new Plane(Far.Normal, Far.D + depthSlack));
    }

    /// <summary>
    ///     Recovers the frustum apex as the common point of three planes, via the standard
    ///     three-plane intersection formula. Returns false when they are near-coplanar (no finite
    ///     apex), which is what an orthographic projection looks like here.
    /// </summary>
    private static bool TrySolveApex(Plane a, Plane b, Plane c, out Vector3 apex)
    {
        apex = default;

        // Plane form is Dot(n, p) + D = 0, so each row reads Dot(n, p) = -D.
        var bc = Vector3.Cross(b.Normal, c.Normal);
        var determinant = Vector3.Dot(a.Normal, bc);
        if (MathF.Abs(determinant) < 1e-6f)
        {
            return false;
        }

        var ca = Vector3.Cross(c.Normal, a.Normal);
        var ab = Vector3.Cross(a.Normal, b.Normal);
        apex = ((-a.D * bc) + (-b.D * ca) + (-c.D * ab)) / determinant;
        return float.IsFinite(apex.X) && float.IsFinite(apex.Y) && float.IsFinite(apex.Z);
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

    /// <summary>Tests whether a sphere intersects (or lies inside) the frustum.</summary>
    public bool IntersectsSphere(Vector3 center, float radius)
    {
        return TestSpherePlane(Left, center, radius)
            && TestSpherePlane(Right, center, radius)
            && TestSpherePlane(Bottom, center, radius)
            && TestSpherePlane(Top, center, radius)
            && TestSpherePlane(Near, center, radius)
            && TestSpherePlane(Far, center, radius);
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

    private static bool TestSpherePlane(Plane plane, Vector3 center, float radius) =>
        Vector3.Dot(plane.Normal, center) + plane.D >= -radius;

    private static Plane NormalizePlane(Plane p)
    {
        var length = p.Normal.Length();
        if (length <= 0f) return p;
        var inv = 1f / length;
        return new Plane(p.Normal * inv, p.D * inv);
    }
}
