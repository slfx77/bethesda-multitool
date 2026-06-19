using System.Numerics;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

internal enum RendererCameraMotionKind
{
    Static,
    Forward,
    Orbit,
    Sweep
}

internal readonly record struct RendererProfilerCameraPose(
    Vector3 Position,
    float Yaw,
    float Pitch,
    float RenderDistance);

internal static class RendererCameraMotion
{
    // Each sweep leg travels 8 cells before turning, so the camera actually traverses new terrain
    // (continuously streaming fresh cells/refs/textures) instead of oscillating within a ~2-cell
    // box and re-streaming the same cached content — a far better streaming-hitch benchmark.
    private const float SweepLegLength = WorldGridConstants.CellSize * 8f;
    private const float OrbitRadius = WorldGridConstants.CellSize * 4f;

    private static readonly Vector2[] SweepDirections =
    [
        new(1f, 0f),
        new(0f, 1f),
        new(-1f, 0f),
        new(0f, 1f),
        new(1f, 0f),
        new(0f, -1f),
        new(-1f, 0f),
        new(0f, -1f),
    ];

    internal static bool TryParseKind(string? value, out RendererCameraMotionKind kind)
    {
        kind = RendererCameraMotionKind.Static;
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        switch (value.Trim().ToLowerInvariant())
        {
            case "static":
                kind = RendererCameraMotionKind.Static;
                return true;
            case "forward":
                kind = RendererCameraMotionKind.Forward;
                return true;
            case "orbit":
                kind = RendererCameraMotionKind.Orbit;
                return true;
            case "sweep":
                kind = RendererCameraMotionKind.Sweep;
                return true;
            default:
                return false;
        }
    }

    internal static RendererProfilerCameraPose Evaluate(
        RendererCameraMotionKind kind,
        RendererProfilerCameraPose initial,
        float speed,
        double elapsedSeconds)
    {
        var safeSpeed = MathF.Max(0f, speed);
        var elapsed = MathF.Max(0f, (float)elapsedSeconds);
        return kind switch
        {
            RendererCameraMotionKind.Forward => MoveForward(initial, safeSpeed * elapsed),
            RendererCameraMotionKind.Orbit => Orbit(initial, safeSpeed, elapsed),
            RendererCameraMotionKind.Sweep => Sweep(initial, safeSpeed * elapsed),
            _ => initial
        };
    }

    internal static Vector3 ForwardFromYawPitch(float yaw, float pitch)
    {
        var cy = MathF.Cos(yaw);
        var sy = MathF.Sin(yaw);
        var cp = MathF.Cos(pitch);
        var sp = MathF.Sin(pitch);
        return new Vector3(sy * cp, cy * cp, sp);
    }

    internal static Vector2 GroundForward(float yaw)
    {
        var forward = new Vector2(MathF.Sin(yaw), MathF.Cos(yaw));
        return NormalizeOr(forward, new Vector2(0f, 1f));
    }

    private static RendererProfilerCameraPose MoveForward(RendererProfilerCameraPose initial, float distance)
    {
        var forward = GroundForward(initial.Yaw);
        var offset = new Vector3(forward.X * distance, forward.Y * distance, 0f);
        return initial with { Position = initial.Position + offset };
    }

    private static RendererProfilerCameraPose Orbit(RendererProfilerCameraPose initial, float speed, float elapsed)
    {
        if (speed <= 0f)
        {
            return initial;
        }

        var forward = GroundForward(initial.Yaw);
        var radius = MathF.Max(OrbitRadius, speed * 2f);
        var target = new Vector2(initial.Position.X, initial.Position.Y) + forward * radius;
        var initialOffset = new Vector2(initial.Position.X, initial.Position.Y) - target;
        var angle = elapsed * speed / radius;
        var rotated = Rotate(initialOffset, angle);
        var xy = target + rotated;
        var yaw = YawFromGroundDirection(NormalizeOr(target - xy, forward));
        return initial with
        {
            Position = new Vector3(xy.X, xy.Y, initial.Position.Z),
            Yaw = yaw
        };
    }

    private static RendererProfilerCameraPose Sweep(RendererProfilerCameraPose initial, float distance)
    {
        if (distance <= 0f)
        {
            return initial;
        }

        var cycleLength = SweepLegLength * SweepDirections.Length;
        var remaining = distance % cycleLength;
        var offset = Vector2.Zero;
        var direction = SweepDirections[0];

        for (var i = 0; i < SweepDirections.Length; i++)
        {
            direction = SweepDirections[i];
            var step = MathF.Min(SweepLegLength, remaining);
            offset += direction * step;
            remaining -= step;
            if (remaining <= 0f)
            {
                break;
            }
        }

        return initial with
        {
            Position = new Vector3(
                initial.Position.X + offset.X,
                initial.Position.Y + offset.Y,
                initial.Position.Z),
            Yaw = YawFromGroundDirection(direction)
        };
    }

    private static Vector2 Rotate(Vector2 value, float radians)
    {
        var c = MathF.Cos(radians);
        var s = MathF.Sin(radians);
        return new Vector2(value.X * c - value.Y * s, value.X * s + value.Y * c);
    }

    private static Vector2 NormalizeOr(Vector2 value, Vector2 fallback)
    {
        var length = value.Length();
        return length > 0.0001f ? value / length : fallback;
    }

    private static float YawFromGroundDirection(Vector2 direction) =>
        MathF.Atan2(direction.X, direction.Y);
}
