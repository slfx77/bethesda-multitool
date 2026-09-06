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
    private const float ZoomExponentPerWheelNotch = 0.16f;
    private const float FramingMargin = 1.2f;
    private const float MinimumElevationDegrees = -89f;
    private const float MaximumElevationDegrees = 89f;

    private float _humanScale = 1f;
    private float _sceneRadius = 100f;
    private BethesdaViewerBounds? _projectedFramingBounds;
    private bool _automaticProjectedFraming;
    private float _lastProjectedFramingAspect = float.NaN;
    private BethesdaViewerScenePurpose _scenePurpose;

    internal Vector3 Target { get; private set; }

    internal float Distance { get; private set; } = 300f;

    internal float AzimuthDegrees { get; private set; }

    internal float ElevationDegrees { get; private set; }

    internal float FieldOfViewRadians { get; set; } = MathF.PI / 3f;

    internal BethesdaSceneViewerCamera()
    {
        var initialOrbit = BethesdaViewerPresentationPolicy.ResolveInitialOrbit(
            BethesdaViewerScenePurpose.Unspecified,
            dedicatedRawSky: false);
        AzimuthDegrees = initialOrbit.AzimuthDegrees;
        ElevationDegrees = initialOrbit.ElevationDegrees;
    }

    /// <summary>Frames finite scene bounds and resets the scene-appropriate presentation view.</summary>
    internal void Frame(
        BethesdaViewerBounds? bounds,
        BethesdaGame game,
        BethesdaViewerScenePurpose purpose = BethesdaViewerScenePurpose.Unspecified,
        bool dedicatedRawSky = false)
    {
        _scenePurpose = purpose;
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
            _projectedFramingBounds = finite;
        }
        else
        {
            Target = Vector3.Zero;
            _sceneRadius = fallbackRadius;
            _projectedFramingBounds = null;
        }

        _automaticProjectedFraming =
            _projectedFramingBounds.HasValue &&
            BethesdaViewerPerspectiveFramingPolicy.ShouldUseProjectedBoundsFit(purpose);
        _lastProjectedFramingAspect = float.NaN;

        // Actors use their established +Y front (native compass azimuth 0, horizon-level eye), while
        // arbitrary raw assets retain the useful three-quarter default. A camera-centred raw sky
        // keeps its inverse elevation so the FOV spans the authored horizon/upper hemisphere.
        var initialOrbit = BethesdaViewerPresentationPolicy.ResolveInitialOrbit(
            purpose,
            dedicatedRawSky);
        AzimuthDegrees = initialOrbit.AzimuthDegrees;
        ElevationDegrees = initialOrbit.ElevationDegrees;
        var halfFov = Math.Clamp(FieldOfViewRadians * 0.5f, 0.05f, 1.5f);
        Distance = MathF.Max(
            (_sceneRadius / MathF.Sin(halfFov)) * FramingMargin,
            MinimumDistance);
    }

    internal void Orbit(Vector2 pixelDelta)
    {
        var orbitDelta = BethesdaViewerPresentationPolicy.OrbitDegreesForPointerDelta(
            pixelDelta,
            _scenePurpose);
        if (orbitDelta == Vector2.Zero) return;

        AzimuthDegrees = NormalizeDegrees(AzimuthDegrees + orbitDelta.X);
        ElevationDegrees = Math.Clamp(
            ElevationDegrees + orbitDelta.Y,
            MinimumElevationDegrees,
            MaximumElevationDegrees);
        _automaticProjectedFraming = false;
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
        _automaticProjectedFraming = false;
    }

    internal void Zoom(float wheelDelta)
    {
        if (!float.IsFinite(wheelDelta) || MathF.Abs(wheelDelta) < float.Epsilon) return;

        var notches = wheelDelta / 120f;
        Distance = Math.Clamp(
            Distance * MathF.Exp(-notches * ZoomExponentPerWheelNotch),
            MinimumDistance,
            MaximumDistance);
        _automaticProjectedFraming = false;
    }

    internal BethesdaSceneViewerCameraFrame GetFrame(float aspectRatio)
    {
        var aspect = float.IsFinite(aspectRatio) && aspectRatio > 0f ? aspectRatio : 1f;
        var (eyeDirection, forward, right, up) = ResolveBasis();
        if (_automaticProjectedFraming &&
            _projectedFramingBounds is { } projectedBounds &&
            (!float.IsFinite(_lastProjectedFramingAspect) ||
             MathF.Abs(_lastProjectedFramingAspect - aspect) > 0.0001f) &&
            BethesdaViewerPerspectiveFramingPolicy.TryResolveProjectedBoundsDistance(
                projectedBounds,
                eyeDirection,
                right,
                up,
                FieldOfViewRadians,
                aspect,
                out var projectedDistance))
        {
            Distance = Math.Clamp(projectedDistance, MinimumDistance, MaximumDistance);
            _lastProjectedFramingAspect = aspect;
        }

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
