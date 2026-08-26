using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Builds an orthographic TRIMETRIC view-projection over a world-XY rectangle, for captures where
///     a straight-down view is hard to read.
///     <para>
///         A pure top-down render (<see cref="TopDownViewProjBuilder" />) collapses every vertical
///         surface to nothing, so walls, doorways, storeys, and object height all disappear and one
///         building looks like the next. Tilting the camera restores that information.
///     </para>
///     <para>
///         Trimetric, not isometric: isometric is the special case yaw = 45° and pitch = 35.264°,
///         where all three axes foreshorten equally — which makes the two ground axes visually
///         interchangeable and leaves square architecture ambiguous about which way it faces. Here
///         <see cref="YawDegrees" /> and <see cref="PitchDegrees" /> are deliberately off those
///         values, so X, Y and Z each foreshorten by a different amount and orientation is readable.
///     </para>
/// </summary>
internal static class TrimetricViewProjBuilder
{
    /// <summary>
    ///     Rotation about world Z, in degrees. Deliberately not 45° — at 45° the two ground axes
    ///     foreshorten identically (dimetric/isometric) and a square footprint gives no cue to which
    ///     edge faces north.
    /// </summary>
    public const float YawDegrees = 30f;

    /// <summary>
    ///     Camera elevation above the horizon, in degrees — the conventional trimetric altitude, and
    ///     deliberately not 35.264° (the isometric angle).
    ///     <para>
    ///         Raising this flattens the view back toward a plan: at 55° the result was
    ///         indistinguishable from top-down for most subjects, because a vertical face only
    ///         occupies cos(elevation) of the image and at 55° that is barely a third. 30° gives
    ///         walls and storeys real screen area while keeping the ground layout readable.
    ///     </para>
    /// </summary>
    public const float PitchDegrees = 30f;

    /// <summary>
    ///     Distance from the framed centre back along the view axis to the camera. Only has to be
    ///     large enough to keep the whole scene in front of the near plane; the projection is
    ///     orthographic, so it has no effect on scale or foreshortening.
    /// </summary>
    public const float EyeDistance = 1_000_000f;

    /// <summary>
    ///     Half-height of the world Z slab assumed to contain all geometry when framing. Exteriors
    ///     have no cheap exact Z extent (terrain streams in), and an ortho box merely has to CONTAIN
    ///     the scene — being generous costs depth precision, which reversed-Z float depth has to
    ///     spare, whereas being tight silently clips mountains and towers out of the picture.
    /// </summary>
    public const float AssumedWorldZHalfSpan = 32_768f;

    /// <summary>
    ///     Fraction of the content box's own size added as padding on every axis when framing.
    ///     <para>
    ///         Content bounds are computed from placed-object ORIGINS, but an object is a mesh that
    ///         extends around its origin — a casino shell or a rock face reaches well past the point
    ///         it is placed at. A proportional pad scales with the subject, unlike the fixed
    ///         world-unit allowance this replaced, which added several thousand units of dead space
    ///         to a room a few hundred units across and shrank it to a corner of the image.
    ///     </para>
    /// </summary>
    public const float ContentPadFraction = 0.18f;

    /// <summary>Floor for the proportional pad, in world units, so a near-degenerate box still frames.</summary>
    public const float MinContentPad = 256f;

    /// <summary>
    ///     Height of a standing human, in world units. A Bethesda unit is ~1.42 cm, so an adult is
    ///     ~128 units — the reference the capture scale is pegged to.
    /// </summary>
    public const float ReferenceFigureWorldHeight = 128f;

    /// <summary>Pixels that figure should occupy at scale 1.0.</summary>
    public const float ReferenceFigurePixelHeight = 64f;

    /// <summary>
    ///     World units per pixel at scale 1.0, measured in the image plane.
    ///     <para>
    ///         Derived from the PROJECTED height of the reference figure, not its raw height: a
    ///         vertical world axis foreshortens by |up.z| under this camera, so pegging the raw
    ///         height would leave the figure visibly shorter than the stated 64 px on screen.
    ///     </para>
    /// </summary>
    public static float WorldUnitsPerPixelAtUnitScale
    {
        get
        {
            // Yaw-invariant: |up.z| depends only on pitch, so any yaw gives the same scale and the
            // four-angle capture keeps one consistent world-units-per-pixel across all views.
            var (_, _, up) = BuildBasis(YawDegrees);
            return ReferenceFigureWorldHeight * MathF.Abs(up.Z) / ReferenceFigurePixelHeight;
        }
    }

    /// <summary>
    ///     The camera basis and matrix for one trimetric framing.
    /// </summary>
    /// <param name="ViewProj">View × projection × reversed-Z, ready to hand to the renderers.</param>
    /// <param name="Right">World-space camera right — the billboard X axis for SpeedTree leaf cards.</param>
    /// <param name="Up">World-space camera up — the billboard Y axis.</param>
    /// <param name="Forward">World-space direction the camera looks along.</param>
    /// <param name="EyePosition">World-space camera position, for view-dependent shading.</param>
    internal readonly record struct TrimetricView(
        Matrix4x4 ViewProj,
        Vector3 Right,
        Vector3 Up,
        Vector3 Forward,
        Vector3 EyePosition);

    /// <summary>
    ///     Builds the trimetric view-projection framing the given world rectangle (north-Y).
    ///     <para>
    ///         Unlike the top-down builder — which can hand the world rectangle straight to an
    ///         off-centre ortho because the camera axes ARE the world axes — a rotated camera has to
    ///         frame in VIEW space: the eight corners of the world box are projected onto the camera
    ///         basis and the ortho bounds taken from their extent. That is what keeps the whole
    ///         subject in frame at any yaw/pitch instead of cropping to a rotated rectangle.
    ///     </para>
    /// </summary>
    /// <param name="clipWorldZMax">
    ///     When set, geometry above this world Z is removed by pulling the near plane in to the cut.
    ///     Interiors use it to take the roof off. Note the cut plane is perpendicular to the VIEW
    ///     axis, not to world Z, so on a tilted camera this reads as an architectural cutaway — it
    ///     also shaves the near-side upper wall — rather than the clean horizontal slice the
    ///     straight-down view produces. That is the intended trade: without it the roof hides
    ///     everything.
    /// </param>
    public static TrimetricView Build(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        float worldMinZ, float worldMaxZ, float? clipWorldZMax = null,
        float yawDegrees = YawDegrees)
    {
        var (forward, right, up) = BuildBasis(yawDegrees);
        var box = PadBox(worldMinX, worldMaxX, worldMinY, worldMaxY, worldMinZ, worldMaxZ);

        // Aim at the CONTENT box's centre, including its Z centre. Aiming at the ground plane
        // instead (z = 0) decentres every subject whose content does not straddle it, which is most
        // of them — an interior sitting at z = 4000 renders in the top half of its own picture.
        var centre = box.Centre;
        var eye = centre - forward * EyeDistance;

        var (minR, maxR, minU, maxU) = FrameExtents(box, eye, right, up);

        // Depth range: the generous slab, which only has to CONTAIN the scene. Reversed-Z float
        // depth has the precision to spare, and clipping geometry out of the picture is far worse.
        // Deliberately NOT the tight content box — geometry reaches past placement origins, and
        // being clipped out of the frustum is invisible in a way that empty margin is not.
        float minD = float.MaxValue, maxD = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? box.MinX : box.MaxX,
                (i & 2) == 0 ? box.MinY : box.MaxY,
                (i & 4) == 0 ? -AssumedWorldZHalfSpan : AssumedWorldZHalfSpan);
            var d = Vector3.Dot(corner - eye, forward);
            minD = MathF.Min(minD, d);
            maxD = MathF.Max(maxD, d);
        }

        var zNear = MathF.Max(1f, minD);
        var zFar = MathF.Max(zNear + 1f, maxD);

        if (clipWorldZMax is float zmax)
        {
            // Distance along the view axis to the cut plane through (centre.xy, zmax). Clamped into
            // the frustum so an unconverged ceiling estimate can never invert near/far.
            var cutDistance = Vector3.Dot(new Vector3(centre.X, centre.Y, zmax) - eye, forward);
            zNear = Math.Clamp(cutDistance, zNear, zFar - 1f);
        }

        // The view matrix is built DIRECTLY from the exact (right, up, −forward) basis the ortho
        // bounds were measured in — never via CreateLookAt(eye, eye + forward, up). That innocuous
        // form is a float32 catastrophic cancellation: at |eye| ≈ 4.3e5 the grid spacing is 1/32,
        // so `eye + forward` rounds forward's cos30°·sin30° = 0.4330127 component to exactly 7/16,
        // a fixed 0.0045 rad direction error that the 1e6 stand-off turns into a CONSTANT ~4,040
        // world-unit displacement of the frame in the image plane — same for every subject and
        // yaw. Small subjects were displaced entirely out of their own frame (the corpus'
        // hundreds of "drew instances, rasterized nothing" angles), and every other subject
        // rendered off-centre by exactly that offset, which the batch auto-fit then spent its
        // passes correcting. cross(right, up) = −forward by construction (right ⊥ forward), so
        // this is the same right-handed basis CreateLookAt would derive, without the rounding.
        var zaxis = -forward;
        var view = new Matrix4x4(
            right.X, up.X, zaxis.X, 0f,
            right.Y, up.Y, zaxis.Y, 0f,
            right.Z, up.Z, zaxis.Z, 0f,
            -Vector3.Dot(right, eye), -Vector3.Dot(up, eye), -Vector3.Dot(zaxis, eye), 1f);
        var proj = Matrix4x4.CreateOrthographicOffCenter(minR, maxR, minU, maxU, zNear, zFar);
        return new TrimetricView(view * proj * CameraState.ReverseZ, right, up, forward, eye);
    }

    /// <summary>
    ///     The framed rectangle's size in world units along the camera's right/up axes.
    ///     <para>
    ///         Callers sizing an output image must use THIS aspect ratio, not the world rectangle's.
    ///         A tilted camera turns a world-axis-aligned rectangle into a rotated, foreshortened
    ///         parallelogram whose bounding box has a different shape — sizing pixels from the world
    ///         rectangle leaves large empty margins on two sides.
    ///     </para>
    /// </summary>
    public static (float Width, float Height) MeasureFrame(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        float worldMinZ, float worldMaxZ, float yawDegrees = YawDegrees)
    {
        var (forward, right, up) = BuildBasis(yawDegrees);
        var box = PadBox(worldMinX, worldMaxX, worldMinY, worldMaxY, worldMinZ, worldMaxZ);
        var eye = box.Centre - forward * EyeDistance;
        var (minR, maxR, minU, maxU) = FrameExtents(box, eye, right, up);
        return (maxR - minR, maxU - minU);
    }

    /// <summary>
    ///     The camera basis for the configured yaw/pitch: (forward, right, up). Exposed so a
    ///     harness can convert an offset measured in the rendered IMAGE back into a world-space
    ///     shift — the image axes are these vectors.
    /// </summary>
    public static (Vector3 Forward, Vector3 Right, Vector3 Up) Basis(float yawDegrees = YawDegrees)
        => BuildBasis(yawDegrees);

    /// <summary>The camera basis for the given yaw and the configured pitch: (forward, right, up).</summary>
    private static (Vector3 Forward, Vector3 Right, Vector3 Up) BuildBasis(float yawDegrees)
    {
        var yaw = yawDegrees * (MathF.PI / 180f);
        var pitch = PitchDegrees * (MathF.PI / 180f);
        var cosPitch = MathF.Cos(pitch);
        var forward = Vector3.Normalize(new Vector3(
            -MathF.Sin(yaw) * cosPitch,
            -MathF.Cos(yaw) * cosPitch,
            -MathF.Sin(pitch)));

        // World up is +Z. It is never parallel to `forward` because pitch is strictly between 0°
        // and 90°, so these cross products are well conditioned.
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitZ));
        return (forward, right, Vector3.Normalize(Vector3.Cross(right, forward)));
    }

    /// <summary>The content box grown by <see cref="ContentPadFraction" /> on every axis.</summary>
    private static ContentBox PadBox(
        float minX, float maxX, float minY, float maxY, float minZ, float maxZ)
    {
        if (maxZ < minZ) (minZ, maxZ) = (maxZ, minZ);
        var pad = MathF.Max(
            MathF.Max(maxX - minX, MathF.Max(maxY - minY, maxZ - minZ)) * ContentPadFraction,
            MinContentPad);
        return new ContentBox(minX - pad, maxX + pad, minY - pad, maxY + pad, minZ - pad, maxZ + pad);
    }

    /// <summary>
    ///     Extent of the framed region along the camera's right/up axes, from all eight corners of
    ///     the content box.
    ///     <para>
    ///         The box must be the CONTENT's own bounds. Framing off a fixed world-Z slab instead —
    ///         the obvious implementation — wrecks the picture twice over: a slab tens of thousands
    ///         of units tall dwarfs the subject (measured: coverage 16.9% → 8.3%, subject pushed
    ///         into a corner), and a fixed height allowance adds the same dead space to a room a few
    ///         hundred units across as to a worldspace, so interiors framed small and off-centre and
    ///         their edges fell outside the image.
    ///     </para>
    /// </summary>
    private static (float MinR, float MaxR, float MinU, float MaxU) FrameExtents(
        ContentBox box, Vector3 eye, Vector3 right, Vector3 up)
    {
        float minR = float.MaxValue, maxR = float.MinValue;
        float minU = float.MaxValue, maxU = float.MinValue;
        for (var i = 0; i < 8; i++)
        {
            var corner = new Vector3(
                (i & 1) == 0 ? box.MinX : box.MaxX,
                (i & 2) == 0 ? box.MinY : box.MaxY,
                (i & 4) == 0 ? box.MinZ : box.MaxZ);
            var rel = corner - eye;
            minR = MathF.Min(minR, Vector3.Dot(rel, right));
            maxR = MathF.Max(maxR, Vector3.Dot(rel, right));
            minU = MathF.Min(minU, Vector3.Dot(rel, up));
            maxU = MathF.Max(maxU, Vector3.Dot(rel, up));
        }

        return (minR, maxR, minU, maxU);
    }

    /// <summary>An axis-aligned world box, with its centre.</summary>
    private readonly record struct ContentBox(
        float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ)
    {
        public Vector3 Centre => new(
            (MinX + MaxX) * 0.5f, (MinY + MaxY) * 0.5f, (MinZ + MaxZ) * 0.5f);
    }

    /// <summary>
    ///     A visibility cylinder covering everything the tilted view can see. A straight-down view
    ///     only needs the rectangle's own footprint, but tilting the camera sweeps the frustum
    ///     ACROSS the ground — geometry up to <see cref="AssumedWorldZHalfSpan" /> tall, standing
    ///     outside the rectangle, still projects into the picture. The radius is padded by that
    ///     horizontal reach so those cells are not culled before they can be drawn.
    /// </summary>
    public static VisibilityCylinder BuildCoverCylinder(
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY, float slack)
    {
        var cx = (worldMinX + worldMaxX) * 0.5f;
        var cy = (worldMinY + worldMaxY) * 0.5f;
        var halfW = (worldMaxX - worldMinX) * 0.5f;
        var halfH = (worldMaxY - worldMinY) * 0.5f;
        var diagonal = MathF.Sqrt(halfW * halfW + halfH * halfH);

        // Horizontal distance a point of height h shifts in the image: h / tan(pitch).
        var reach = AssumedWorldZHalfSpan / MathF.Tan(PitchDegrees * (MathF.PI / 180f));
        return new VisibilityCylinder(
            new Vector3(cx, cy, EyeDistance), diagonal + reach + slack);
    }
}
