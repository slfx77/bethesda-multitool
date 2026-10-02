using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The geometry <see cref="XnGineModelGeometry.Build" /> produced for one XnGine mesh: the primitives in their
///     document order with the texture key each one draws, the texture keys left with no primitive, and the drawn
///     planes whose faces repeat a source point.
/// </summary>
/// <param name="Primitives">One primitive per surviving texture key, in first-use order.</param>
/// <param name="Keys">The texture key of each primitive (key i belongs to primitive i and to material i).</param>
/// <param name="EmptiedKeys">
///     The texture keys every plane of which yields no triangle (plan D5), in first-use order: they get no primitive
///     (9 retail keys over 9 meshes; no retail mesh loses every key).
/// </param>
/// <param name="PositionRounded">True when a coordinate's magnitude reaches 2^24, so a float32 position is rounded.</param>
/// <param name="RepeatedPointPlanes">
///     The drawn planes, as source ordinals in plane order, whose corners name one source point more than once
///     (<see cref="XnGineModelGeometry.RepeatsSourcePoint" />). Each is one face whose point indices repeat that point,
///     which a Blender mesh cannot hold: Shared's Blender admission omits the face (a Dropped <c>faces-repeat-vertex</c>
///     row) while the GLB draws its triangles. 147 drawn retail planes in 60 meshes (slice-5 review receipt
///     <c>measure_review_fixes.json</c>); no-triangle planes never reach a face and are not listed.
/// </param>
/// <param name="RepeatedPointPlanesWithArea">
///     How many of <paramref name="RepeatedPointPlanes" /> keep a triangle that covers area (135 of the 147 retail
///     planes), so their loss in Blender removes visible surface.
/// </param>
internal sealed record XnGineModelGeometryResult(
    IReadOnlyList<ScenePrimitive> Primitives,
    IReadOnlyList<XnGineTextureKey> Keys,
    IReadOnlyList<XnGineTextureKey> EmptiedKeys,
    bool PositionRounded,
    IReadOnlyList<int> RepeatedPointPlanes,
    int RepeatedPointPlanesWithArea)
{
    /// <summary>
    ///     The source points no primitive vertex names, ascending (slice-6 review finding 2): no plane corner names them,
    ///     or only corners of planes that yield no triangle do, which reach no primitive. Their coordinates reach no
    ///     vertex or morph target, so the readers keep them in a native row (<c>bmt.xngine.unreferenced-points</c>,
    ///     <c>bmt.redguard.3dc.unreferenced-points</c>). Retail: 49 points in 9 static meshes (1 named by no corner, 48
    ///     only by no-triangle planes) and 7 points in 4 <c>.3DC</c> files (named by no corner), review receipt
    ///     <c>measure_review6_fixes.json</c>.
    /// </summary>
    /// <param name="pointCount">The mesh's point count, the size of the point domain every primitive shares.</param>
    /// <exception cref="InvalidOperationException">A primitive carries no point indices (every XnGine primitive does).</exception>
    public IReadOnlyList<int> UnreferencedPoints(int pointCount)
    {
        var named = new bool[pointCount];
        foreach (var primitive in Primitives)
        {
            var indices = primitive.PointIndices ??
                          throw new InvalidOperationException("Every XnGine primitive carries its source point indices.");
            foreach (var point in indices.Values)
            {
                named[point] = true;
            }
        }

        var unreferenced = new List<int>();
        for (var point = 0; point < named.Length; point++)
        {
            if (!named[point])
            {
                unreferenced.Add(point);
            }
        }

        return unreferenced;
    }
}
