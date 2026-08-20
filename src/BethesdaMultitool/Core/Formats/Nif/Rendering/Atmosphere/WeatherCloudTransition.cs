using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     One recovered weather-transition view of an authored cloud source layer. Skyrim integrates a
///     single velocity blended from outgoing/current weather; keeping that result here prevents callers
///     from advancing two copies of the sheet at different offsets.
/// </summary>
internal readonly record struct WeatherCloudTransition(
    WeatherCloudLayer? CurrentLayer,
    WeatherCloudLayer? OutgoingLayer,
    bool HasOutgoingWeather,
    float CurrentWeatherWeight,
    Vector2 ScrollVelocity,
    bool UsesSameTexture)
{
    internal float OutgoingWeatherWeight => HasOutgoingWeather ? 1f - CurrentWeatherWeight : 0f;

    internal float CurrentTextureWeight => !HasOutgoingWeather || UsesSameTexture
        ? 1f
        : CurrentWeatherWeight;

    internal float OutgoingTextureWeight => !HasOutgoingWeather || UsesSameTexture
        ? 0f
        : 1f - CurrentWeatherWeight;
}

internal static class WeatherCloudTransitionResolver
{
    internal static Vector2 AdvanceOffset(
        Vector2 offset,
        Vector2 velocity,
        float deltaSeconds)
    {
        if (!float.IsFinite(deltaSeconds) || deltaSeconds <= 0f ||
            !float.IsFinite(velocity.X) || !float.IsFinite(velocity.Y))
        {
            return offset;
        }

        var advanced = offset + velocity * deltaSeconds;
        return new Vector2(WrapUv(advanced.X), WrapUv(advanced.Y));
    }

    internal static Vector2 OffsetAtTime(Vector2 velocity, float elapsedSeconds)
    {
        return AdvanceOffset(Vector2.Zero, velocity, elapsedSeconds);
    }

    internal static Vector4 BlendSample(Vector4 current, Vector4 outgoing, float currentWeatherWeight)
    {
        return Vector4.Lerp(outgoing, current, Math.Clamp(currentWeatherWeight, 0f, 1f));
    }

    internal static Vector3 BlendSample(Vector3 current, Vector3 outgoing, float currentWeatherWeight)
    {
        return Vector3.Lerp(outgoing, current, Math.Clamp(currentWeatherWeight, 0f, 1f));
    }

    internal static float BlendSample(float current, float outgoing, float currentWeatherWeight)
    {
        return outgoing + (current - outgoing) * Math.Clamp(currentWeatherWeight, 0f, 1f);
    }

    internal static WeatherCloudTransition Resolve(
        WeatherRecord? currentWeather,
        WeatherRecord? outgoingWeather,
        int sourceLayerIndex,
        float currentWeatherWeight,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        var currentLayer = currentWeather?.FindCloudLayerBySourceIndex(sourceLayerIndex);
        var outgoingLayer = outgoingWeather?.FindCloudLayerBySourceIndex(sourceLayerIndex);
        var weight = outgoingWeather is null
            ? 1f
            : Math.Clamp(currentWeatherWeight, 0f, 1f);
        Vector2 velocity;
        // FO3 parity 2026-08-10: FO3's Clouds::Update (0x006BD930) uses the identical
        // blend-ONAM-first-then-multiply-blended-wind order (fo3-decompile-spotchecks.md).
        if (game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 &&
            outgoingWeather is not null)
        {
            // FNV Clouds::Update blends the current/outgoing ONAM scalar first. Sky::UpdateWind
            // independently blends the two DATA wind bytes, and Clouds multiplies those two blended
            // values. Blending already wind-scaled endpoint velocities is a different polynomial and
            // can run too fast (2x at the synthetic 0->255 midpoint).
            var currentSpeed = WeatherCloudMotion.ResolveBeforeLegacyWind(
                currentWeather, currentLayer, sourceLayerIndex, game);
            var outgoingSpeed = WeatherCloudMotion.ResolveBeforeLegacyWind(
                outgoingWeather, outgoingLayer, sourceLayerIndex, game);
            var blendedSpeed = Vector2.Lerp(outgoingSpeed, currentSpeed, weight);
            var blendedWind = BlendSample(
                WeatherCloudMotion.ResolveLegacyWind(currentWeather),
                WeatherCloudMotion.ResolveLegacyWind(outgoingWeather),
                weight);
            velocity = blendedSpeed * blendedWind;
        }
        else
        {
            var currentVelocity = WeatherCloudMotion.Resolve(
                currentWeather, currentLayer, sourceLayerIndex, game);
            var outgoingVelocity = WeatherCloudMotion.Resolve(
                outgoingWeather, outgoingLayer, sourceLayerIndex, game);
            velocity = outgoingWeather is null
                ? currentVelocity
                : Vector2.Lerp(outgoingVelocity, currentVelocity, weight);
        }

        return new WeatherCloudTransition(
            currentLayer,
            outgoingLayer,
            outgoingWeather is not null,
            weight,
            velocity,
            SameTexture(currentLayer?.Texture, outgoingLayer?.Texture));
    }

    private static bool SameTexture(string? left, string? right)
    {
        if (left is null || right is null) return left is null && right is null;
        return string.Equals(NormalizeTextureKey(left), NormalizeTextureKey(right),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeTextureKey(string path)
    {
        var normalized = path.Replace('/', '\\').TrimStart('\\');
        return normalized.StartsWith(@"textures\", StringComparison.OrdinalIgnoreCase)
            ? normalized[9..]
            : normalized;
    }

    private static float WrapUv(float value)
    {
        return value - MathF.Floor(value);
    }
}
