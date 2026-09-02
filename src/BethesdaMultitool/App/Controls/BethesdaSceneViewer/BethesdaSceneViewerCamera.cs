using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Viewer;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

/// <summary>Immutable camera values consumed by one native scene frame.</summary>
internal readonly record struct BethesdaSceneViewerCameraFrame(
    Matrix4x4 View,
    Matrix4x4 Projection,
    Matrix4x4 ViewProjection,
    Vector3 Position,
    Vector3 Target,
    Vector3 Forward,
    Vector3 Right,
    Vector3 Up,
    float NearPlane,
    float FarPlane);

/// <summary>
///     Small Z-up orbit camera for standalone assets and assembled actors. It is deliberately pure
///     model math: pointer ownership and frame invalidation stay in the WinUI control, while the
///     direct renderer receives a stable frame snapshot.
/// </summary>
internal sealed class BethesdaSceneViewerCamera
{
    private const float DefaultAzimuthDegrees = 315f;
    private const float DefaultElevationDegrees = 30f;
    private const float RawSkyElevationDegrees = -30f;
    private const float OrbitDegreesPerPixel = 0.35f;
    private const float ZoomExponentPerWheelNotch = 0.16f;
    private const float FramingMargin = 1.2f;
    private const float MinimumElevationDegrees = -89f;
    private const float MaximumElevationDegrees = 89f;

    private float _humanScale = 1f;
    private float _sceneRadius = 100f;

    internal Vector3 Target { get; private set; }

    internal float Distance { get; private set; } = 300f;

    internal float AzimuthDegrees { get; private set; } = DefaultAzimuthDegrees;

    internal float ElevationDegrees { get; private set; } = DefaultElevationDegrees;

    internal float FieldOfViewRadians { get; set; } = MathF.PI / 3f;

    /// <summary>Frames finite scene bounds and resets the authored three-quarter presentation view.</summary>
    internal void Frame(
        BethesdaViewerBounds? bounds,
        BethesdaGame game,
        bool dedicatedRawSky = false)
    {
        _humanScale = GameProfiles.HumanScaleFactor(game);
        if (!(_humanScale > 0f) || !float.IsFinite(_humanScale))
        {
            _humanScale = 1f;
        }

        var fallbackRadius = 100f * _humanScale;
        if (bounds is { IsFinite: true } finite &&
            finite.Maximum.X >= finite.Minimum.X &&
            finite.Maximum.Y >= finite.Minimum.Y &&
            finite.Maximum.Z >= finite.Minimum.Z)
        {
            Target = finite.Center;
            _sceneRadius = MathF.Max(finite.Size.Length() * 0.5f, fallbackRadius * 0.01f);
        }
        else
        {
            Target = Vector3.Zero;
            _sceneRadius = fallbackRadius;
        }

        AzimuthDegrees = DefaultAzimuthDegrees;
        // The orbit camera's elevation describes the eye position; Forward is its negation. The
        // ordinary +30-degree three-quarter view therefore looks down. A camera-centred raw sky
        // needs the inverse preset so its vertical FOV spans the authored horizon/upper hemisphere
        // instead of the dome's uniform lower closure. Mixed and assembled scenes stay ordinary.
        ElevationDegrees = dedicatedRawSky
            ? RawSkyElevationDegrees
            : DefaultElevationDegrees;
        var halfFov = Math.Clamp(FieldOfViewRadians * 0.5f, 0.05f, 1.5f);
        Distance = MathF.Max(
            (_sceneRadius / MathF.Sin(halfFov)) * FramingMargin,
            MinimumDistance);
    }

    internal void Orbit(Vector2 pixelDelta)
    {
        if (!float.IsFinite(pixelDelta.X) || !float.IsFinite(pixelDelta.Y)) return;

        AzimuthDegrees = NormalizeDegrees(AzimuthDegrees - pixelDelta.X * OrbitDegreesPerPixel);
        ElevationDegrees = Math.Clamp(
            ElevationDegrees + pixelDelta.Y * OrbitDegreesPerPixel,
            MinimumElevationDegrees,
            MaximumElevationDegrees);
    }

    internal void Pan(Vector2 pixelDelta, float viewportHeight)
    {
        if (!(viewportHeight > 0f) ||
            !float.IsFinite(pixelDelta.X) ||
            !float.IsFinite(pixelDelta.Y))
        {
            return;
        }

        var (_, _, right, up) = ResolveBasis();
        var worldPerPixel =
            2f * Distance * MathF.Tan(FieldOfViewRadians * 0.5f) / viewportHeight;
        Target += (-right * pixelDelta.X + up * pixelDelta.Y) * worldPerPixel;
    }

    internal void Zoom(float wheelDelta)
    {
        if (!float.IsFinite(wheelDelta) || MathF.Abs(wheelDelta) < float.Epsilon) return;

        var notches = wheelDelta / 120f;
        Distance = Math.Clamp(
            Distance * MathF.Exp(-notches * ZoomExponentPerWheelNotch),
            MinimumDistance,
            MaximumDistance);
    }

    internal BethesdaSceneViewerCameraFrame GetFrame(float aspectRatio)
    {
        var aspect = float.IsFinite(aspectRatio) && aspectRatio > 0f ? aspectRatio : 1f;
        var (eyeDirection, forward, right, up) = ResolveBasis();
        var position = Target + eyeDirection * Distance;
        var nearPlane = MathF.Max(_sceneRadius * 0.0001f, 0.001f * _humanScale);
        nearPlane = MathF.Min(nearPlane, MathF.Max(Distance * 0.25f, 0.001f * _humanScale));
        var farPlane = MathF.Max(Distance + _sceneRadius * 4f, nearPlane * 1024f);
        var view = Matrix4x4.CreateLookAt(position, Target, Vector3.UnitZ);
        var projection = Matrix4x4.CreatePerspectiveFieldOfView(
                             FieldOfViewRadians,
                             aspect,
                             nearPlane,
                             farPlane) *
                         CameraState.ReverseZ;

        return new BethesdaSceneViewerCameraFrame(
            view,
            projection,
            view * projection,
            position,
            Target,
            forward,
            right,
            up,
            nearPlane,
            farPlane);
    }

    private float MinimumDistance => MathF.Max(_sceneRadius * 0.05f, 0.01f * _humanScale);

    private float MaximumDistance => MathF.Max(_sceneRadius * 100f, MinimumDistance * 2f);

    private (Vector3 EyeDirection, Vector3 Forward, Vector3 Right, Vector3 Up) ResolveBasis()
    {
        var eyeDirection = OrthoViewProjBuilder.EyeDirection(AzimuthDegrees, ElevationDegrees);
        var (right, up) = OrthoViewProjBuilder.CameraBasis(AzimuthDegrees, ElevationDegrees);
        return (eyeDirection, -eyeDirection, right, up);
    }

    private static float NormalizeDegrees(float degrees)
    {
        var normalized = degrees % 360f;
        return normalized < 0f ? normalized + 360f : normalized;
    }
}
