using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering;

/// <summary>
///     Resolves the scene "atmosphere" — sun direction plus sky / ambient / fog colors — from a game
///     hour and an optional WTHR / climate context. The viewer feeds the result into a shared GPU
///     constant buffer the lighting / sky / water shaders read (atmosphere roadmap Phase 2+).
///     <para>
///         SKELETON: the sun curve is a simple analytic placeholder and the colors are fixed clear-day
///         defaults blended toward a dim night tint. Phase 2b replaces both with the decompiled engine
///         sun-angle and the WTHR NAM0 time-band color blend; the public <see cref="Resolved" /> shape
///         here is exactly what those fills target, so consumers (lighting/sky shaders, P3+) can be
///         wired against it now. Lives in Core (not GUI-gated) so it is unit-testable.
///     </para>
/// </summary>
public static class AtmosphereState
{
    /// <summary>
    ///     Climate sunrise / sunset window in game hours (0..24), adapted from a CLMT TNAM block once
    ///     CLMT parsing lands. Until then callers pass <see cref="Default" />.
    /// </summary>
    public readonly record struct ClimateTiming(
        float SunriseBeginHour,
        float SunriseEndHour,
        float SunsetBeginHour,
        float SunsetEndHour)
    {
        /// <summary>Fallout's default day: sunrise ~06:00, sunset ~18:00.</summary>
        public static ClimateTiming Default => new(5.5f, 6.5f, 18.0f, 19.0f);

        /// <summary>Build timing from a CLMT TNAM block. PROVISIONAL byte→hours encoding (≈ value / 6,
        /// i.e. 10-minute units) — confirmed in P2b. Returns <see cref="Default" /> for a null block.</summary>
        public static ClimateTiming FromClimateData(ClimateTimingData? d) =>
            d is null
                ? Default
                : new(d.SunriseBegin / 6f, d.SunriseEnd / 6f, d.SunsetBegin / 6f, d.SunsetEnd / 6f);
    }

    /// <summary>The resolved per-frame atmosphere the GPU constant buffer mirrors.</summary>
    public readonly record struct Resolved(
        Vector3 SunWorldDirection,  // unit vector FROM the surface TOWARD the sun (+Z up)
        Vector3 SunColor,           // 0..1 RGB; zero when lighting is disabled or the sun is down
        float SunIntensity,         // 0 at/below the horizon → 1 at solar noon
        Vector3 AmbientColor,
        Vector3 SkyTopColor,
        Vector3 SkyHorizonColor,
        Vector3 FogColor,
        float FogNear,
        float FogFar);

    // Default clear-day palette — placeholder until WTHR NAM0 drives these in P2b/P3.
    private static readonly Vector3 DaySun = new(1.00f, 0.96f, 0.88f);
    private static readonly Vector3 DayAmbient = new(0.40f, 0.43f, 0.50f);
    private static readonly Vector3 NightAmbient = new(0.05f, 0.06f, 0.10f);
    private static readonly Vector3 DaySkyTop = new(0.25f, 0.45f, 0.75f);
    private static readonly Vector3 DaySkyHorizon = new(0.66f, 0.74f, 0.84f);
    private static readonly Vector3 DayFog = new(0.62f, 0.70f, 0.80f);
    private static readonly Vector3 NightTint = new(0.03f, 0.04f, 0.09f);

    // Scene-scale fog fallback (world units) when the weather carries no FNAM distances.
    private const float DefaultFogNear = 4096f;
    private const float DefaultFogFar = 98304f;

    /// <summary>
    ///     Resolves the atmosphere for <paramref name="gameHour" /> (0..24, wrapped). When
    ///     <paramref name="lightingEnabled" /> is false the sun contribution is zeroed so a shader can
    ///     fall back to flat shading. When <paramref name="weather" /> carries WTHR NAM0 colors they
    ///     drive the sky/ambient/sun/fog colors via a time-band blend (P4); otherwise a placeholder
    ///     day↔night palette is used. <paramref name="weather" /> also supplies fog distances (FNAM).
    /// </summary>
    public static Resolved Resolve(
        float gameHour,
        WeatherRecord? weather = null,
        ClimateTiming? climate = null,
        bool lightingEnabled = true)
    {
        var hour = WrapHour(gameHour);
        var timing = climate ?? ClimateTiming.Default;
        var (sunDir, dayFactor) = SunDirection(hour, timing);

        // Colors: when the weather carries NAM0 time-band colors, blend those by the hour (real game
        // look — P4); otherwise fall back to the placeholder day↔night palette. PROVISIONAL color-slot
        // indices (xEdit) — P2b confirms them against the decompile. Per channel: a missing/short NAM0
        // index falls back to the placeholder for that channel only.
        Vector3 sunColorBase, ambient, skyTop, skyHorizon, fogColor;
        if (weather is { Colors.Count: > 0 } wc)
        {
            // NAM0 bands already encode day/night intensity, so the sun color is NOT re-attenuated by
            // dayFactor here (the shader's N·L still gates it on the below-horizon night sun).
            sunColorBase = BandOr(wc, WeatherColorType.Sunlight, hour, timing, DaySun);
            ambient = BandOr(wc, WeatherColorType.Ambient, hour, timing, Vector3.Lerp(NightAmbient, DayAmbient, dayFactor));
            skyTop = BandOr(wc, WeatherColorType.SkyUpper, hour, timing, Vector3.Lerp(NightTint, DaySkyTop, dayFactor));
            skyHorizon = BandOr(wc, WeatherColorType.SkyLower, hour, timing, Vector3.Lerp(NightTint, DaySkyHorizon, dayFactor));
            fogColor = BandOr(wc, WeatherColorType.Fog, hour, timing, Vector3.Lerp(NightTint, DayFog, dayFactor));
        }
        else
        {
            // Placeholder day palette blended toward the night tint by the daytime factor.
            sunColorBase = Vector3.Lerp(Vector3.Zero, DaySun, dayFactor);
            ambient = Vector3.Lerp(NightAmbient, DayAmbient, dayFactor);
            skyTop = Vector3.Lerp(NightTint, DaySkyTop, dayFactor);
            skyHorizon = Vector3.Lerp(NightTint, DaySkyHorizon, dayFactor);
            fogColor = Vector3.Lerp(NightTint, DayFog, dayFactor);
        }

        var sunColor = lightingEnabled ? sunColorBase : Vector3.Zero;
        var sunIntensity = lightingEnabled ? dayFactor : 0f;

        // FNAM fog distances (0 = Day Near, 1 = Day Far) are unambiguous floats, so use them when the
        // weather supplies them; otherwise fall back to scene-scale constants.
        var fogNear = DefaultFogNear;
        var fogFar = DefaultFogFar;
        if (weather is { FogDistances.Count: >= 2 } w && w.FogDistances[1] > w.FogDistances[0])
        {
            fogNear = w.FogDistances[0];
            fogFar = w.FogDistances[1];
        }

        return new Resolved(sunDir, sunColor, sunIntensity, ambient, skyTop, skyHorizon, fogColor, fogNear, fogFar);
    }

    // Analytic placeholder sun: rises in +X ("east"), arcs overhead at solar noon, sets in -X, with
    // +Z up. Returns the unit TO-sun direction and a 0..1 daytime factor (= the sun's height). Night
    // (outside the sunrise→sunset window) returns a below-horizon sun and factor 0.
    // TODO(atmosphere P2b): replace with the decompiled TESClimate/Sky sun-angle curve.
    private static (Vector3 Dir, float DayFactor) SunDirection(float hour, ClimateTiming t)
    {
        var sunrise = (t.SunriseBeginHour + t.SunriseEndHour) * 0.5f;
        var sunset = (t.SunsetBeginHour + t.SunsetEndHour) * 0.5f;
        if (sunset <= sunrise || hour <= sunrise || hour >= sunset)
        {
            return (new Vector3(0f, 0f, -1f), 0f); // sun below the horizon
        }

        var t01 = (hour - sunrise) / (sunset - sunrise); // 0 at sunrise → 1 at sunset
        var elevation = MathF.Sin(t01 * MathF.PI) * (MathF.PI / 2f); // 0 → π/2 (noon) → 0
        var azimuth = MathF.PI * t01;                                // 0 (east) → π (west)
        var cosEl = MathF.Cos(elevation);
        var dir = new Vector3(
            MathF.Cos(azimuth) * cosEl,
            MathF.Sin(azimuth) * cosEl,
            MathF.Sin(elevation));
        return (Vector3.Normalize(dir), MathF.Sin(elevation));
    }

    // --- WTHR NAM0 time-band sampling (P4) -----------------------------------------------------------
    // Samples a single NAM0 color category (by PROVISIONAL xEdit index) for the given hour, or returns
    // the placeholder fallback when the weather's Colors array is too short to hold that index.
    private static Vector3 BandOr(WeatherRecord w, WeatherColorType type, float hour, ClimateTiming t, Vector3 fallback)
    {
        var i = (int)type;
        return i >= 0 && i < w.Colors.Count ? SampleBand(w.Colors[i], hour, t) : fallback;
    }

    // Blends the four NAM0 time bands (Sunrise/Day/Sunset/Night) across the 24h clock by climate timing.
    // Keyframes: Night at 0/24, Sunrise at the sunrise midpoint, Day at noon (midpoint of sunrise/sunset),
    // Sunset at the sunset midpoint — the hour lerps the two bracketing keyframes, so the midnight
    // Night↔Night wrap is continuous. Keyframe hours are clamped to a sane day order so odd climate data
    // can't invert or collapse a segment.
    private static Vector3 SampleBand(WeatherColor c, float hour, ClimateTiming t)
    {
        var sunriseMid = Math.Clamp((t.SunriseBeginHour + t.SunriseEndHour) * 0.5f, 1f, 11f);
        var sunsetMid = Math.Clamp((t.SunsetBeginHour + t.SunsetEndHour) * 0.5f, 13f, 23f);
        var noon = (sunriseMid + sunsetMid) * 0.5f;

        var night = ToVec(c.Night);
        var sunrise = ToVec(c.Sunrise);
        var day = ToVec(c.Day);
        var sunset = ToVec(c.Sunset);

        if (hour <= sunriseMid)
        {
            return Vector3.Lerp(night, sunrise, hour / sunriseMid);
        }

        if (hour <= noon)
        {
            return Vector3.Lerp(sunrise, day, (hour - sunriseMid) / (noon - sunriseMid));
        }

        if (hour <= sunsetMid)
        {
            return Vector3.Lerp(day, sunset, (hour - noon) / (sunsetMid - noon));
        }

        return Vector3.Lerp(sunset, night, (hour - sunsetMid) / (24f - sunsetMid));
    }

    private static Vector3 ToVec(WeatherRgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    private static float WrapHour(float hour)
    {
        hour %= 24f;
        if (hour < 0f)
        {
            hour += 24f;
        }

        return hour;
    }
}
