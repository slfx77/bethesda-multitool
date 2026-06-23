using System.Numerics;
using BethesdaMultitool.CLI.Rendering.Nif;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Projection mode for the 3D viewer's "Projection" control and the 3D export.
///     <see cref="None" /> is the live perspective camera (<see cref="CameraState" />); the others are
///     orthographic views built by <see cref="OrthoViewProjBuilder" />.
/// </summary>
internal enum ProjectionMode
{
    /// <summary>Perspective free-fly — the viewer's default (CameraState drives view + projection).</summary>
    None,

    /// <summary>Orthographic, straight down (top-down).</summary>
    Orthographic,

    /// <summary>Orthographic from a 30° elevation (game-art isometric).</summary>
    Isometric,

    /// <summary>Orthographic from a ~25.66° elevation (trimetric).</summary>
    Trimetric,
}

/// <summary>
///     Builds an orthographic view-projection for the viewer's Orthographic / Isometric / Trimetric
///     projection modes and the 3D export, from a focus point + azimuth/elevation + ortho extent.
///     Generalizes <see cref="TopDownViewProjBuilder" /> (top-down is Orthographic at 90° elevation) to
///     any viewing angle.
///     <para>
///         World space is X = east, Y = north, Z = up. The result is an ABSOLUTE view-projection
///         (multiply world positions directly) with the shared <see cref="CameraState.ReverseZ" />
///         applied, so the offscreen/scene depth test matches every scene PSO (GreaterEqual depth test,
///         depth cleared to 0). Azimuth is the camera's compass bearing relative to the focus, measured
///         clockwise from north (+Y): 0 = camera north of focus looking south, 90 = camera east, etc.
///     </para>
/// </summary>
internal static class OrthoViewProjBuilder
{
    /// <summary>Elevation (degrees above the horizon) for each non-perspective mode. Isometric and
    /// trimetric reuse the NPC renderer's presets (see <c>CameraConfig</c>); orthographic is
    /// straight down.</summary>
    public const float OrthographicElevationDeg = 90f;
    public const float IsometricElevationDeg = 30f;
    public const float TrimetricElevationDeg = 25.65891f;

    /// <summary>Distance from the focus to the eye. Large so all geometry sits inside [near, far]
    /// regardless of placement Z; reversed-Z keeps depth precision across the range.</summary>
    private const float EyeDistance = 1_000_000f;

    /// <summary>Elevation in degrees for a projection mode (0 for the perspective <see cref="ProjectionMode.None" />).</summary>
    public static float ElevationDegFor(ProjectionMode mode) => mode switch
    {
        ProjectionMode.Orthographic => OrthographicElevationDeg,
        ProjectionMode.Isometric => IsometricElevationDeg,
        ProjectionMode.Trimetric => TrimetricElevationDeg,
        _ => 0f,
    };

    /// <summary>
    ///     Azimuth (camera compass bearing relative to the focus, degrees CW from north) for a
    ///     90°-snapped quadrant. Orthographic snaps to the cardinals (camera N/E/S/W of the focus);
    ///     isometric/trimetric to the diagonals (NE/SE/SW/NW) so the diamond reads naturally.
    /// </summary>
    public static float AzimuthDegFor(ProjectionMode mode, int quadrant)
    {
        var q = (((quadrant % 4) + 4) % 4);
        var baseDeg = mode == ProjectionMode.Orthographic ? 0f : 45f;
        return baseDeg + (q * 90f);
    }

    /// <summary>
    ///     Builds the orthographic view-projection looking at <paramref name="focus" /> from the given
    ///     azimuth/elevation. <paramref name="orthoHalfHeight" /> is half the vertical world extent the
    ///     frustum covers (the zoom); the horizontal extent follows <paramref name="aspect" />.
    /// </summary>
    public static Matrix4x4 BuildViewProj(
        Vector3 focus, float azimuthDeg, float elevationDeg, float orthoHalfHeight, float aspect)
    {
        var az = azimuthDeg * (MathF.PI / 180f);
        var el = elevationDeg * (MathF.PI / 180f);
        var cosEl = MathF.Cos(el);
        // Direction from the focus toward the eye: compass bearing CW from +Y (north), lifted by the
        // elevation. el = 90° → straight up (top-down); az = 0 → camera north of the focus.
        var toEye = new Vector3(cosEl * MathF.Sin(az), cosEl * MathF.Cos(az), MathF.Sin(el));
        var eye = focus + (toEye * EyeDistance);

        // Up = world +Y (north) at the straight-down extreme (toEye ≈ +Z would make a world-up look-at
        // degenerate) — matching TopDownViewProjBuilder's north-up orientation; world +Z otherwise.
        var up = elevationDeg >= 89.5f ? Vector3.UnitY : Vector3.UnitZ;

        var view = Matrix4x4.CreateLookAt(eye, focus, up);
        var halfH = MathF.Max(orthoHalfHeight, 1f);
        var halfW = halfH * MathF.Max(aspect, 1e-4f);
        var proj = Matrix4x4.CreateOrthographic(halfW * 2f, halfH * 2f, 1f, 2f * EyeDistance);
        return view * proj * CameraState.ReverseZ;
    }

    /// <summary>
    ///     Covering visibility cylinder centered on the focus with a world-space radius.
    ///     <see cref="VisibilityCylinder" /> is an XY-circle cull (camera-orientation-independent), so a
    ///     generous radius admits every cell that could fall inside the ortho frustum.
    /// </summary>
    public static VisibilityCylinder BuildCoverCylinder(Vector3 focus, float radius) =>
        new(new Vector3(focus.X, focus.Y, EyeDistance), radius);
}
