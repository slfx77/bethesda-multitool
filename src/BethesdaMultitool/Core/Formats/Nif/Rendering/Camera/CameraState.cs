using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Mutable view + projection state for the v3 3D worldspace camera. Pure data + matrix
///     construction; no input handling, no GPU dependency. Uses Fallout's right-handed Z-up
///     world convention (X = east, Y = north, Z = up), matching the game data directly.
/// </summary>
internal sealed class CameraState
{
    /// <summary>World-space camera position (Fallout coords: X east, Y north, Z up).</summary>
    public Vector3 Position { get; set; }

    /// <summary>Rotation around world Z, radians. Zero = looking along +Y (north).</summary>
    public float Yaw { get; set; }

    /// <summary>Rotation around the camera's right vector, radians. Positive tilts up.</summary>
    public float Pitch { get; set; }

    /// <summary>Vertical field of view in radians. Default 60°.</summary>
    public float FovYRadians { get; set; } = MathF.PI / 3f;

    /// <summary>
    ///     Near clip plane (world units). 16 = clipping at half a cell-vertex spacing — far
    ///     enough to give D32_Float depth precision room to breathe across the wide
    ///     near/far range, close enough that flythrough never visibly clips terrain in
    ///     front of the camera.
    /// </summary>
    public float NearPlane { get; set; } = 16f;

    /// <summary>
    ///     Far clip plane (world units). Decoupled from the streaming radius — the caller sets it
    ///     large enough to cover all loaded geometry from any altitude/tilt so terrain is never
    ///     truncated at the horizon (see WorldView3DControl). Reversed-Z depth (see
    ///     <see cref="GetProjectionMatrix" />) keeps float-depth precision intact across this wide
    ///     near/far range, so a large far plane no longer costs distant z-fighting.
    /// </summary>
    public float FarPlane { get; set; } = 800_000f;

    /// <summary>Forward direction (unit), derived from yaw + pitch.</summary>
    public Vector3 Forward
    {
        get
        {
            var cy = MathF.Cos(Yaw);
            var sy = MathF.Sin(Yaw);
            var cp = MathF.Cos(Pitch);
            var sp = MathF.Sin(Pitch);
            // Yaw 0 → +Y; yaw +π/2 → +X. Pitch tilts the Z component (+ up).
            return new Vector3(sy * cp, cy * cp, sp);
        }
    }

    /// <summary>Right direction (unit), derived from yaw only — keeps roll at zero.</summary>
    public Vector3 Right
    {
        get
        {
            var cy = MathF.Cos(Yaw);
            var sy = MathF.Sin(Yaw);
            // Right is perpendicular to Forward in the XY plane. Yaw 0 (looking +Y) → right = +X.
            return new Vector3(cy, -sy, 0f);
        }
    }

    /// <summary>Up direction (unit), derived so the basis stays orthonormal.</summary>
    public Vector3 Up => Vector3.Cross(Right, Forward);

    public Matrix4x4 GetViewMatrix() =>
        // Use the camera's derived Up rather than world Z. Forward approaches ±UnitZ at vertical
        // pitch, which makes CreateLookAt's `cross(up, forward)` degenerate and the resulting
        // right axis swings around as the camera moves — visible as a "twist" while strafing
        // straight down. The derived Up is `cross(Right, Forward)` and stays orthogonal to
        // Forward at every pitch, so the view basis is stable everywhere.
        Matrix4x4.CreateLookAt(Position, Position + Forward, Up);

    /// <summary>
    ///     Translation-free view matrix — the camera sits at the world origin (only its rotation is
    ///     applied). Pairs with camera-relative rendering: each scene vertex is shifted by
    ///     <c>-Position</c> in its vertex shader before projection, so the large world coordinates
    ///     (worldspace edges reach ±200k units) never enter the <c>viewProj × worldPos</c> product,
    ///     where float32 cancellation produces the visible "wobble" as the camera moves. Mathematically
    ///     <c>GetViewMatrixCameraRelative() × (worldPos − Position)</c> equals
    ///     <c>GetViewMatrix() × worldPos</c>, so the projected result is identical — only the float
    ///     precision improves, and layers rendered either way stay pixel-aligned in clip space.
    /// </summary>
    public Matrix4x4 GetViewMatrixCameraRelative() =>
        Matrix4x4.CreateLookAt(Vector3.Zero, Forward, Up);

    public Matrix4x4 GetProjectionMatrix(float aspectRatio) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovYRadians, aspectRatio, NearPlane, FarPlane) * ReverseZ;

    /// <summary>
    ///     Reversed-Z remap: post-multiplies a standard [0,1] projection so depth maps near→1,
    ///     far→0. Pairs with a depth clear of 0 and a <c>GreaterEqual</c> depth test in every scene
    ///     PSO. With a float (D32) depth buffer this is the standard fix for precision across a wide
    ///     near/far range — it cancels the 1/z nonlinearity, so a large <see cref="FarPlane" /> no
    ///     longer z-fights. Only the Z row is touched (z' = w − z, w' = w), so X/Y clip mapping and
    ///     the extracted frustum volume are unchanged. Shared with the 2D top-down overlay
    ///     (TopDownViewProjBuilder) so the offscreen depth test matches.
    /// </summary>
    public static readonly Matrix4x4 ReverseZ = new(
        1f, 0f, 0f, 0f,
        0f, 1f, 0f, 0f,
        0f, 0f, -1f, 0f,
        0f, 0f, 1f, 1f);
}
