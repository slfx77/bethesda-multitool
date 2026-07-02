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

    /// <summary>Apex of the analytic sun arc at solar noon, in radians (50°). The engine's sun path is a
    /// triangle wave with a constant lateral offset — Sun::Update: X sweeps ±cb68 linearly across the day,
    /// Z = |cb68| − |X|, Y = cb74 constant (atmosphere_decompiled.txt:1859-1895) — so its noon elevation is
    /// atan(cb68/|cb74|), NOT the zenith; the .data constants aren't in the decompile artifacts, so the
    /// exact engine apex is open. 50° matches the game's real-world setting (October, ~36°N ⇒ solar noon
    /// ~45-50°) and restores vertical-surface N·L at midday: at the old 72° apex a wall facing the sun got
    /// N·L ≤ cos72° = 0.31 and noon walls read darker than 6 PM ones (the reported "too dark at midday");
    /// at 50° the same wall gets up to cos50° = 0.64.</summary>
    private const float PeakSunElevation = 50f * (MathF.PI / 180f);

    // Twilight (sunrise/sunset) placeholder tints — used by the no-NAM0 palette so a worldspace without
    // authored weather still warms to orange at dawn/dusk instead of fading straight blue→night. Sunrise
    // and sunset share these (the look is symmetric). Warm low-sun light: orange horizon, muted purple
    // upper sky, warm fog + ambient. (When a weather DOES carry NAM0, its authored bands win.)
    private static readonly Vector3 TwilightSun = new(1.00f, 0.60f, 0.33f);
    private static readonly Vector3 TwilightAmbient = new(0.34f, 0.27f, 0.30f);
    private static readonly Vector3 TwilightSkyTop = new(0.38f, 0.36f, 0.52f);
    private static readonly Vector3 TwilightSkyHorizon = new(0.96f, 0.52f, 0.32f);
    private static readonly Vector3 TwilightFog = new(0.74f, 0.54f, 0.50f);

    // Max fraction of the dome horizon replaced by the warm NAM0 "Horizon" band when the sun is at the
    // horizon (scaled down by sun elevation, so noon is untouched). Tuned for a visible-but-not-garish glow.
    private const float HorizonGlowStrength = 0.7f;

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
            var skyLowerBand = BandOr(wc, WeatherColorType.SkyLower, hour, srB, srE, ssB, ssE, Vector3.Lerp(NightTint, DaySkyHorizon, day));
            // Horizon glow: FNV authors the warm sunrise/sunset horizon in the SEPARATE NAM0 "Horizon"
            // band (index 8) — e.g. NVWastelandClear Sunset Horizon=(219,192,174) warm vs its blue
            // SkyUpper/SkyLower — which the dome gradient (SkyUpper↔SkyLower) otherwise drops, leaving the
            // sky blue at sunset ("no sunset lighting"). Fold the Horizon band into the dome's horizon,
            // gated by sun elevation so it only appears when the sun is low (zero at noon → the daytime
            // sky is unchanged), reproducing the warm low-sun horizon glow.
            var horizonBand = BandOr(wc, WeatherColorType.Horizon, hour, srB, srE, ssB, ssE, skyLowerBand);
            // Glow tracks the sun's PROXIMITY to the horizon (|Z|), peaking at dawn/dusk and fading to
            // zero both at noon and in deep night. The old `1 - Z*1.5` gate saturated for the whole night
            // (sun far below ⇒ Z ≈ -1), folding the Horizon band into the dome at full strength after
            // dark — harmless while the night fold was the dark Night column, but the 6-band blend also
            // reaches the Midnight column, which weathers author as junk for this category (e.g.
            // NVWastelandClear Horizon Midnight = (43,200,213) teal) since the engine only shows the
            // horizon glow around the low sun.
            var horizonGlow = Math.Clamp(1f - (MathF.Abs(sunDir.Z) * 1.5f), 0f, 1f);
            skyHorizon = Vector3.Lerp(skyLowerBand, horizonBand, horizonGlow * HorizonGlowStrength);
            fogColor = BandOr(wc, WeatherColorType.Fog, hour, srB, srE, ssB, ssE, Vector3.Lerp(NightTint, DayFog, day));
        }
        else
        {
            // Placeholder palette with explicit sunrise/sunset phases (same windowed scheme as the NAM0
            // path) so a worldspace with NO authored NAM0 weather still shows a warm dawn/dusk instead of
            // fading straight blue→night. Sunrise and sunset share the twilight tints. The sun BASE warms
            // through the windows; the daylight-fraction fade (sunColor = base * day, below) still dims it
            // to near-zero at the horizon, so the directional reads as a low, warm sunrise/sunset light.
            sunColorBase = SampleBandV(DaySun, TwilightSun, DaySun, TwilightSun, hour, srB, srE, ssB, ssE);
            ambient = SampleBandV(NightAmbient, TwilightAmbient, DayAmbient, TwilightAmbient, hour, srB, srE, ssB, ssE);
            skyTop = SampleBandV(NightTint, TwilightSkyTop, DaySkyTop, TwilightSkyTop, hour, srB, srE, ssB, ssE);
            skyHorizon = SampleBandV(NightTint, TwilightSkyHorizon, DaySkyHorizon, TwilightSkyHorizon, hour, srB, srE, ssB, ssE);
            fogColor = SampleBandV(NightTint, TwilightFog, DayFog, TwilightFog, hour, srB, srE, ssB, ssE);
        }

        // Fade the directional sun colour by the daylight fraction. GROUNDED in Sky::UpdateColors
        // (atmosphere_decompiled.txt): the engine modulates the Sunlight (cat 4) colour by a daylight
        // factor (sky+0x100), so the directional vanishes as the sun sets. This fixes BOTH night bugs with
        // NO direction hack: (1) at night day=0 ⇒ the directional is 0 ⇒ a below-horizon sun vector can no
        // longer light mesh undersides; (2) `day` is CONTINUOUS and 0 at both window edges (srB, ssE), so
        // there is no sudden dark↔bright step at the night boundaries (the earlier zenith-direction hack
        // caused that step — a 90° swing in N·L). The NAM0 night Sunlight band is thus correctly suppressed
        // at night instead of lighting the scene. The shader stays decompile-faithful
        // (`mad NdotL, PSLightColor, Ambient`) — the fade is baked into PSLightColor here, as the engine does.
        var sunColor = lightingEnabled ? sunColorBase * day : Vector3.Zero;
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
    // unit TO-sun direction. The engine builds the real direction from climate azimuth/elevation via
    // NiMatrix3 rotations (Sun::Update); this matches its structure (Z up, peak at solar noon), which is
    // what the lighting needs.
    //
    // Analytic placeholder sun: a great-arc above the horizon for the whole daylight span — rises in +X
    // ("east") at sunriseBegin, arcs overhead at solar noon (Z up), sets in -X at sunsetEnd. Returns the
    // unit TO-sun direction. Night (outside the span) returns a below-horizon vector — which is now FINE:
    // the directional COLOUR is faded to 0 at night by the daylight fraction (see Resolve), so the night
    // direction is never actually applied to the scene. (An earlier attempt aimed it at the zenith to stop
    // "lit from below"; that caused a sudden dark↔bright step at the night boundaries — the colour fade is
    // the grounded fix, so the direction is back to the natural below-horizon arc.) The engine builds the
    // real direction from climate azimuth/elevation via NiMatrix3 rotations (Sun::Update); this matches its
    // structure (Z up, peak at solar noon). NOTE: still NOT decompile-confirmed end-to-end — the night
    // directional path (Sun::Update → scene light → Lighting30Shader::SetLight, and pinning sky+0x100) is
    // owed; the colour fade matches the Sky::UpdateColors cat-4 modulation, which is as far as it's traced.
    private static Vector3 SunDirection(float hour, float srB, float ssE)
    {
        if (hour <= srB || hour >= ssE)
        {
            return new Vector3(0f, 0f, -1f); // night: below the horizon (moot — the directional colour is faded to 0)
        }

        var t01 = (hour - srB) / (ssE - srB);        // 0 at sunriseBegin → 1 at sunsetEnd
        // Peak the arc BELOW the zenith. A sun that reaches an exact 90° at noon (sunDir.Z = 1) leaves every
        // VERTICAL surface — tree trunks, walls, side-facing leaf cards — at N·L ≈ 0 under the shader's
        // saturate(N·L) term, so they collapse to dim ambient and read as too dark, while every up-facing
        // surface is lit identically (flat, no midday shading). Real climates never put the sun directly
        // overhead at this worldspace's latitude — the engine derives the apex from climate azimuth/elevation —
        // so cap the arc's apex so a directional component survives at noon: trunks stay lit and the canopy
        // keeps directional shading. (Future refinement: read the true per-climate sun-path elevation.)
        var elevation = MathF.Sin(t01 * MathF.PI) * PeakSunElevation; // 0 → peak (solar noon) → 0
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

    // Blends the NAM0 time bands across the 24h clock, grounded in Sky::FillColorBlend: the colors
    // cross-fade only WITHIN the sunrise / sunset windows, pivoting at each window's midpoint. A band reads:
    //   night→midnight→night  outside the daylight span, pivoting at solar midnight (FNV's Midnight slot);
    //   night→sunrise  over sunriseBegin..sunriseMid, sunrise→day over sunriseMid..sunriseEnd;
    //   day→highNoon→day  over sunriseEnd..sunsetBegin, pivoting at solar noon (FNV's High Noon slot) —
    //     this is the engine's "extra daytime slot pivoting at a stored solar-noon time" previously
    //     simplified to solid Day;
    //   day→sunset over sunsetBegin..sunsetMid, sunset→night over sunsetMid..sunsetEnd.
    // FNV authors the two peak slots per fopdoc; an all-zero peak means "unused" and falls back to
    // Day/Night, which also keeps FO3 (4-band NAM0) and the placeholder palette on the old solid segments.
    // Every segment denominator is a window half-width, provably > 0 after NormalizeWindows (the peak
    // pivots are clamped into their spans; degenerate spans fall back to the solid segment).
    private static Vector3 SampleBand(WeatherColor c, float hour, float srB, float srE, float ssB, float ssE)
    {
        var day = ToVec(c.Day);
        var night = ToVec(c.Night);
        Vector3? highNoon = IsAuthored(c.HighNoon) ? ToVec(c.HighNoon) : null;
        Vector3? midnight = IsAuthored(c.Midnight) ? ToVec(c.Midnight) : null;
        return SampleBandV(night, ToVec(c.Sunrise), day, ToVec(c.Sunset),
            hour, srB, srE, ssB, ssE, highNoon, midnight);
    }

    // fopdoc FNV WTHR: a (0,0,0) High Noon / Midnight color is authored-empty — the engine uses Day/Night.
    private static bool IsAuthored(WeatherRgba c) => c.R != 0 || c.G != 0 || c.B != 0;

    // Vector3 form of the time-band blend, shared by the NAM0 path (above) and the placeholder palette.
    // highNoon/midnight are the optional FNV peak slots; null keeps the segment solid (4-band behavior).
    private static Vector3 SampleBandV(Vector3 night, Vector3 sunrise, Vector3 day, Vector3 sunset,
        float hour, float srB, float srE, float ssB, float ssE,
        Vector3? highNoon = null, Vector3? midnight = null)
    {
        var srMid = (srB + srE) * 0.5f;
        var ssMid = (ssB + ssE) * 0.5f;
        var solarNoon = (srB + ssE) * 0.5f; // daylight-span midpoint (the engine stores its own noon time)

        if (hour < srB || hour >= ssE)
        {
            // Night span [ssE .. srB+24], pivoting at solar midnight: Night → Midnight → Night.
            var span = (srB + 24f) - ssE;
            if (midnight is not { } mid || span < 0.2f)
            {
                return night; // no authored Midnight (or degenerate span): solid night, the old behavior
            }

            var h = hour < srB ? hour + 24f : hour; // unwrap onto [ssE, srB+24]
            var solarMidnight = Math.Clamp(solarNoon + 12f, ssE + 0.05f, srB + 24f - 0.05f);
            return h < solarMidnight
                ? Vector3.Lerp(night, mid, (h - ssE) / (solarMidnight - ssE))
                : Vector3.Lerp(mid, night, (h - solarMidnight) / (srB + 24f - solarMidnight));
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
            // Day span, pivoting at solar noon: Day → HighNoon → Day.
            if (highNoon is not { } hn || ssB - srE < 0.2f)
            {
                return day; // no authored High Noon (or degenerate span): solid day, the old behavior
            }

            var noon = Math.Clamp(solarNoon, srE + 0.05f, ssB - 0.05f);
            return hour < noon
                ? Vector3.Lerp(day, hn, (hour - srE) / (noon - srE))
                : Vector3.Lerp(hn, day, (hour - noon) / (ssB - noon));
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

    // RGBA twin of SampleBand — identical windowed blend (incl. the FNV High Noon / Midnight peak slots),
    // but keeps the alpha channel (cloud opacity). A peak is "authored" if ANY of RGBA is non-zero (alpha
    // is opacity here, so an authored invisible-at-midnight layer is meaningful).
    private static Vector4 SampleBand4(WeatherColor c, float hour, float srB, float srE, float ssB, float ssE)
    {
        var srMid = (srB + srE) * 0.5f;
        var ssMid = (ssB + ssE) * 0.5f;
        var solarNoon = (srB + ssE) * 0.5f;
        var night = ToVec4(c.Night);
        var sunrise = ToVec4(c.Sunrise);
        var day = ToVec4(c.Day);
        var sunset = ToVec4(c.Sunset);

        if (hour < srB || hour >= ssE)
        {
            var span = (srB + 24f) - ssE;
            var m = c.Midnight;
            if ((m.R | m.G | m.B | m.A) == 0 || span < 0.2f) return night;
            var mid4 = ToVec4(m);
            var h = hour < srB ? hour + 24f : hour;
            var solarMidnight = Math.Clamp(solarNoon + 12f, ssE + 0.05f, srB + 24f - 0.05f);
            return h < solarMidnight
                ? Vector4.Lerp(night, mid4, (h - ssE) / (solarMidnight - ssE))
                : Vector4.Lerp(mid4, night, (h - solarMidnight) / (srB + 24f - solarMidnight));
        }

        if (hour < srMid) return Vector4.Lerp(night, sunrise, (hour - srB) / (srMid - srB));
        if (hour < srE) return Vector4.Lerp(sunrise, day, (hour - srMid) / (srE - srMid));
        if (hour < ssB)
        {
            var hn = c.HighNoon;
            if ((hn.R | hn.G | hn.B | hn.A) == 0 || ssB - srE < 0.2f) return day;
            var hn4 = ToVec4(hn);
            var noon = Math.Clamp(solarNoon, srE + 0.05f, ssB - 0.05f);
            return hour < noon
                ? Vector4.Lerp(day, hn4, (hour - srE) / (noon - srE))
                : Vector4.Lerp(hn4, day, (hour - noon) / (ssB - noon));
        }

        if (hour < ssMid) return Vector4.Lerp(day, sunset, (hour - ssB) / (ssMid - ssB));
        return Vector4.Lerp(sunset, night, (hour - ssMid) / (ssE - ssMid));
    }

    private static Vector4 ToVec4(WeatherRgba c) => new(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);

    /// <summary>
    ///     Samples a cloud layer's JNAM per-layer OPACITY for the given game hour, using the SAME windowed
    ///     band blend as the sky/cloud colors (Sky::FillColorBlend). Returns the layer's authored opacity in
    ///     [0, 1] — the engine's per-draw cloud-layer alpha (so a weather hides a layer with 0 and thins
    ///     others with fractions). Public so the sky-geometry renderer can apply each cloud layer's real
    ///     opacity instead of a flat constant.
    /// </summary>
    public static float SampleCloudAlpha(WeatherCloudAlpha a, float gameHour, ClimateTiming? timing)
    {
        var (srB, srE, ssB, ssE) = NormalizeWindows(timing ?? ClimateTiming.Default);
        var hour = WrapHour(gameHour);
        var srMid = (srB + srE) * 0.5f;
        var ssMid = (ssB + ssE) * 0.5f;
        if (hour < srB || hour >= ssE) return a.Night;
        if (hour < srMid) return Lerp1(a.Night, a.Sunrise, (hour - srB) / (srMid - srB));
        if (hour < srE) return Lerp1(a.Sunrise, a.Day, (hour - srMid) / (srE - srMid));
        if (hour < ssB) return a.Day;
        if (hour < ssMid) return Lerp1(a.Day, a.Sunset, (hour - ssB) / (ssMid - ssB));
        return Lerp1(a.Sunset, a.Night, (hour - ssMid) / (ssE - ssMid));
    }

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
