using System.Globalization;
using Slfx77.Multitool.Core.Documents;
using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The document diagnostics the XnGine <c>.3D</c> reader raises (cut-1c plan section 3.4, "Diagnostics"), beside the
///     game-identity codes it passes through unchanged (<see cref="XnGineGameIdentity" />). Every message is raw text.
/// </summary>
internal static class XnGineModelDiagnostics
{
    /// <summary>Header +16 is non-zero on a mesh read as static (MENU.ROB's four +16 = 9 segments on retail).</summary>
    public const string StaticFrameCount = "bmt.xngine.static-header-plus16";

    /// <summary>The packed-UV unfold was applied to corners 0 to 2 (Daggerfall, object id below 905; plan D2, Assumed).</summary>
    public const string UnfoldApplied = "bmt.xngine.uv-unfold-applied";

    /// <summary>A Daggerfall record whose object id no container supplied, so the unfold cannot be decided and is not applied.</summary>
    public const string UnfoldUndetermined = "bmt.xngine.uv-unfold-undetermined";

    /// <summary>Texture sizes are not resolved; portable UVs divide by the legacy 64-texel fallback (Assumed).</summary>
    public const string TextureSizeAssumed = "bmt.xngine.texture-size-assumed";

    /// <summary>Planes yield no triangle and are carried only in <c>bmt.xngine.omitted-planes</c> (plan D5).</summary>
    public const string OmittedPlanes = "bmt.xngine.omitted-planes";

    /// <summary>Zero-area triangles on zero-normal planes were omitted (plan D5).</summary>
    public const string OmittedTriangles = "bmt.xngine.omitted-triangles";

    /// <summary>
    ///     Drawn planes whose corners name one source point twice: each face repeats a point, which a Blender mesh cannot
    ///     hold, so Shared's Blender admission omits it (Dropped <c>faces-repeat-vertex</c>) while the GLB keeps its
    ///     triangles (147 retail planes in 60 meshes, 135 with area; how they should reach Faces is an open owner question).
    /// </summary>
    public const string RepeatedPointFaces = "bmt.xngine.repeated-point-faces";

    /// <summary>Kept n-gon triangles fold against the polygon's Newell normal (the fan does not cover the polygon exactly).</summary>
    public const string FoldedTriangles = "bmt.xngine.folded-triangles";

    /// <summary>A coordinate's magnitude reaches 2^24, so its float32 position is rounded (no retail coordinate does).</summary>
    public const string PositionRounded = "bmt.xngine.position-rounded";

    /// <summary>The declared byte areas overlap or do not fit the record (no retail mesh does; plan M-T).</summary>
    public const string AreaTiling = "bmt.xngine.area-tiling";

    /// <summary>A raw-text diagnostic.</summary>
    public static SceneDiagnostic Create(string code, string message)
    {
        return new SceneDiagnostic(code, DocumentText.Raw(message));
    }

    /// <summary>
    ///     Appends the geometry diagnostics both XnGine readers raise, in this order: the texture-size fallback (always),
    ///     then, when they occur, the omitted planes, the omitted zero-area triangles, the faces that repeat a source point,
    ///     the folded fan triangles and a position rounded to float32. The <c>.3D</c> reader raises them between its
    ///     header and tiling diagnostics; the <c>.3DC</c> reader beside its own.
    /// </summary>
    public static void AddGeometry(List<SceneDiagnostic> diagnostics, IReadOnlyList<XnGineTriangulatedPlane> planes,
        XnGineModelGeometryResult geometry)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);
        ArgumentNullException.ThrowIfNull(planes);
        ArgumentNullException.ThrowIfNull(geometry);
        diagnostics.Add(Create(TextureSizeAssumed, string.Create(CultureInfo.InvariantCulture,
            $"textures are not resolved by this reader yet; portable UVs divide the reference texel coordinates by the " +
            $"legacy {XnGineModelGeometry.FallbackTextureSize}-texel fallback (Assumed) and every primitive carries a " +
            $"placeholder material named TEXTURE.aaa#r")));

        var omitted = planes.Where(static plane => plane.IsOmitted).ToList();
        if (omitted.Count > 0)
        {
            var keys = geometry.EmptiedKeys.Count == 0
                ? "no texture key is left without a plane"
                : "texture keys left with no plane: " + string.Join(", ", geometry.EmptiedKeys.Select(static key =>
                    string.Create(CultureInfo.InvariantCulture, $"0x{key.Key:X} ({key.MaterialName})")));
            diagnostics.Add(Create(OmittedPlanes, string.Create(CultureInfo.InvariantCulture,
                $"{omitted.Count} plane(s) yield no triangle under the reference triangulation " +
                $"({omitted.Count(static p => p.Omission == XnGinePlaneOmission.ZeroNormalNoArea)} with a zero normal and " +
                $"no area, {omitted.Count(static p => p.Omission == XnGinePlaneOmission.FewerThanThreeKeptCorners)} " +
                $"keeping fewer than 3 corners, {omitted.Count(static p => p.Omission == XnGinePlaneOmission.FewerThanThreeCorners)} " +
                $"with fewer than 3 corners) and are carried in bmt.xngine.omitted-planes; {keys}")));
        }

        var omittedTriangles = planes.Where(static plane => !plane.IsOmitted).Sum(static plane => plane.OmittedZeroAreaTriangles);
        if (omittedTriangles > 0)
        {
            diagnostics.Add(Create(OmittedTriangles, string.Create(CultureInfo.InvariantCulture,
                $"{omittedTriangles} zero-area triangle(s) on zero-normal planes that still have area were omitted")));
        }

        if (geometry.RepeatedPointPlanes.Count > 0)
        {
            diagnostics.Add(Create(RepeatedPointFaces, string.Create(CultureInfo.InvariantCulture,
                $"{geometry.RepeatedPointPlanes.Count} drawn plane(s) name one source point at two corners (planes " +
                $"{DescribeOrdinals(geometry.RepeatedPointPlanes)}; {geometry.RepeatedPointPlanesWithArea} with a " +
                $"triangle that covers area); each is written as one face whose point indices repeat that point, which " +
                $"a Blender mesh cannot hold, so the Blender package omits the face and its triangles while the GLB " +
                $"draws them (counted in bmt.xngine.uv-rule; how such planes should reach Faces is an open question)")));
        }

        var folded = planes.Sum(static plane => plane.FoldedTriangles);
        if (folded > 0)
        {
            diagnostics.Add(Create(FoldedTriangles, string.Create(CultureInfo.InvariantCulture,
                $"{folded} reference fan triangle(s) point against their polygon's Newell normal, so the fan does not " +
                $"cover those polygons exactly (the reference triangulation is kept)")));
        }

        if (geometry.PositionRounded)
        {
            diagnostics.Add(Create(PositionRounded,
                "a coordinate's magnitude reaches 2^24, so its float32 position is rounded"));
        }
    }

    /// <summary>A plane-ordinal list for a diagnostic: the first 16 ordinals, then how many more.</summary>
    private static string DescribeOrdinals(IReadOnlyList<int> ordinals)
    {
        const int shown = 16;
        var text = string.Join(", ",
            ordinals.Take(shown).Select(static ordinal => ordinal.ToString(CultureInfo.InvariantCulture)));
        return ordinals.Count > shown
            ? string.Create(CultureInfo.InvariantCulture, $"{text} and {ordinals.Count - shown} more")
            : text;
    }
}
