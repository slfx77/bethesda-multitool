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

    /// <summary>Near clip plane (world units). 4 = clipping starts inside a player-height step.</summary>
    public float NearPlane { get; set; } = 4f;

    /// <summary>Far clip plane (world units). 200 000 ≈ 49 cells; tune in Phase 2 when terrain meshes drop the visible cap.</summary>
    public float FarPlane { get; set; } = 200_000f;

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
        Matrix4x4.CreateLookAt(Position, Position + Forward, Vector3.UnitZ);

    public Matrix4x4 GetProjectionMatrix(float aspectRatio) =>
        Matrix4x4.CreatePerspectiveFieldOfView(FovYRadians, aspectRatio, NearPlane, FarPlane);
}
