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
    /// regardless of placement Z; reversed-Z keeps depth precision across the range. Public so the
    /// viewer can place a shading "camera" along the eye direction (specular view vector) far enough
    /// that the ortho view direction reads as parallel across the whole frame.</summary>
    public const float EyeDistance = 1_000_000f;

    /// <summary>Elevation at/above which the view looks straight down and world +Y (north) is "up" in
    /// the image — matching <see cref="TopDownViewProjBuilder" />. Below it, world +Z is the up axis.</summary>
    private const float TopDownElevationThresholdDeg = 89.5f;

    /// <summary>
    ///     Unit direction from the focus toward the eye for a compass bearing (degrees CW from north,
    ///     +Y) lifted by an elevation (degrees above the horizon). el = 90° → straight up (top-down);
    ///     az = 0 → camera north of the focus. Shared by <see cref="BuildViewProj" />,
    ///     <see cref="EyePosition" />, and <see cref="CameraBasis" /> so all three stay consistent.
    /// </summary>
    private static Vector3 ToEyeDirection(float azimuthDeg, float elevationDeg)
    {
        var az = azimuthDeg * (MathF.PI / 180f);
        var el = elevationDeg * (MathF.PI / 180f);
        var cosEl = MathF.Cos(el);
        return new Vector3(cosEl * MathF.Sin(az), cosEl * MathF.Cos(az), MathF.Sin(el));
    }

    /// <summary>
    ///     Unit direction from the focus toward the eye (the camera's view axis). Sliding the look-at
    ///     focus + eye together along this vector leaves the orthographic image unchanged (it only
    ///     re-anchors depth), which the viewer uses to re-seat the focus onto the terrain without an
    ///     image jump. Same vector <see cref="EyePosition" /> scales by <see cref="EyeDistance" />.
    /// </summary>
    public static Vector3 EyeDirection(float azimuthDeg, float elevationDeg) =>
        ToEyeDirection(azimuthDeg, elevationDeg);

    /// <summary>
    ///     The look-at "up" vector. Below the top-down threshold the camera is tilted and world +Z is up
    ///     (azimuth orbits the camera around the focus). AT the straight-down extreme world +Z projects to
    ///     a point, so the azimuth instead becomes the image ROLL: up = the compass bearing placed at the
    ///     top of the image (az 0 → north up, az 90 → east up, …). This is what makes the ◄ ► rotate and
    ///     the export's N/E/S/W "what's at the top" actually turn the top-down view (a fixed north-up would
    ///     ignore the azimuth there). az 0 reduces to +Y, matching <see cref="TopDownViewProjBuilder" />.
    /// </summary>
    private static Vector3 WorldUp(float azimuthDeg, float elevationDeg)
    {
        if (elevationDeg < TopDownElevationThresholdDeg) return Vector3.UnitZ;
        var az = azimuthDeg * (MathF.PI / 180f);
        return new Vector3(MathF.Sin(az), MathF.Cos(az), 0f);
    }

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

    /// <summary>Tolerance (in 90° quadrant units) for treating an azimuth as aligned to an increment.</summary>
    private const float AzimuthAlignEpsilon = 1e-3f;

    /// <summary>
    ///     Computes the ◄ ► snap target for a continuous azimuth. The aligned positions are the mode's
    ///     quadrant set (<see cref="AzimuthDegFor" /> = base + k·90, base 0 ortho / 45 iso-tri). When the
    ///     azimuth is already on an increment, this steps a full 90° in <paramref name="direction" />
    ///     (−1 = left / +1 = right); when it has been rotated off-axis (a Shift+drag free rotation), it
    ///     snaps to the nearest increment in the arrow's direction instead (right → the next increment
    ///     above, left → the next below) so the first click re-aligns. Result normalized to [0, 360).
    /// </summary>
    public static float SnapAzimuth(float azimuthDeg, ProjectionMode mode, int direction)
    {
        var baseDeg = AzimuthDegFor(mode, 0);
        var rel = (azimuthDeg - baseDeg) / 90f; // quadrants from the mode base
        var nearest = MathF.Round(rel);
        int k;
        if (MathF.Abs(rel - nearest) < AzimuthAlignEpsilon)
        {
            k = (int)nearest + Math.Sign(direction); // aligned → full 90° step
        }
        else
        {
            // Off-axis → snap to the next increment in the arrow's direction.
            k = direction > 0 ? (int)MathF.Floor(rel) + 1 : (int)MathF.Ceiling(rel) - 1;
        }
        var result = (baseDeg + (k * 90f)) % 360f;
        return result < 0f ? result + 360f : result;
    }

    /// <summary>
    ///     Builds the orthographic view-projection looking at <paramref name="focus" /> from the given
    ///     azimuth/elevation. <paramref name="orthoHalfHeight" /> is half the vertical world extent the
    ///     frustum covers (the zoom); the horizontal extent follows <paramref name="aspect" />.
    /// </summary>
    public static Matrix4x4 BuildViewProj(
        Vector3 focus, float azimuthDeg, float elevationDeg, float orthoHalfHeight, float aspect)
    {
        // Direction from the focus toward the eye: compass bearing CW from +Y (north), lifted by the
        // elevation. el = 90° → straight up (top-down); az = 0 → camera north of the focus.
        var eye = focus + (ToEyeDirection(azimuthDeg, elevationDeg) * EyeDistance);
        var view = Matrix4x4.CreateLookAt(eye, focus, WorldUp(azimuthDeg, elevationDeg));
        var halfH = MathF.Max(orthoHalfHeight, 1f);
        var halfW = halfH * MathF.Max(aspect, 1e-4f);
        var proj = Matrix4x4.CreateOrthographic(halfW * 2f, halfH * 2f, 1f, 2f * EyeDistance);
        return view * proj * CameraState.ReverseZ;
    }

    /// <summary>
    ///     One tile of a <paramref name="cols" />×<paramref name="rows" /> tiling of the full ortho frame
    ///     described by (<paramref name="focus" />, azimuth, elevation, <paramref name="orthoHalfHeight" />,
    ///     <paramref name="aspect" />). The tiles share the SAME view (eye + up), and each renders an
    ///     off-center sub-rectangle of the full clip volume — because an orthographic projection has no
    ///     perspective divide, subdividing the clip rectangle is exact, so the tiles stitch seamlessly at
    ///     ANY elevation (top-down or tilted iso/tri). <paramref name="tileCol" /> counts left→right;
    ///     <paramref name="tileRow" /> counts TOP→bottom (matching image rows + the tile manifest), so it
    ///     is flipped onto the bottom-up view-space Y here.
    /// </summary>
    public static Matrix4x4 BuildViewProjTile(
        Vector3 focus, float azimuthDeg, float elevationDeg, float orthoHalfHeight, float aspect,
        int tileCol, int tileRow, int cols, int rows)
    {
        var eye = focus + (ToEyeDirection(azimuthDeg, elevationDeg) * EyeDistance);
        var view = Matrix4x4.CreateLookAt(eye, focus, WorldUp(azimuthDeg, elevationDeg));

        var halfH = MathF.Max(orthoHalfHeight, 1f);
        var halfW = halfH * MathF.Max(aspect, 1e-4f);
        var c = Math.Max(cols, 1);
        var r = Math.Max(rows, 1);
        var ci = Math.Clamp(tileCol, 0, c - 1);
        // Image rows run top→bottom; view-space Y runs bottom→top, so flip the row index.
        var rjFromBottom = r - 1 - Math.Clamp(tileRow, 0, r - 1);

        var left = -halfW + ((2f * halfW) * ci / c);
        var right = -halfW + ((2f * halfW) * (ci + 1) / c);
        var bottom = -halfH + ((2f * halfH) * rjFromBottom / r);
        var top = -halfH + ((2f * halfH) * (rjFromBottom + 1) / r);

        var proj = Matrix4x4.CreateOrthographicOffCenter(left, right, bottom, top, 1f, 2f * EyeDistance);
        return view * proj * CameraState.ReverseZ;
    }

    /// <summary>
    ///     World-space eye position for the given focus + azimuth/elevation — the same point
    ///     <see cref="BuildViewProj" /> looks from. The viewer feeds this to the atmosphere CB as the
    ///     shading camera position so the specular view vector reads as (effectively) parallel across
    ///     the ortho frame, which is the correct ortho behavior.
    /// </summary>
    public static Vector3 EyePosition(Vector3 focus, float azimuthDeg, float elevationDeg) =>
        focus + (ToEyeDirection(azimuthDeg, elevationDeg) * EyeDistance);

    /// <summary>
    ///     World-space right + up axes of the ortho camera (the view matrix's X and Y axes). Used to
    ///     re-face the SpeedTree leaf billboards for the ortho/iso/tri view (the live frame derives the
    ///     basis from the perspective camera, which isn't aimed where the ortho view looks) and to pan
    ///     the focus in screen space. At the top-down extreme this is (east, north) = (+X, +Y), matching
    ///     the 2D overlay's flat leaf basis.
    /// </summary>
    public static (Vector3 Right, Vector3 Up) CameraBasis(float azimuthDeg, float elevationDeg)
    {
        // CreateLookAt's basis: zAxis = normalize(eye − target) = toEye; xAxis = normalize(cross(up,
        // zAxis)); yAxis = cross(zAxis, xAxis). The camera's world right/up are xAxis/yAxis.
        var zAxis = Vector3.Normalize(ToEyeDirection(azimuthDeg, elevationDeg));
        var right = Vector3.Normalize(Vector3.Cross(WorldUp(azimuthDeg, elevationDeg), zAxis));
        var up = Vector3.Cross(zAxis, right);
        return (right, up);
    }

    /// <summary>Cap (in cells) on the relief parallax reach of <see cref="CoverRadius" /> so extreme
    /// relief can't balloon the streamed set — a documented clip risk only past ~16·cellSize·tan(el)
    /// of relief above/below the focus (Skyrim's ~30k-unit range stays under it at iso/tri).</summary>
    private const float MaxReliefReachCells = 16f;

    /// <summary>
    ///     Extra XY reach required for geometry <paramref name="verticalRelief" /> above or below an
    ///     orthographic view's focus plane. Holding screen Y fixed gives
    ///     <c>-Δd·sin(el) + Δz·cos(el) = 0</c>, hence the ground-footprint displacement is
    ///     <c>Δd = Δz·cot(el)</c>. This is not the height's projection onto camera-up
    ///     (<c>Δz·cos(el)</c>), which measures screen-space framing rather than XY cull reach.
    ///     <paramref name="verticalRelief" /> is one-sided: callers starting from a Z range centered
    ///     on the focus pass half its span.
    /// </summary>
    public static float ReliefParallaxReach(float verticalRelief, float elevationDeg)
    {
        if (!(verticalRelief > 0f)) return 0f; // Includes zero, negative values, and NaN.
        if (!float.IsFinite(verticalRelief) || !float.IsFinite(elevationDeg)) return float.MaxValue;

        // The supported projection modes live in [25.66°, 90°]. A horizon-facing projection has no
        // finite ground footprint, so saturate rather than returning a deceptively small radius.
        if (elevationDeg <= 0f) return float.MaxValue;
        if (elevationDeg >= 90f) return 0f;

        var el = elevationDeg * (MathF.PI / 180f);
        var cotEl = MathF.Cos(el) / MathF.Sin(el);
        var reach = (double)verticalRelief * cotEl;
        return !double.IsFinite(reach) || reach >= float.MaxValue
            ? float.MaxValue
            : (float)Math.Max(reach, 0d);
    }

    /// <summary>
    ///     Builds the conservative XY streaming footprint for one off-center export tile. Screen-space
    ///     tile coordinates cannot be copied directly into world XY for a tilted view: the tile center
    ///     first has to follow its orthographic view ray back to the focus-Z plane, and its screen-height
    ///     plus the focus-centered Z relief both widen on that plane. The eight projected corner/height
    ///     combinations yield the exact circumscribed-circle radius; that circle also conservatively
    ///     covers the axis-aligned square represented by <see cref="VisibilityCylinder" />.
    /// </summary>
    public static VisibilityCylinder BuildTileCoverCylinder(
        Vector3 focus, float azimuthDeg, float elevationDeg,
        float centerViewX, float centerViewY, float tileHalfWidth, float tileHalfHeight,
        float verticalRelief, float cellSize)
    {
        // Avoid MathF.Cos(π/2)'s tiny residual: top-down has exactly no XY relief parallax.
        var toEye = elevationDeg >= 90f
            ? Vector3.UnitZ
            : ToEyeDirection(azimuthDeg, elevationDeg);
        var (right, up) = CameraBasis(azimuthDeg, elevationDeg);
        var eyeZ = toEye.Z;
        if (!(eyeZ > 1e-4f) || !float.IsFinite(eyeZ))
        {
            return BuildCoverCylinder(focus, float.MaxValue);
        }

        // Follow the ray through the tile's screen-space center until it reaches Z=focus.Z.
        var centerOnViewPlane = focus + (right * centerViewX) + (up * centerViewY);
        var center = centerOnViewPlane - (toEye * ((centerOnViewPlane.Z - focus.Z) / eyeZ));

        // Project each screen basis vector and a unit Z displacement onto that same focus-Z plane.
        var groundRight = right - (toEye * (right.Z / eyeZ));
        var groundUp = up - (toEye * (up.Z / eyeZ));
        var groundRelief = toEye / eyeZ;
        var xReach = new Vector2(groundRight.X, groundRight.Y) * MathF.Max(tileHalfWidth, 0f);
        var yReach = new Vector2(groundUp.X, groundUp.Y) * MathF.Max(tileHalfHeight, 0f);
        var zReach = new Vector2(groundRelief.X, groundRelief.Y) * MathF.Max(verticalRelief, 0f);

        var radius = 0f;
        for (var sx = -1; sx <= 1; sx += 2)
        {
            for (var sy = -1; sy <= 1; sy += 2)
            {
                for (var sz = -1; sz <= 1; sz += 2)
                {
                    radius = MathF.Max(radius, (sx * xReach + sy * yReach + sz * zReach).Length());
                }
            }
        }

        radius += 2f * MathF.Max(cellSize, 0f);
        return BuildCoverCylinder(center, float.IsFinite(radius) ? radius : float.MaxValue);
    }

    /// <summary>
    ///     Cull-cylinder radius covering everything an ortho frame can see, from three terms:
    ///     (1) the visible rectangle's GROUND-footprint diagonal — the vertical screen half-extent is a
    ///     view-space distance that projects onto the ground as halfHeight / sin(elevation), up to ~2×
    ///     at iso 30°; (2) terrain-relief parallax — a point Δz above the focus plane stays on screen
    ///     until its horizontal distance toward the camera reaches Δz·cot(elevation) PAST the footprint
    ///     edge (bottom screen edge: −halfH = −d·sin(el) + Δz·cos(el) ⇒ d = halfH/sin(el) + Δz·cot(el);
    ///     cot 30° ≈ 1.73), so tilted views need extra XY reach for peaks/valleys leaning in — the same
    ///     compensation the 3D export applies to half its focus-centered Z span — capped at
    ///     <see cref="MaxReliefReachCells" /> cells; (3) two cells of drift slack. Top-down has no
    ///     parallax (cot clamps to 0), so the radius reduces exactly to the flat footprint formula.
    /// </summary>
    public static float CoverRadius(
        float orthoHalfHeight, float aspect, float elevationDeg, float cellSize,
        float reliefAboveFocus, float reliefBelowFocus)
    {
        var halfW = orthoHalfHeight * MathF.Max(aspect, 1e-4f);
        var el = elevationDeg * (MathF.PI / 180f);
        var sinEl = MathF.Max(MathF.Sin(el), 0.1f);
        var groundHalfH = orthoHalfHeight / sinEl;
        var footprint = MathF.Sqrt((halfW * halfW) + (groundHalfH * groundHalfH)) + (2f * cellSize);

        var relief = MathF.Max(MathF.Max(reliefAboveFocus, reliefBelowFocus), 0f);
        var reliefReach = MathF.Min(
            ReliefParallaxReach(relief, elevationDeg),
            MaxReliefReachCells * cellSize);
        return footprint + reliefReach;
    }

    /// <summary>
    ///     Covering visibility cylinder centered on the focus with a world-space radius.
    ///     <see cref="VisibilityCylinder" /> is an axis-aligned XY square (camera-orientation-independent),
    ///     so a radius derived from a circumscribed circle conservatively admits every covered cell.
    /// </summary>
    public static VisibilityCylinder BuildCoverCylinder(Vector3 focus, float radius) =>
        new(new Vector3(focus.X, focus.Y, EyeDistance), radius);
}
