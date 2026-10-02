namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     Why an XnGine plane yields no triangle under the reference triangulation (cut-1c plan decision D5). Such a plane
///     is omitted from the primitive geometry (no vertices, no face, no point indices), carried in full in the
///     <c>bmt.xngine.omitted-planes</c> native row, and classified NativeOnly in the coverage. Retail: 137 planes in
///     3D.BSA, 249 in 3D.BS6 and 1 in the ROB meshes, at most 66 in one mesh (3D.BS6 ESPEAR); none elsewhere.
/// </summary>
internal enum XnGinePlaneOmission
{
    /// <summary>The plane yields at least one triangle and reaches the geometry.</summary>
    None,

    /// <summary>The plane stores fewer than three corners (no retail plane does; a face needs three).</summary>
    FewerThanThreeCorners,

    /// <summary>
    ///     An n-gon whose reference corner test keeps fewer than three corners (24 in 3D.BSA, 26 in 3D.BS6, 1 in the ROB
    ///     meshes); the legacy decomposer drops these too.
    /// </summary>
    FewerThanThreeKeptCorners,

    /// <summary>
    ///     A plane whose authored normal is zero and whose every reference triangle covers no area (113 three-corner
    ///     planes in 3D.BSA, 223 in 3D.BS6), so it has no drawable surface; the legacy exporter keeps these as
    ///     degenerate triangles with a +Y normal.
    /// </summary>
    ZeroNormalNoArea
}
