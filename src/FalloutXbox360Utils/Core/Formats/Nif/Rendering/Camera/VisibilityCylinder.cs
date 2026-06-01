using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     v3 Phase 2a culling primitive — a 2D cylinder in the XY (ground) plane, used by the
///     terrain / water / wireframe renderers in place of a 3D view frustum.
///     <para>
///         A cell renders iff its XY footprint lies within <see cref="Radius" /> of
///         <see cref="Position" /> in the ground plane. Camera orientation (yaw <b>and</b>
///         pitch) is intentionally ignored, so neither rotation direction unloads terrain
///         — only camera translation does.
///     </para>
///     <para>
///         We tried a yaw wedge (cone in XY around the camera's forward direction) but no
///         fixed half-angle is sound across all camera pitches: at near-vertical downward
///         pitch, cells at any world-XY azimuth from the yaw direction can be on-screen
///         (the view-space condition <c>y·cos(P) + H·sin(P) &gt; 0</c> degenerates to "always
///         true" as P → π/2). Cells geometrically behind the camera in yaw terms are still
///         submitted to the GPU and clipped in hardware — the cost is some extra vertex-
///         shader work in exchange for visual continuity across every rotation.
///     </para>
/// </summary>
internal readonly record struct VisibilityCylinder(Vector3 Position, float Radius)
{
    /// <summary>
    ///     Tests whether the exterior cell at (<paramref name="gridX" />, <paramref name="gridY" />)
    ///     intersects the cylinder. Uses the closest-point AABB test on the cell's XY footprint
    ///     so any cell whose footprint partially clips the cylinder counts as inside.
    /// </summary>
    public bool ContainsCell(int gridX, int gridY)
    {
        var minX = gridX * global::FalloutXbox360Utils.WorldGridConstants.CellSize;
        var minY = gridY * global::FalloutXbox360Utils.WorldGridConstants.CellSize;
        var maxX = minX + global::FalloutXbox360Utils.WorldGridConstants.CellSize;
        var maxY = minY + global::FalloutXbox360Utils.WorldGridConstants.CellSize;

        var closestX = Math.Clamp(Position.X, minX, maxX);
        var closestY = Math.Clamp(Position.Y, minY, maxY);
        var dx = Position.X - closestX;
        var dy = Position.Y - closestY;
        return dx * dx + dy * dy < Radius * Radius;
    }
}
