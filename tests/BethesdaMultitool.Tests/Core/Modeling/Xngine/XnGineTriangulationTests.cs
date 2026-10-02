using System.Numerics;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Modeling.Xngine;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Modeling.Xngine;

/// <summary>
///     The reference triangulation (cut-1c plan section 3.2, decision D5) on hand-built planes whose kept corners and
///     triangles are worked out by hand in each test: the corner order <c>c1..c(n-1), c0</c>, the fan from the first kept
///     corner, the n-gon keeping fewer than three corners, the zero-area rule on zero-normal planes, the folded count, and
///     the pose union the <c>.3DC</c> reader will use. Each positive result has a control that the tested rule rejects.
/// </summary>
public sealed class XnGineTriangulationTests
{
    private static readonly XnGineMeshPoint Up = new(0, -256, 0);

    /// <summary>
    ///     A pentagon whose c3 lies on the edge c2-c4: the corner test drops c3 (its angle at c2 is 0), keeps
    ///     c1, c2, c4, c0 in that order, and fans from c1: (1, 2, 4), (1, 4, 0).
    /// </summary>
    [Fact]
    public void NGon_KeepsCornersInReferenceOrder_AndFansFromTheFirstKeptCorner()
    {
        XnGineMeshPoint[] corners = [new(0, 16, 0), new(256, 16, 0), new(256, 16, 256), new(128, 16, 256), new(0, 16, 256)];

        var plane = XnGineTriangulation.Triangulate(0, corners, Up);

        Assert.Equal(new[] { 1, 2, 4, 0 }, plane.KeptCorners);
        Assert.Equal(new[] { new XnGineCornerTriangle(1, 2, 4), new XnGineCornerTriangle(1, 4, 0) }, plane.Triangles);
        Assert.Equal(1, plane.CollinearCornersDropped);
        Assert.Equal(XnGinePlaneOmission.None, plane.Omission);
        Assert.Equal(0, plane.FoldedTriangles);

        // Control: the withdrawn draft's fan from c0 over the same kept set gives another triangle stream.
        var c0Fan = new[] { new XnGineCornerTriangle(0, 1, 2), new XnGineCornerTriangle(0, 2, 4) };
        Assert.NotEqual(c0Fan, plane.Triangles);
    }

    /// <summary>A 3-corner plane takes no corner test and is its own triangle, even with a collinear corner.</summary>
    [Fact]
    public void Triangle_IsItsOwnTriangle_WithoutTheCornerTest()
    {
        XnGineMeshPoint[] corners = [new(0, 0, 0), new(256, 0, 0), new(0, 0, 256)];

        var plane = XnGineTriangulation.Triangulate(3, corners, Up);

        Assert.Equal(new[] { 0, 1, 2 }, plane.KeptCorners);
        Assert.Equal(new[] { new XnGineCornerTriangle(0, 1, 2) }, plane.Triangles);
        Assert.Equal(3, plane.PlaneIndex);
    }

    /// <summary>
    ///     Four collinear corners with a non-zero normal (HBBLD01's shape): only c3 survives the corner test (its angle at
    ///     c2 is pi), so the plane yields no triangle. The same four corners made a square keep all four (the control).
    /// </summary>
    [Fact]
    public void NGonKeepingFewerThanThreeCorners_YieldsNoTriangle()
    {
        XnGineMeshPoint[] line = [new(0, 0, 0), new(128, 0, 0), new(256, 0, 0), new(384, 0, 0)];
        XnGineMeshPoint[] square = [new(0, 0, 0), new(256, 0, 0), new(256, 0, 256), new(0, 0, 256)];

        var plane = XnGineTriangulation.Triangulate(0, line, Up);

        Assert.Equal(new[] { 3 }, plane.KeptCorners);
        Assert.Empty(plane.Triangles);
        Assert.Equal(XnGinePlaneOmission.FewerThanThreeKeptCorners, plane.Omission);
        Assert.True(plane.IsOmitted);
        Assert.Equal(XnGinePlaneOmission.None, XnGineTriangulation.Triangulate(0, square, Up).Omission);
    }

    /// <summary>
    ///     A zero-area triangle is omitted when the plane's authored normal is zero (plan D5) and the plane then yields no
    ///     triangle; with a non-zero normal the same zero-area triangle is kept (the control), as in cut 1a.
    /// </summary>
    [Fact]
    public void ZeroAreaTriangle_IsOmittedOnlyOnAZeroNormalPlane()
    {
        XnGineMeshPoint[] collinear = [new(0, 0, 0), new(128, 0, 0), new(256, 0, 0)];

        var zeroNormal = XnGineTriangulation.Triangulate(0, collinear, new XnGineMeshPoint(0, 0, 0));
        var withNormal = XnGineTriangulation.Triangulate(0, collinear, Up);

        Assert.True(zeroNormal.ZeroNormal);
        Assert.Equal(1, zeroNormal.ZeroAreaTriangles);
        Assert.Equal(1, zeroNormal.OmittedZeroAreaTriangles);
        Assert.Empty(zeroNormal.Triangles);
        Assert.Equal(XnGinePlaneOmission.ZeroNormalNoArea, zeroNormal.Omission);

        Assert.Equal(1, withNormal.ZeroAreaTriangles);
        Assert.Equal(0, withNormal.OmittedZeroAreaTriangles);
        Assert.Single(withNormal.Triangles);
        Assert.Equal(XnGinePlaneOmission.None, withNormal.Omission);
    }

    [Fact]
    public void FewerThanThreeCorners_IsOmitted()
    {
        var plane = XnGineTriangulation.Triangulate(0, [new XnGineMeshPoint(0, 0, 0), new XnGineMeshPoint(1, 0, 0)], Up);

        Assert.Equal(XnGinePlaneOmission.FewerThanThreeCorners, plane.Omission);
        Assert.Empty(plane.KeptCorners);
    }

    /// <summary>
    ///     A concave quad (an arrowhead, reflex at c2) whose reference fan from c1 folds. Worked by hand with the reader's
    ///     formulas: Newell y = -49152; the kept triangles are (1, 2, 3), cross y = +16384, and (1, 3, 0), cross y =
    ///     -65536. So the triangle that folds (points against the Newell normal) is (1, 2, 3), the notch at c2, and only
    ///     it. The count is reported and both triangles are kept.
    /// </summary>
    [Fact]
    public void FoldedFanTriangle_IsCounted_AndKept()
    {
        // Arrowhead in the XZ plane at y = 0, as (x, z): c0 (0, 0), c1 (256, 0), c2 (128, 64) reflex, c3 (0, 256).
        XnGineMeshPoint[] arrow = [new(0, 0, 0), new(256, 0, 0), new(128, 0, 64), new(0, 0, 256)];
        var newell = XnGineTriangulation.Newell(arrow);
        var normal = new XnGineMeshPoint(0, newell.Y > 0 ? 256 : -256, 0);

        var plane = XnGineTriangulation.Triangulate(0, arrow, normal);

        Assert.Equal((0L, -49152L, 0L), newell);
        Assert.Equal(new[] { 1, 2, 3, 0 }, plane.KeptCorners);
        Assert.Equal(new[] { new XnGineCornerTriangle(1, 2, 3), new XnGineCornerTriangle(1, 3, 0) }, plane.Triangles);
        Assert.Equal(1, plane.FoldedTriangles);

        // Which triangle folds: the sign of each kept triangle's cross product against the Newell normal.
        Assert.Equal((0L, 16384L, 0L), XnGineTriangulation.Cross(arrow[1], arrow[2], arrow[3]));
        Assert.Equal((0L, -65536L, 0L), XnGineTriangulation.Cross(arrow[1], arrow[3], arrow[0]));
        var signs = plane.Triangles.Select(t =>
        {
            var cross = XnGineTriangulation.Cross(arrow[t.A], arrow[t.B], arrow[t.C]);
            return Math.Sign(cross.X * newell.X + cross.Y * newell.Y + cross.Z * newell.Z);
        }).ToArray();
        Assert.Equal(new[] { -1, 1 }, signs);
    }

    /// <summary>
    ///     The pose union (the <c>.3DC</c> rule): a corner collinear in one pose but not another is kept once the second
    ///     pose is marked. Control: the first pose alone drops it.
    /// </summary>
    [Fact]
    public void MarkKeptCorners_AccumulatesAcrossPoses()
    {
        Vector3[] flat = [new(0, 0, 0), new(128, 0, 0), new(256, 0, 0), new(256, 0, 256), new(0, 0, 256)];
        Vector3[] bent = [new(0, 0, 0), new(128, 0, 64), new(256, 0, 0), new(256, 0, 256), new(0, 0, 256)];
        var kept = new bool[5];

        XnGineTriangulation.MarkKeptCorners(flat, kept);
        Assert.False(kept[1]);
        Assert.Equal(new[] { 2, 3, 4, 0 }, XnGineTriangulation.ReferenceOrder(kept));

        XnGineTriangulation.MarkKeptCorners(bent, kept);
        Assert.True(kept[1]);
        Assert.Equal(new[] { 1, 2, 3, 4, 0 }, XnGineTriangulation.ReferenceOrder(kept));
    }

    /// <summary>
    ///     <see cref="XnGineTriangulation.TriangulatePoses" /> (plan section 4) over the flat and bent pentagons above: the
    ///     union keeps c1, so all five corners are fanned from c1 as (1, 2, 3), (1, 3, 4), (1, 4, 0). Control: the keyframe
    ///     alone keeps c2, c3, c4, c0 and fans (2, 3, 4), (2, 4, 0), exactly as the single-pose rule does.
    /// </summary>
    [Fact]
    public void TriangulatePoses_FansTheCornersAnyPoseKeeps()
    {
        XnGineMeshPoint[] flat = [new(0, 0, 0), new(128, 0, 0), new(256, 0, 0), new(256, 0, 256), new(0, 0, 256)];
        XnGineMeshPoint[] bent = [new(0, 0, 0), new(128, 0, 64), new(256, 0, 0), new(256, 0, 256), new(0, 0, 256)];
        int[] points = [0, 1, 2, 3, 4];

        var union = XnGineTriangulation.TriangulatePoses(7, points, new IReadOnlyList<XnGineMeshPoint>[] { flat, bent });

        Assert.Equal(7, union.PlaneIndex);
        Assert.Equal(new[] { 1, 2, 3, 4, 0 }, union.KeptCorners);
        Assert.Equal(new[]
        {
            new XnGineCornerTriangle(1, 2, 3), new XnGineCornerTriangle(1, 3, 4), new XnGineCornerTriangle(1, 4, 0)
        }, union.Triangles);
        Assert.False(union.ZeroNormal);
        Assert.Equal(XnGinePlaneOmission.None, union.Omission);

        var keyframeOnly = XnGineTriangulation.TriangulatePoses(7, points, new IReadOnlyList<XnGineMeshPoint>[] { flat });
        var single = XnGineTriangulation.Triangulate(7, flat, Up);
        Assert.Equal(new[] { 2, 3, 4, 0 }, keyframeOnly.KeptCorners);
        Assert.Equal(single.KeptCorners, keyframeOnly.KeptCorners);
        Assert.Equal(single.Triangles, keyframeOnly.Triangles);
        Assert.NotEqual(keyframeOnly.Triangles, union.Triangles);
    }

    /// <summary>
    ///     A triangle collinear in the keyframe (its Newell vector is zero, so a keyframe-only rule sees a zero normal and
    ///     no area) but not in pose 1 is KEPT: nothing a pose shows is omitted. Control: the single-pose rule on the
    ///     keyframe with the keyframe's zero normal omits it. Collinear in every pose, the stack's plane is omitted too.
    /// </summary>
    [Fact]
    public void TriangulatePoses_OmitsOnlyWhatNoPoseShows()
    {
        XnGineMeshPoint[] line = [new(0, 0, 0), new(128, 0, 0), new(256, 0, 0)];
        XnGineMeshPoint[] lifted = [new(0, 0, 0), new(128, 0, 64), new(256, 0, 0)];
        XnGineMeshPoint[] slid = [new(0, 0, 0), new(64, 0, 0), new(256, 0, 0)];
        int[] points = [0, 1, 2];

        var alive = XnGineTriangulation.TriangulatePoses(0, points, new IReadOnlyList<XnGineMeshPoint>[] { line, lifted });
        Assert.False(alive.ZeroNormal);
        Assert.Equal(0, alive.ZeroAreaTriangles);
        Assert.Equal(new[] { new XnGineCornerTriangle(0, 1, 2) }, alive.Triangles);
        Assert.Equal(XnGinePlaneOmission.None, alive.Omission);

        var keyframeRule = XnGineTriangulation.Triangulate(0, line, new XnGineMeshPoint(0, 0, 0));
        Assert.Equal(XnGinePlaneOmission.ZeroNormalNoArea, keyframeRule.Omission);

        var dead = XnGineTriangulation.TriangulatePoses(0, points, new IReadOnlyList<XnGineMeshPoint>[] { line, slid });
        Assert.True(dead.ZeroNormal);
        Assert.Equal(1, dead.ZeroAreaTriangles);
        Assert.Equal(1, dead.OmittedZeroAreaTriangles);
        Assert.Equal(XnGinePlaneOmission.ZeroNormalNoArea, dead.Omission);
    }

    /// <summary>A stack needs at least its keyframe, and every pose must hold every corner's point.</summary>
    [Fact]
    public void TriangulatePoses_RefusesAnEmptyStackOrAMissingPoint()
    {
        XnGineMeshPoint[] pose = [new(0, 0, 0), new(256, 0, 0), new(0, 0, 256)];

        Assert.Throws<ArgumentException>(() =>
            XnGineTriangulation.TriangulatePoses(0, [0, 1, 2], Array.Empty<IReadOnlyList<XnGineMeshPoint>>()));
        Assert.Throws<ArgumentException>(() =>
            XnGineTriangulation.TriangulatePoses(0, [0, 1, 3], new IReadOnlyList<XnGineMeshPoint>[] { pose }));
    }

    /// <summary>
    ///     The corner test agrees with the legacy decomposer's on random polygons, verdict for verdict: both run the
    ///     reference's float32 chain, so they may not disagree anywhere (a float64 chain disagrees on retail planes whose
    ///     angles sit near the threshold). The random polygons include near-collinear corners one unit off a line.
    /// </summary>
    [Fact]
    public void CornerTest_AgreesWithTheLegacyDecomposer_OnRandomPolygons()
    {
        var random = new Random(20260928);
        for (var trial = 0; trial < 2000; trial++)
        {
            var count = random.Next(4, 12);
            var corners = new XnGineMeshPoint[count];
            for (var q = 0; q < count; q++)
            {
                corners[q] = q > 1 && random.Next(3) == 0
                    ? new XnGineMeshPoint(2 * corners[q - 1].X - corners[q - 2].X + random.Next(-1, 2),
                        2 * corners[q - 1].Y - corners[q - 2].Y, 2 * corners[q - 1].Z - corners[q - 2].Z)
                    : new XnGineMeshPoint(random.Next(-4096, 4096), random.Next(-4096, 4096), random.Next(-4096, 4096));
            }

            var pure = corners.Select(p => new XnGineMeshDecomposer.PurePoint(p, new XnGineMeshPoint(0, 0, 0), 0, 0))
                .ToArray();
            var legacy = XnGineMeshDecomposer.CornerIndices(pure);
            var plane = XnGineTriangulation.Triangulate(trial, corners, Up);

            Assert.Equal(legacy, plane.KeptCorners);
        }
    }
}
