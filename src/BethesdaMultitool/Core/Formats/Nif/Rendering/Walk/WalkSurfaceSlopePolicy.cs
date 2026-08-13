using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;

/// <summary>
///     Slope partition shared by the two halves of walk collision: the horizontal wall sweep and the
///     placed-object ground probe.
///     <para>
///         The two thresholds deliberately OVERLAP. A face between 45° and 60° is both a wall (above
///         step height) and a surface the capsule may rest on. A gap would be far worse than the
///         overlap: a face that is neither ground nor wall is one the camera can neither stand on nor
///         be stopped by, i.e. geometry it falls through.
///     </para>
///     <para>
///         Before this partition existed the ground probe accepted ANY face inside the step window,
///         including a vertical cave wall. The capsule ring samples sit exactly on the wall the sweep
///         just stopped the camera against, so each frame the feet were lifted one step height up the
///         wall face — a ratchet that climbed the camera out through walls and ceilings in caves.
///     </para>
/// </summary>
internal static class WalkSurfaceSlopePolicy
{
    /// <summary>
    ///     |n.z| at or above which a face may be stood on (60° from horizontal). Permissive on purpose:
    ///     rocky cave floors are locally steep, and rejecting them would drop the camera into the void.
    /// </summary>
    public const float MinGroundNormalZ = 0.5f;

    /// <summary>
    ///     |n.z| below which a face participates in the horizontal wall sweep (45° from horizontal).
    ///     Shallower faces are floors/ramps/stairs and stay the ground probe's responsibility.
    /// </summary>
    public const float MaxWallNormalZ = 0.70710677f;

    /// <summary>True when an unnormalized world-space face normal is shallow enough to stand on.</summary>
    public static bool IsGround(Vector3 worldNormal)
    {
        var length = worldNormal.Length();
        return length > 1e-12f && MathF.Abs(worldNormal.Z) / length >= MinGroundNormalZ;
    }
}
