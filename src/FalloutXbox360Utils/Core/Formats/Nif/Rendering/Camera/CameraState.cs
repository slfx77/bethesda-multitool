using System.Numerics;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Camera;

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
    ///     Far clip plane (world units). 800 000 ≈ 195 cells — covers a camera positioned
    ///     anywhere in a 128×128 worldspace (the largest the loader has produced so far,
    ///     ~524k units across) when tilted toward the far corner. Smaller values truncate
    ///     the horizon as the camera tilts up. Phase 4 LOD work will let us shrink the cap
    ///     by switching distant cells to coarser meshes; until then, this trades VRAM for
    ///     uninterrupted terrain.
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

    public Matrix4x4 GetProjectionMatrix(float aspectRatio) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovYRadians, aspectRatio, NearPlane, FarPlane);
}
