using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The reference triangulation of an XnGine plane (cut-1c plan section 3.2 and decision D5), re-implemented from the
///     plan's statement of the rule rather than called through <c>XnGineMeshDecomposer</c>, which bakes units and
///     normalizes normals: a 3-corner plane is its own triangle <c>(c0, c1, c2)</c>; an n-gon keeps the corners the
///     0.001 rad corner test keeps, in the order <c>c1, c2, ..., c(n-1), c0</c>, and is fanned from the FIRST KEPT corner
///     as <c>(K0, K(i), K(i+1))</c>; an n-gon keeping fewer than three corners yields no triangle; and a triangle that
///     covers exactly no area is omitted when the plane's authored normal is zero. The fan is therefore index for index
///     the legacy exporter's triangle stream once the document reverses each triangle.
/// </summary>
/// <remarks>
///     <para>
///         PRECISION IS PART OF THE RULE. The reference runs the corner test in <see cref="Vector3" /> float32 (the
///         int-to-float conversion, the edge subtraction, the two lengths, their product, the edge dot, the division and
///         the clamp) and only the final arccosine in double. This type computes the same chain with the same
///         operations, in the SOURCE coordinates (before the Y negation). A float64 chain flips 32 verdicts on 28 retail
///         planes whose angles sit within about 6e-5 rad of the threshold (slice-2 receipt
///         <c>float32_corner_divergence.json</c>), so a "more accurate" test would disagree with the legacy output.
///     </para>
///     <para>
///         The zero-area and folded tests are exact integer arithmetic on the stored coordinates (every retail
///         coordinate is below 2^24, so the cross products fit a 64-bit integer with room to spare). The corner test is
///         applied per pose through <see cref="MarkKeptCorners" /> so that the <c>.3DC</c> reader keeps a corner that
///         ANY pose keeps (plan section 4) with the same code: <see cref="TriangulatePoses" /> is that rule.
///     </para>
/// </remarks>
internal static class XnGineTriangulation
{
    /// <summary>The reference corner test's threshold: a corner whose edge angle does not exceed it is collinear.</summary>
    public const double CornerAngleThreshold = 0.001;

    /// <summary>The stable identity of this triangulation rule, recorded in the native state and the evidence.</summary>
    public const string RuleId = "bmt.xngine.triangulation-reference/1";

    /// <summary>
    ///     The stable identity of the pose-independent form of the rule a <c>.3DC</c> frame stack takes
    ///     (<see cref="TriangulatePoses" />): the same order and fan over the corners ANY pose keeps.
    /// </summary>
    public const string PoseUnionRuleId = "bmt.xngine.triangulation-reference-pose-union/1";

    /// <summary>
    ///     Runs the reference corner test over one pose of an n-gon and sets <c>kept[c]</c> for every corner it keeps
    ///     (it never clears a flag, so calling it once per pose yields the pose-independent union). Slot <c>i</c> tests the
    ///     angle at corner <c>i</c> between the edges to its two successors (wrapping), and keeps corner <c>i + 1</c>
    ///     (corner 0 at the last slot) when that angle exceeds <see cref="CornerAngleThreshold" />.
    /// </summary>
    /// <exception cref="ArgumentException">The two spans differ in length.</exception>
    public static void MarkKeptCorners(ReadOnlySpan<Vector3> corners, Span<bool> kept)
    {
        if (corners.Length != kept.Length)
        {
            throw new ArgumentException("The kept-corner flags must match the corner count.", nameof(kept));
        }

        var count = corners.Length;
        for (var i = 0; i < count; i++)
        {
            var v0 = corners[i];
            Vector3 v1, v2;
            int corner;
            if (i < count - 2)
            {
                v1 = corners[i + 1];
                v2 = corners[i + 2];
                corner = i + 1;
            }
            else if (i < count - 1)
            {
                v1 = corners[i + 1];
                v2 = corners[0];
                corner = i + 1;
            }
            else
            {
                v1 = corners[0];
                v2 = corners[count > 1 ? 1 : 0];
                corner = 0;
            }

            var l0 = v1 - v0;
            var l1 = v2 - v0;
            var lengths = l0.Length() * l1.Length();
            if (lengths > 0 && Math.Acos(Math.Clamp(Vector3.Dot(l0, l1) / lengths, -1f, 1f)) > CornerAngleThreshold)
            {
                kept[corner] = true;
            }
        }
    }

    /// <summary>The kept corners in the reference order: every flagged corner from c1 to c(n-1), then c0 when flagged.</summary>
    public static int[] ReferenceOrder(ReadOnlySpan<bool> kept)
    {
        var order = new List<int>(kept.Length);
        for (var q = 1; q < kept.Length; q++)
        {
            if (kept[q])
            {
                order.Add(q);
            }
        }

        if (kept.Length > 0 && kept[0])
        {
            order.Add(0);
        }

        return [.. order];
    }

    /// <summary>A plane's corner positions in source coordinates, as the float32 vectors the corner test reads.</summary>
    public static Vector3[] CornerVectors(IReadOnlyList<XnGineMeshPoint> corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        var vectors = new Vector3[corners.Count];
        for (var q = 0; q < vectors.Length; q++)
        {
            vectors[q] = new Vector3(corners[q].X, corners[q].Y, corners[q].Z);
        }

        return vectors;
    }

    /// <summary>Triangulates one static plane: the corner test on its one pose, then <see cref="Triangulate(int, IReadOnlyList{XnGineMeshPoint}, XnGineMeshPoint, ReadOnlySpan{bool})" />.</summary>
    public static XnGineTriangulatedPlane Triangulate(int planeIndex, IReadOnlyList<XnGineMeshPoint> corners,
        XnGineMeshPoint normal)
    {
        ArgumentNullException.ThrowIfNull(corners);
        var kept = new bool[corners.Count];
        if (corners.Count > 3)
        {
            MarkKeptCorners(CornerVectors(corners), kept);
        }

        return Triangulate(planeIndex, corners, normal, kept);
    }

    /// <summary>
    ///     Triangulates one plane from its corner positions (source coordinates, the pose the zero-area and folded tests
    ///     read), its authored normal and the kept-corner flags of an n-gon (ignored for a 3-corner plane, which takes no
    ///     corner test). See the type summary for the rule.
    /// </summary>
    /// <exception cref="ArgumentException">The flags do not match the corner count.</exception>
    public static XnGineTriangulatedPlane Triangulate(int planeIndex, IReadOnlyList<XnGineMeshPoint> corners,
        XnGineMeshPoint normal, ReadOnlySpan<bool> kept)
    {
        ArgumentNullException.ThrowIfNull(corners);
        if (kept.Length != corners.Count)
        {
            throw new ArgumentException("The kept-corner flags must match the corner count.", nameof(kept));
        }

        return Build(planeIndex, corners, normal is { X: 0, Y: 0, Z: 0 }, kept, static _ => true);
    }

    /// <summary>
    ///     Triangulates one plane of a pose stack (a Redguard <c>.3DC</c>, plan section 4). <paramref name="poses" /> holds
    ///     every pose's points, the keyframe (the pose the geometry is built on) first; <paramref name="cornerPoints" />
    ///     are the plane's corners as indices into each pose. The rule is <see cref="PoseUnionRuleId" />: an n-gon keeps a
    ///     corner when the reference corner test keeps it in AT LEAST ONE pose (<see cref="MarkKeptCorners" /> once per
    ///     pose), in the same reference order and fan; the plane's normal counts as zero only when its exact Newell
    ///     vector is zero in EVERY pose (a <c>.3DC</c> stores no normal list a <c>.3D</c> reader could use); and a
    ///     triangle counts as covering no area only when its exact cross product is zero in every pose, so nothing any
    ///     pose shows is omitted. Folded triangles are counted in the keyframe, against its Newell normal.
    /// </summary>
    /// <remarks>
    ///     Measured over the 147 retail <c>.3DC</c> files (slice-6 receipt <c>measure_slice6.json</c>, through the
    ///     gate-1c oracle <c>rg3dc_probe.pose_independent_kept</c>): 45 of 3,898 n-gons in 32 files keep a different
    ///     corner list in some pose than in the keyframe, and on 41 of them the union differs from the keyframe's list;
    ///     no plane has a zero Newell vector in any keyframe, so none is zero-normal; 50 reference triangles cover no
    ///     area in the keyframe and every one covers area in some pose, so all are kept; no union keeps fewer than three
    ///     corners and no keyframe triangle folds. No retail <c>.3DC</c> plane is omitted.
    /// </remarks>
    /// <exception cref="ArgumentException">No pose is given, or a pose does not hold a corner's point.</exception>
    public static XnGineTriangulatedPlane TriangulatePoses(int planeIndex, IReadOnlyList<int> cornerPoints,
        IReadOnlyList<IReadOnlyList<XnGineMeshPoint>> poses)
    {
        ArgumentNullException.ThrowIfNull(cornerPoints);
        ArgumentNullException.ThrowIfNull(poses);
        if (poses.Count == 0)
        {
            throw new ArgumentException("A pose stack holds at least the keyframe.", nameof(poses));
        }

        foreach (var pose in poses)
        {
            foreach (var point in cornerPoints)
            {
                if (point < 0 || point >= pose.Count)
                {
                    throw new ArgumentException("Every pose must hold every corner's point.", nameof(poses));
                }
            }
        }

        var count = cornerPoints.Count;
        var keyframe = new XnGineMeshPoint[count];
        for (var q = 0; q < count; q++)
        {
            keyframe[q] = poses[0][cornerPoints[q]];
        }

        var kept = new bool[count];
        if (count > 3)
        {
            var buffer = new Vector3[count];
            foreach (var pose in poses)
            {
                for (var q = 0; q < count; q++)
                {
                    var point = pose[cornerPoints[q]];
                    buffer[q] = new Vector3(point.X, point.Y, point.Z);
                }

                MarkKeptCorners(buffer, kept);
            }
        }

        var zeroNormal = poses.All(pose => Newell(pose, cornerPoints) == (0L, 0L, 0L));
        return Build(planeIndex, keyframe, zeroNormal, kept, triangle => poses.All(pose =>
            Cross(pose[cornerPoints[triangle.A]], pose[cornerPoints[triangle.B]], pose[cornerPoints[triangle.C]]) ==
            (0L, 0L, 0L)));
    }

    /// <summary>
    ///     The rule both forms share: <paramref name="corners" /> is the pose the zero-area and fold tests read first (the
    ///     only pose of a static plane; the keyframe of a stack), <paramref name="zeroNormal" /> whether the plane counts
    ///     as zero-normal, and <paramref name="noAreaInEveryPose" /> whether a triangle with a zero cross product in
    ///     <paramref name="corners" /> also covers no area in every other pose (always true for a static plane).
    /// </summary>
    private static XnGineTriangulatedPlane Build(int planeIndex, IReadOnlyList<XnGineMeshPoint> corners,
        bool zeroNormal, ReadOnlySpan<bool> kept, Func<XnGineCornerTriangle, bool> noAreaInEveryPose)
    {
        var count = corners.Count;
        if (count < 3)
        {
            return new XnGineTriangulatedPlane(planeIndex, count, [], [], 0, 0, 0, 0, zeroNormal,
                XnGinePlaneOmission.FewerThanThreeCorners);
        }

        int[] order;
        var reference = new List<XnGineCornerTriangle>();
        if (count == 3)
        {
            order = [0, 1, 2];
            reference.Add(new XnGineCornerTriangle(0, 1, 2));
        }
        else
        {
            order = ReferenceOrder(kept);
            if (order.Length < 3)
            {
                return new XnGineTriangulatedPlane(planeIndex, count, order, [], 0, 0, 0, 0, zeroNormal,
                    XnGinePlaneOmission.FewerThanThreeKeptCorners);
            }

            for (var i = 1; i + 1 < order.Length; i++)
            {
                reference.Add(new XnGineCornerTriangle(order[0], order[i], order[i + 1]));
            }
        }

        var newell = count > 3 ? Newell(corners) : default;
        var triangles = new List<XnGineCornerTriangle>(reference.Count);
        int zeroArea = 0, omitted = 0, folded = 0;
        foreach (var triangle in reference)
        {
            var cross = Cross(corners[triangle.A], corners[triangle.B], corners[triangle.C]);
            if (cross == (0L, 0L, 0L) && noAreaInEveryPose(triangle))
            {
                zeroArea++;
                if (zeroNormal)
                {
                    omitted++;
                    continue;
                }
            }
            else if (count > 3 && Dot(cross, newell) < 0)
            {
                folded++;
            }

            triangles.Add(triangle);
        }

        return new XnGineTriangulatedPlane(planeIndex, count, order, triangles, reference.Count, zeroArea, omitted,
            folded, zeroNormal,
            triangles.Count == 0 ? XnGinePlaneOmission.ZeroNormalNoArea : XnGinePlaneOmission.None);
    }

    /// <summary>The exact integer cross product <c>(b - a) x (c - a)</c> of three stored points.</summary>
    public static (long X, long Y, long Z) Cross(XnGineMeshPoint a, XnGineMeshPoint b, XnGineMeshPoint c)
    {
        long bx = (long)b.X - a.X, by = (long)b.Y - a.Y, bz = (long)b.Z - a.Z;
        long cx = (long)c.X - a.X, cy = (long)c.Y - a.Y, cz = (long)c.Z - a.Z;
        return (by * cz - bz * cy, bz * cx - bx * cz, bx * cy - by * cx);
    }

    /// <summary>The exact integer Newell normal of a polygon in stored corner order.</summary>
    public static (long X, long Y, long Z) Newell(IReadOnlyList<XnGineMeshPoint> corners)
    {
        ArgumentNullException.ThrowIfNull(corners);
        long x = 0, y = 0, z = 0;
        for (var q = 0; q < corners.Count; q++)
        {
            var a = corners[q];
            var b = corners[(q + 1) % corners.Count];
            x += ((long)a.Y - b.Y) * ((long)a.Z + b.Z);
            y += ((long)a.Z - b.Z) * ((long)a.X + b.X);
            z += ((long)a.X - b.X) * ((long)a.Y + b.Y);
        }

        return (x, y, z);
    }

    /// <summary>The exact integer Newell normal of the polygon whose corners are <paramref name="cornerPoints" /> of one pose.</summary>
    private static (long X, long Y, long Z) Newell(IReadOnlyList<XnGineMeshPoint> pose, IReadOnlyList<int> cornerPoints)
    {
        long x = 0, y = 0, z = 0;
        for (var q = 0; q < cornerPoints.Count; q++)
        {
            var a = pose[cornerPoints[q]];
            var b = pose[cornerPoints[(q + 1) % cornerPoints.Count]];
            x += ((long)a.Y - b.Y) * ((long)a.Z + b.Z);
            y += ((long)a.Z - b.Z) * ((long)a.X + b.X);
            z += ((long)a.X - b.X) * ((long)a.Y + b.Y);
        }

        return (x, y, z);
    }

    /// <summary>The sign-exact dot product of two integer vectors (in 128-bit arithmetic, so it cannot overflow).</summary>
    private static Int128 Dot((long X, long Y, long Z) a, (long X, long Y, long Z) b)
    {
        return (Int128)a.X * b.X + (Int128)a.Y * b.Y + (Int128)a.Z * b.Z;
    }
}
