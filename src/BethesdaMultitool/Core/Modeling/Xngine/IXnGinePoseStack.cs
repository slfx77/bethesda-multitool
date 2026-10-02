using Slfx77.Multitool.Core.Models;

namespace BethesdaMultitool.Core.Modeling.Xngine;

/// <summary>
///     The later poses of a posed XnGine mesh, a Redguard <c>.3DC</c> frame stack (cut-1c plan section 4), which
///     <see cref="XnGineModelGeometry.Build" /> carries into every primitive of the keyframe as morph targets. Giving
///     one also makes the primitives Flat (plan decision D4): a <c>.3DC</c> stores no normal a <c>.3D</c> reader could
///     use, and Flat shading derives each pose's directions from that pose's own triangles.
/// </summary>
internal interface IXnGinePoseStack
{
    /// <summary>The number of morph targets every primitive carries: one per pose after the keyframe.</summary>
    int TargetCount { get; }

    /// <summary>
    ///     The morph targets of one primitive, one per later pose in pose order, in that primitive's vertex domain: vertex
    ///     <c>i</c> carries source point <c>vertexPoints[i]</c>, and its base position is that point's keyframe position
    ///     <c>(X, -Y, Z)</c>.
    /// </summary>
    IReadOnlyList<SceneMorphTarget> TargetsFor(IReadOnlyList<int> vertexPoints, CancellationToken cancellationToken);
}
