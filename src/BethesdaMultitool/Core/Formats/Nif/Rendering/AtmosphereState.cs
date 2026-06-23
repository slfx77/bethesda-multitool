using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>
///     Resolves the scene "atmosphere" — sun direction plus sky / ambient / fog colors — from a game
///     hour and an optional WTHR / climate context. The viewer feeds the result into a shared GPU
///     constant buffer the lighting / sky / water shaders read (atmosphere roadmap Phase 2+).
///     <para>
///         GROUNDED (Phase 2b) against the decompiled Xbox 360 "MemDebug" XEX
///         (<c>tools/GhidraProject/atmosphere_decompiled.txt</c>). The structure here mirrors the engine:
///         <list type="bullet">
///             <item>climate timing bytes → hours via ×1/6 (<c>Sky::GetSunriseBegin</c> reads
///             <c>climate+0x60</c> and multiplies by <c>0.16666667</c>; <c>TESClimate::Load</c> reads the
///             6-byte TNAM into <c>climate+0x60</c>);</item>
///             <item>NAM0 time-band order is Sunrise(0) / Day(1) / Sunset(2) / Night(3)
///             (<c>TESWeather::GetCloudColor</c> defaults);</item>
///             <item>the bands cross-fade within the sunrise / sunset windows (pivoting at each window
///             midpoint) and Night is solid outside the daylight span (<c>Sky::FillColorBlend</c>).
///             SIMPLIFICATION: the viewer holds the Day band solid through midday, whereas the engine
///             additionally cross-fades a separate daytime slot around solar noon — a 5th runtime band
///             this 4-band NAM0 model does not carry — and widens the windows by a small ± pad the
///             viewer drops. See <see cref="SampleBand" />;</item>
///             <item>directional sun intensity is the daylight fraction, 0 below the horizon
///             (<c>Sun::Update</c>) — see <see cref="DaylightFraction" />.</item>
///         </list>
///         The sun world-direction is an analytic east→west great-arc (Z up, peak at solar noon): the
///         engine builds it from climate-specific azimuth/elevation via NiMatrix3 rotations, which the
///         viewer approximates structurally (a plausible directional-light angle is all the lighting
///         needs). Lives in Core (not GUI-gated) so it is unit-testable.
///     </para>
/// </summary>
public static class AtmosphereState
{
    /// <summary>
    ///     Climate sunrise / sunset window in game hours (0..24), adapted from a CLMT TNAM block.
    ///     Callers without a climate pass <see cref="Default" />.
    /// </summary>
    public readonly record struct ClimateTiming(
        float SunriseBeginHour,
        float SunriseEndHour,
        float SunsetBeginHour,
        float SunsetEndHour)
    {
        /// <summary>Fallout's default day: sunrise ~06:00, sunset ~18:00.</summary>
        public static ClimateTiming Default => new(5.5f, 6.5f, 18.0f, 19.0f);

        /// <summary>Build timing from a CLMT TNAM block. Byte→hours = value × 1/6 (10-minute units) —
        /// CONFIRMED by the decompile: <c>Sky::GetSunriseBegin</c> reads <c>climate+0x60</c> and scales by
        /// <c>0.16666667</c>. Returns <see cref="Default" /> for a null block.</summary>
        public static ClimateTiming FromClimateData(ClimateTimingData? d) =>
            d is null
                ? Default
                : new(d.SunriseBegin / 6f, d.SunriseEnd / 6f, d.SunsetBegin / 6f, d.SunsetEnd / 6f);
    }

    /// <summary>The resolved per-frame atmosphere the GPU constant buffer mirrors.</summary>
    public readonly record struct Resolved(
        Vector3 SunWorldDirection,  // unit vector FROM the surface TOWARD the sun (+Z up)
        Vector3 SunColor,           // 0..1 RGB; zero when lighting is disabled or the sun is down
        float SunIntensity,         // 0 at/below the horizon → 1 across the day (engine daylight fraction)
        Vector3 AmbientColor,
        Vector3 SkyTopColor,
        Vector3 SkyHorizonColor,
        Vector3 FogColor,
        float FogNear,
        float FogFar,
        float FogPower);            // distance-fog exponent (1 = linear), from WTHR FNAM day/night power

    // Default clear-day palette — placeholder for worldspaces with no WTHR NAM0 to drive the colors.
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
    ///     drive the sky/ambient/sun/fog colors via the engine's time-band blend; otherwise a placeholder
    ///     day↔night palette is used. <paramref name="weather" /> also supplies fog distances (FNAM).
    /// </summary>
    public static Resolved Resolve(
        float gameHour,
        WeatherRecord? weather = null,
        ClimateTiming? climate = null,
        bool lightingEnabled = true)
    {
        var hour = WrapHour(gameHour);
        var (srB, srE, ssB, ssE) = NormalizeWindows(climate ?? ClimateTiming.Default);

        var sunDir = SunDirection(hour, srB, ssE);
        // Engine daylight fraction (Sun::Update): 0 below the horizon, ramps across the sunrise/sunset
        // windows, 1 through the day. Drives the directional sun intensity AND the placeholder day↔night
        // blends (the NAM0 path encodes its own per-band intensity, so it does not use this).
        var day = DaylightFraction(hour, srB, srE, ssB, ssE);

        // Colors: when the weather carries NAM0 time-band colors, blend those by the hour via the
        // engine's windowed scheme (Sky::FillColorBlend); otherwise fall back to the placeholder day↔night
        // palette. CONFIRMED category indices (Sky::UpdateColors special-cases Ambient=3 / Sunlight=4 for
        // the lighting scale; SkyUpper=0 / Fog=1 / SkyLower=7 per xEdit, corroborated by the 10-category
        // upload loop). Per channel: a missing/short NAM0 index falls back to the placeholder for that
        // channel only.
        Vector3 sunColorBase, ambient, skyTop, skyHorizon, fogColor;
        if (weather is { Colors.Count: > 0 } wc)
        {
            sunColorBase = BandOr(wc, WeatherColorType.Sunlight, hour, srB, srE, ssB, ssE, DaySun);
            ambient = BandOr(wc, WeatherColorType.Ambient, hour, srB, srE, ssB, ssE, Vector3.Lerp(NightAmbient, DayAmbient, day));
            skyTop = BandOr(wc, WeatherColorType.SkyUpper, hour, srB, srE, ssB, ssE, Vector3.Lerp(NightTint, DaySkyTop, day));
            skyHorizon = BandOr(wc, WeatherColorType.SkyLower, hour, srB, srE, ssB, ssE, Vector3.Lerp(NightTint, DaySkyHorizon, day));
            fogColor = BandOr(wc, WeatherColorType.Fog, hour, srB, srE, ssB, ssE, Vector3.Lerp(NightTint, DayFog, day));
        }
        else
        {
            // Placeholder day palette blended toward the night tint by the daylight fraction.
            sunColorBase = Vector3.Lerp(Vector3.Zero, DaySun, day);
            ambient = Vector3.Lerp(NightAmbient, DayAmbient, day);
            skyTop = Vector3.Lerp(NightTint, DaySkyTop, day);
            skyHorizon = Vector3.Lerp(NightTint, DaySkyHorizon, day);
            fogColor = Vector3.Lerp(NightTint, DayFog, day);
        }

        var sunColor = lightingEnabled ? sunColorBase : Vector3.Zero;
        var sunIntensity = lightingEnabled ? day : 0f;

        // Distance fog (grounded in Sky::UpdateFog): the engine blends day↔night near/far/power by the
        // daylight fraction — the same windowed factor as the colors — reading the WTHR FNAM floats
        // [0]=DayNear [1]=DayFar [2]=NightNear [3]=NightFar [4]=DayPower [5]=NightPower. A 2-float FNAM
        // (day near/far only) is used verbatim; with none, scene-scale defaults stand in (the engine's
        // no-weather default is a ~163840-unit near/far = effectively no fog).
        var fogNear = DefaultFogNear;
        var fogFar = DefaultFogFar;
        var fogPower = 1f;
        if (weather is { FogDistances.Count: >= 4 } wf)
        {
            fogNear = Lerp1(wf.FogDistances[2], wf.FogDistances[0], day); // night → day
            fogFar = Lerp1(wf.FogDistances[3], wf.FogDistances[1], day);
            if (wf.FogDistances.Count >= 6)
            {
                fogPower = Lerp1(wf.FogDistances[5], wf.FogDistances[4], day);
            }
        }
        else if (weather is { FogDistances.Count: >= 2 } w && w.FogDistances[1] > w.FogDistances[0])
        {
            fogNear = w.FogDistances[0];
            fogFar = w.FogDistances[1];
        }

        if (fogFar <= fogNear)
        {
            fogFar = fogNear + 1f; // keep the near→far ramp non-degenerate
        }

        fogPower = MathF.Max(fogPower, 0.01f);

        return new Resolved(sunDir, sunColor, sunIntensity, ambient, skyTop, skyHorizon, fogColor, fogNear, fogFar, fogPower);
    }

    // Clamps a climate's sunrise/sunset bounds to a strictly-ordered day (srB < srE < ssB < ssE, all in
    // [0,24]) so odd or partial climate data can never invert or collapse a window — every downstream
    // denominator (window widths and half-widths) is then provably positive.
    private static (float srB, float srE, float ssB, float ssE) NormalizeWindows(ClimateTiming t)
    {
        var srB = Math.Clamp(t.SunriseBeginHour, 0f, 11f);
        var srE = Math.Clamp(t.SunriseEndHour, srB + 0.1f, 11.5f);
        var ssE = Math.Clamp(t.SunsetEndHour, srE + 0.2f, 24f);
        var ssB = Math.Clamp(t.SunsetBeginHour, srE + 0.1f, ssE - 0.1f);
        return (srB, srE, ssB, ssE);
    }

    // Engine daylight fraction (Sun::Update): 0 outside the [sunriseBegin, sunsetEnd] daylight span,
    // ramps linearly up across the sunrise window, holds at 1 through the day, ramps down across the
    // sunset window. This is the directional sun's intensity — flat across the day, NOT a noon bell
    // (the day/sunrise/sunset look comes from the NAM0 color bands, not from dimming the sun).
    private static float DaylightFraction(float hour, float srB, float srE, float ssB, float ssE)
    {
        if (hour <= srB || hour >= ssE)
        {
            return 0f;
        }

        if (hour < srE)
        {
            return (hour - srB) / (srE - srB); // ramp up across the sunrise window
        }

        if (hour <= ssB)
        {
            return 1f; // full day
        }

        return (ssE - hour) / (ssE - ssB); // ramp down across the sunset window
    }

    // Analytic placeholder sun: a great-arc above the horizon for the whole daylight span — rises in +X
    // ("east") at sunriseBegin, arcs overhead at solar noon (Z up), sets in -X at sunsetEnd. Returns the
    // unit TO-sun direction; night (outside the span) returns a below-horizon sun. The engine builds the
    // real direction from climate azimuth/elevation via NiMatrix3 rotations (Sun::Update); this matches
    // its structure (Z up, peak at solar noon, below-horizon at night), which is what the lighting needs.
    private static Vector3 SunDirection(float hour, float srB, float ssE)
    {
        if (hour <= srB || hour >= ssE)
        {
            return new Vector3(0f, 0f, -1f); // sun below the horizon
        }

        var t01 = (hour - srB) / (ssE - srB);        // 0 at sunriseBegin → 1 at sunsetEnd
        var elevation = MathF.Sin(t01 * MathF.PI) * (MathF.PI / 2f); // 0 → π/2 (solar noon) → 0
        var azimuth = MathF.PI * t01;                // 0 (east) → π (west)
        var cosEl = MathF.Cos(elevation);
        var dir = new Vector3(
            MathF.Cos(azimuth) * cosEl,
            MathF.Sin(azimuth) * cosEl,
            MathF.Sin(elevation));
        return Vector3.Normalize(dir);
    }

    // --- WTHR NAM0 time-band sampling (Sky::FillColorBlend) -------------------------------------------
    // Samples a single NAM0 color category (by index) for the given hour, or returns the placeholder
    // fallback when the weather's Colors array is too short to hold that index.
    private static Vector3 BandOr(WeatherRecord w, WeatherColorType type, float hour,
        float srB, float srE, float ssB, float ssE, Vector3 fallback)
    {
        var i = (int)type;
        return i >= 0 && i < w.Colors.Count ? SampleBand(w.Colors[i], hour, srB, srE, ssB, ssE) : fallback;
    }

    // Blends the four NAM0 time bands across the 24h clock, grounded in Sky::FillColorBlend: the colors
    // cross-fade only WITHIN the sunrise / sunset windows, pivoting at each window's midpoint, and stay
    // steady between them. A band reads:
    //   night  for hour outside the sunriseBegin..sunsetEnd daylight span;
    //   night→sunrise  over sunriseBegin..sunriseMid, sunrise→day over sunriseMid..sunriseEnd;
    //   day    over sunriseEnd..sunsetBegin;
    //   day→sunset over sunsetBegin..sunsetMid, sunset→night over sunsetMid..sunsetEnd.
    // Night is a single solid color outside the windows, so the midnight wrap is inherently continuous.
    // Every segment denominator is a window half-width, provably > 0 after NormalizeWindows.
    //
    // SIMPLIFICATION vs. the engine (verified against Sky::FillColorBlend): the engine does NOT hold Day
    // perfectly solid — between the windows it cross-fades the Day band toward a separate daytime slot,
    // pivoting at a stored solar-noon time, and uses window edges padded out by a constant. That extra
    // daytime slot is a 5th runtime band index this 4-band (Sunrise/Day/Sunset/Night) NAM0 model can't
    // represent, and the daytime colors it blends are near-identical, so holding Day steady is a faithful
    // approximation of the dominant look (the visible sunrise/sunset transitions are reproduced exactly).
    private static Vector3 SampleBand(WeatherColor c, float hour, float srB, float srE, float ssB, float ssE)
    {
        var srMid = (srB + srE) * 0.5f;
        var ssMid = (ssB + ssE) * 0.5f;

        var night = ToVec(c.Night);
        var sunrise = ToVec(c.Sunrise);
        var day = ToVec(c.Day);
        var sunset = ToVec(c.Sunset);

        if (hour < srB || hour >= ssE)
        {
            return night; // solid night outside the daylight span
        }

        if (hour < srMid)
        {
            return Vector3.Lerp(night, sunrise, (hour - srB) / (srMid - srB));
        }

        if (hour < srE)
        {
            return Vector3.Lerp(sunrise, day, (hour - srMid) / (srE - srMid));
        }

        if (hour < ssB)
        {
            return day; // solid day
        }

        if (hour < ssMid)
        {
            return Vector3.Lerp(day, sunset, (hour - ssB) / (ssMid - ssB));
        }

        return Vector3.Lerp(sunset, night, (hour - ssMid) / (ssE - ssMid));
    }

    private static Vector3 ToVec(WeatherRgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f);

    /// <summary>
    ///     Samples a cloud-layer's PNAM Time-of-Day color — RGB tint + <b>A = layer opacity</b> — for the
    ///     given game hour, using the SAME windowed band blend as the sky colors (Sky::FillColorBlend).
    ///     Public so the sky-geometry renderer can resolve each cloud layer's engine per-draw color uniform
    ///     (SkyShader::SetupGeometryConstants) per frame, instead of a guessed daylight tint.
    /// </summary>
    public static Vector4 SampleCloudColor(WeatherColor c, float gameHour, ClimateTiming? timing)
    {
        var (srB, srE, ssB, ssE) = NormalizeWindows(timing ?? ClimateTiming.Default);
        return SampleBand4(c, WrapHour(gameHour), srB, srE, ssB, ssE);
    }

    // RGBA twin of SampleBand — identical windowed blend, but keeps the alpha channel (cloud opacity).
    private static Vector4 SampleBand4(WeatherColor c, float hour, float srB, float srE, float ssB, float ssE)
    {
        var srMid = (srB + srE) * 0.5f;
        var ssMid = (ssB + ssE) * 0.5f;
        var night = ToVec4(c.Night);
        var sunrise = ToVec4(c.Sunrise);
        var day = ToVec4(c.Day);
        var sunset = ToVec4(c.Sunset);

        if (hour < srB || hour >= ssE) return night;
        if (hour < srMid) return Vector4.Lerp(night, sunrise, (hour - srB) / (srMid - srB));
        if (hour < srE) return Vector4.Lerp(sunrise, day, (hour - srMid) / (srE - srMid));
        if (hour < ssB) return day;
        if (hour < ssMid) return Vector4.Lerp(day, sunset, (hour - ssB) / (ssMid - ssB));
        return Vector4.Lerp(sunset, night, (hour - ssMid) / (ssE - ssMid));
    }

    private static Vector4 ToVec4(WeatherRgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    private static float Lerp1(float a, float b, float t) => a + ((b - a) * t);

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
