namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     One XnGine plane after the reference triangulation (cut-1c plan decision D5, <see cref="XnGineTriangulation" />):
///     the corners the corner test keeps in the reference order, the triangles that survive the zero-area rule, the
///     counts the <c>bmt.xngine.uv-rule</c> native row and the diagnostics report, and why the plane yields no triangle
///     when it does not.
/// </summary>
/// <param name="PlaneIndex">The plane's ordinal in the plane list.</param>
/// <param name="CornerCount">The number of corners the plane stores.</param>
/// <param name="KeptCorners">
///     The kept corners in the reference order: <c>c0, c1, c2</c> for a 3-corner plane (which takes no corner test), and
///     for an n-gon the corners the 0.001 rad test keeps in the order <c>c1, c2, ..., c(n-1), c0</c>. Empty for a plane of
///     fewer than three corners.
/// </param>
/// <param name="Triangles">The surviving triangles in the reference orientation, in fan order.</param>
/// <param name="ReferenceTriangleCount">The triangles the reference emits before the zero-area rule.</param>
/// <param name="ZeroAreaTriangles">Reference triangles whose integer cross product is exactly zero.</param>
/// <param name="OmittedZeroAreaTriangles">Zero-area triangles omitted because the plane's authored normal is zero.</param>
/// <param name="FoldedTriangles">
///     Kept n-gon triangles whose integer normal points against the polygon's Newell normal (the fan does not cover the
///     polygon exactly there; plan section 0.2, an open question for the GLB triangulation row).
/// </param>
/// <param name="ZeroNormal">True when the plane's authored normal is exactly (0, 0, 0).</param>
/// <param name="Omission">Why the plane yields no triangle, or <see cref="XnGinePlaneOmission.None" />.</param>
internal sealed record XnGineTriangulatedPlane(
    int PlaneIndex,
    int CornerCount,
    IReadOnlyList<int> KeptCorners,
    IReadOnlyList<XnGineCornerTriangle> Triangles,
    int ReferenceTriangleCount,
    int ZeroAreaTriangles,
    int OmittedZeroAreaTriangles,
    int FoldedTriangles,
    bool ZeroNormal,
    XnGinePlaneOmission Omission)
{
    /// <summary>True when the plane yields no triangle and is omitted from the primitive geometry.</summary>
    public bool IsOmitted => Omission != XnGinePlaneOmission.None;

    /// <summary>
    ///     The corners the corner test dropped from an n-gon (0 for a plane of three or fewer corners). They stay in the
    ///     plane's face when the plane reaches the geometry; their vertices are simply not referenced by a triangle.
    /// </summary>
    public int CollinearCornersDropped => CornerCount > 3 ? CornerCount - KeptCorners.Count : 0;
}
