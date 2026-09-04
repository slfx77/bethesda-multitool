using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Procedural;

/// <summary>Provenance for the deterministic direction supplied to Fallout 4 spline wind.</summary>
internal enum Fo4BendableSplineWindDirectionSelection
{
    HostFallback,
    CurrentWeatherCenter,
    OutgoingWeatherCenterFallback,
    InteriorZero
}

/// <summary>Resolved weather inputs shared by the live and deterministic-capture hosts.</summary>
internal readonly record struct Fo4BendableSplineWeatherWind(
    float NormalizedSpeed,
    float NormalizedTurbulence,
    Vector2 Direction,
    float DirectionRadians,
    float DirectionRangeDegrees,
    Fo4BendableSplineWindDirectionSelection DirectionSelection);

/// <summary>The two existing constant-buffer lanes consumed by the recovered spline vertex path.</summary>
internal readonly record struct Fo4BendableSplineWindConstants(
    Vector4 WindVector,
    Vector4 WindVectorEx);

/// <summary>
///     API-neutral Fallout 4 bendable-spline wind math recovered from the retail shader and its
///     constant producer. This deliberately exposes no guessed shader ID or stochastic RNG state.
/// </summary>
internal static class Fo4BendableSplineWind
{
    internal const float LowestFrequency = 0.5f;
    internal const float HighestFrequency = 4f;
    internal const float LowestSpeedLowMultiplier = 0f;
    internal const float HighestSpeedHighMultiplier = 1.5f;
    internal const float MaximumDefaultDisplacement = 405f;

    private const float TimerScale = 0.0016666667070239782f;
    private const float TimerRadians = 6.283180236816406f;
    private const float FrequencyEpsilon = 1e-6f;
    private const float DirectionEpsilonSquared = 1e-12f;

    internal static Fo4BendableSplineWeatherWind ResolveWeather(
        WeatherData? current,
        WeatherData? outgoing,
        float currentWeight,
        bool isInterior,
        Vector2 hostFallbackDirection)
    {
        if (isInterior)
        {
            return new Fo4BendableSplineWeatherWind(
                0f,
                0f,
                Vector2.UnitX,
                0f,
                0f,
                Fo4BendableSplineWindDirectionSelection.InteriorZero);
        }

        var weight = float.IsFinite(currentWeight)
            ? Math.Clamp(currentWeight, 0f, 1f)
            : 1f;
        var currentSpeed = NormalizeByte(current?.WindSpeed ?? 0);
        var currentTurbulence = NormalizeByte(current?.WindTurbulence ?? 0);
        float speed;
        float turbulence;
        if (outgoing is null)
        {
            speed = currentSpeed;
            turbulence = currentTurbulence;
        }
        else
        {
            speed = Lerp(NormalizeByte(outgoing.WindSpeed), currentSpeed, weight);
            turbulence = Lerp(
                NormalizeByte(outgoing.WindTurbulence ?? 0),
                currentTurbulence,
                weight);
        }

        byte? directionByte;
        byte? rangeByte;
        Fo4BendableSplineWindDirectionSelection selection;
        if (current?.WindDirection is { } currentDirection)
        {
            directionByte = currentDirection;
            rangeByte = current.WindDirectionRange;
            selection = Fo4BendableSplineWindDirectionSelection.CurrentWeatherCenter;
        }
        else if (outgoing?.WindDirection is { } outgoingDirection)
        {
            directionByte = outgoingDirection;
            rangeByte = outgoing.WindDirectionRange;
            selection = Fo4BendableSplineWindDirectionSelection.OutgoingWeatherCenterFallback;
        }
        else
        {
            directionByte = null;
            rangeByte = null;
            selection = Fo4BendableSplineWindDirectionSelection.HostFallback;
        }

        if (directionByte is { } authoredDirection)
        {
            var directionRadians = DirectionByteToRadians(authoredDirection);
            return new Fo4BendableSplineWeatherWind(
                speed,
                turbulence,
                new Vector2(MathF.Cos(directionRadians), MathF.Sin(directionRadians)),
                directionRadians,
                DirectionRangeByteToDegrees(rangeByte ?? 0),
                selection);
        }

        var fallback = NormalizeDirection(hostFallbackDirection);
        var fallbackRadians = MathF.Atan2(fallback.Y, fallback.X);
        if (fallbackRadians < 0f)
        {
            fallbackRadians += MathF.Tau;
        }

        return new Fo4BendableSplineWeatherWind(
            speed,
            turbulence,
            fallback,
            fallbackRadians,
            0f,
            selection);
    }

    internal static Fo4BendableSplineWindConstants BuildConstants(
        Vector2 direction,
        float normalizedSpeed,
        float normalizedTurbulence,
        float flexibility,
        double animationSeconds,
        bool animationsEnabled)
    {
        var normalizedDirection = NormalizeDirection(direction);
        var angle = MathF.Atan2(normalizedDirection.Y, normalizedDirection.X);
        if (angle < 0f)
        {
            angle += MathF.Tau;
        }

        if (!animationsEnabled ||
            !float.IsFinite(normalizedSpeed) ||
            !float.IsFinite(normalizedTurbulence) ||
            !float.IsFinite(flexibility))
        {
            return new Fo4BendableSplineWindConstants(
                new Vector4(angle, float.IsFinite(flexibility) ? flexibility : 0f, 0f, 0f),
                Vector4.Zero);
        }

        var speed = Math.Clamp(normalizedSpeed, 0f, 1f);
        var turbulence = Math.Clamp(normalizedTurbulence, 0f, 1f);
        var frequency = Lerp(LowestFrequency, HighestFrequency, turbulence);
        var minimumSpeed = ((1f - turbulence) + turbulence * LowestSpeedLowMultiplier) * speed;
        var maximumSpeed = ((1f - turbulence) + turbulence * HighestSpeedHighMultiplier) * speed;
        var packedTimer = PackTimer(animationSeconds);
        return new Fo4BendableSplineWindConstants(
            new Vector4(angle, flexibility, packedTimer, packedTimer),
            new Vector4(minimumSpeed * 300f, maximumSpeed * 300f, frequency, 0f));
    }

    /// <summary>CPU oracle for tests and diagnostics; the runtime deformation executes in HLSL.</summary>
    internal static Vector3 DeformWorldPosition(
        Vector3 worldPosition,
        Vector3 absolutePlacement,
        float packedAlpha,
        in Fo4BendableSplineWindConstants constants)
    {
        var restPosition = worldPosition;
        var wind = constants.WindVector;
        var windEx = constants.WindVectorEx;
        if (!IsFinite(worldPosition) ||
            !IsFinite(absolutePlacement) ||
            !IsFinite(wind) ||
            !IsFinite(windEx) ||
            !float.IsFinite(packedAlpha) ||
            MathF.Abs(windEx.Z) <= FrequencyEpsilon)
        {
            return worldPosition;
        }

        var alpha = Math.Clamp(packedAlpha, 0f, 1f);
        var placementPhase = 0.001f *
                             (absolutePlacement.X + absolutePlacement.Y + absolutePlacement.Z);
        var spatial = placementPhase + MathF.PI *
            (placementPhase * MathF.Sin((wind.W * 5f) * placementPhase) / (windEx.Z * 10f));
        var phase = (1f + wind.Y * alpha) * spatial -
                    40f * windEx.Z +
                    50f * windEx.Z * wind.W;
        var speedRange = windEx.Y - windEx.X;
        var amplitude = alpha *
            (0.002f * speedRange * speedRange * MathF.Sin(phase) + 0.25f * windEx.X);
        if (!float.IsFinite(amplitude))
        {
            return worldPosition;
        }

        worldPosition.X += MathF.Cos(wind.X) * amplitude;
        worldPosition.Y += MathF.Sin(wind.X) * amplitude;
        return IsFinite(worldPosition) ? worldPosition : restPosition;
    }

    internal static float PackTimer(double animationSeconds)
    {
        var seconds = (float)animationSeconds;
        if (!float.IsFinite(seconds))
        {
            return 0f;
        }

        // The old PDB-matched producer performs these as two distinct single-precision operations.
        var scaled = seconds * TimerScale;
        return scaled * TimerRadians;
    }

    internal static float BoundsExpansion(float maximumPackedAlpha)
    {
        return float.IsFinite(maximumPackedAlpha)
            ? MaximumDefaultDisplacement * Math.Clamp(maximumPackedAlpha, 0f, 1f)
            : 0f;
    }

    internal static Vector2 NormalizeDirection(Vector2 direction)
    {
        var lengthSquared = direction.LengthSquared();
        return float.IsFinite(lengthSquared) && lengthSquared > DirectionEpsilonSquared
            ? direction / MathF.Sqrt(lengthSquared)
            : Vector2.UnitX;
    }

    internal static float DirectionByteToRadians(byte value) => value / 255f * MathF.Tau;

    internal static float DirectionRangeByteToDegrees(byte value) => value / 255f * 180f;

    private static float NormalizeByte(byte value) => value / 255f;

    private static float Lerp(float from, float to, float amount) => from + (to - from) * amount;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);

    private static bool IsFinite(Vector4 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) &&
        float.IsFinite(value.Z) && float.IsFinite(value.W);
}
