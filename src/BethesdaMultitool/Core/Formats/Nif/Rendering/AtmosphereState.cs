using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

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
///             this 4-band NAM0 model does not carry. Skyrim's exact ±0.5h
///             <c>fDaytimeColorExtension</c> is applied by <see cref="Resolve" />; see
///             <see cref="SampleBand" />;</item>
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
        float SunsetEndHour,
        int MoonPhaseDays = 0)
    {
        /// <summary>Fallout's default day: sunrise ~06:00, sunset ~18:00 (no climate moon phase length).</summary>
        public static ClimateTiming Default => new(5.5f, 6.5f, 18.0f, 19.0f);

        /// <summary>Build timing from a CLMT TNAM block. Byte→hours = value × 1/6 (10-minute units) —
        /// CONFIRMED by the decompile: <c>Sky::GetSunriseBegin</c> reads <c>climate+0x60</c> and scales by
        /// <c>0.16666667</c>. The phase-length byte masks its low 6 bits (TESClimate::GetMoonPhaseDays —
        /// the top 2 bits are the Masser/Secunda enable flags); 0 = climate carries no phase length and
        /// the moon profile's fallback applies. Returns <see cref="Default" /> for a null block.</summary>
        public static ClimateTiming FromClimateData(ClimateTimingData? d) =>
            d is null
                ? Default
                : new(d.SunriseBegin / 6f, d.SunriseEnd / 6f, d.SunsetBegin / 6f, d.SunsetEnd / 6f,
                    d.MoonPhaseLength & 0x3F);
    }

    /// <summary>The resolved per-frame atmosphere the GPU constant buffer mirrors.</summary>
    public readonly record struct Resolved(
        Vector3 SunWorldDirection,  // unit vector FROM the surface TOWARD the sun (+Z up)
        Vector3 SunColor,           // 0..1 RGB; zero when lighting is disabled or the sun is down
        float SunIntensity,         // 0 at/below the horizon → 1 across the day (engine daylight fraction)
        Vector3 AmbientColor,
        Vector3 SkyTopColor,
        Vector3 SkyHorizonColor,
        Vector3 FogColor,           // near-fog color (legacy name retained for callers)
        Vector3 FogFarColor,
        float FogNear,
        float FogFar,
        float FogPower,             // distance-fog exponent (1 = linear), from WTHR FNAM day/night power
        float FogMaxOpacity);       // max powered fog amount; Skyrim FNAM day/night max, else 1

    /// <summary>
    ///     Interior-cell atmosphere from CELL XCLL lighting with LGTM lighting-template fallback —
    ///     engine model: interiors are lit by their AUTHORED ambient + directional + fog, with NO
    ///     time-of-day dependence (the previous exterior-sun fallback drove interiors to black at
    ///     night). Field precedence per value: cell XCLL when present, else the referenced lighting
    ///     template's DATA, else a neutral default. <paramref name="inheritanceFlags" /> bits force
    ///     specific fields to come from the TEMPLATE even when XCLL is present (Skyrim-style LTMP
    ///     inherit bits; FO3/FNV cells usually carry a full XCLL and no flags).
    ///     Dictionary keys are the shared XCLL/LGTM schema names (SubrecordCellAndMiscSchemas):
    ///     AmbientColor / DirectionalColor / FogColor (packed R|G&lt;&lt;8|B&lt;&lt;16),
    ///     FogNear / FogFar / FogPow (floats), DirectionalRotationXY / DirectionalRotationZ
    ///     (degrees, ints), DirectionalFade (float).
    /// </summary>
    public static Resolved ResolveInterior(
        IReadOnlyDictionary<string, object?>? cellLighting,
        IReadOnlyDictionary<string, object?>? templateLighting,
        uint inheritanceFlags = 0,
        bool lightingEnabled = true)
    {
        // Inherit bits (xEdit LTMP 'Inherit' flags): a set bit takes the field from the TEMPLATE.
        const uint inheritAmbient = 1u << 0;
        const uint inheritDirectional = 1u << 1;
        const uint inheritFogColor = 1u << 2;
        const uint inheritFogNear = 1u << 3;
        const uint inheritFogFar = 1u << 4;
        const uint inheritRotation = 1u << 5;
        const uint inheritFade = 1u << 6;
        const uint inheritFogPower = 1u << 8;

        object? Pick(string key, uint inheritBit)
        {
            var fromTemplate = (inheritanceFlags & inheritBit) != 0;
            var primary = fromTemplate ? templateLighting : cellLighting;
            var secondary = fromTemplate ? cellLighting : templateLighting;
            if (primary is not null && primary.TryGetValue(key, out var v) && v is not null) return v;
            if (secondary is not null && secondary.TryGetValue(key, out var w) && w is not null) return w;
            return null;
        }

        static Vector3? AsColor(object? v) => v switch
        {
            uint packed => new Vector3(packed & 0xFF, (packed >> 8) & 0xFF, (packed >> 16) & 0xFF) / 255f,
            int packed => new Vector3((uint)packed & 0xFF, ((uint)packed >> 8) & 0xFF, ((uint)packed >> 16) & 0xFF) / 255f,
            _ => null,
        };

        static float? AsFloat(object? v) => v switch
        {
            float f => f,
            double d => (float)d,
            int i => i,
            uint u => u,
            _ => null,
        };

        // Neutral defaults for a cell with neither XCLL nor a template (rare): readable gray ambient,
        // soft white top-down directional, fog effectively off.
        var ambient = AsColor(Pick("AmbientColor", inheritAmbient)) ?? new Vector3(0.33f, 0.33f, 0.33f);
        var directional = AsColor(Pick("DirectionalColor", inheritDirectional)) ?? new Vector3(0.45f, 0.45f, 0.45f);
        var fogColor = AsColor(Pick("FogColor", inheritFogColor)) ?? new Vector3(0.05f, 0.05f, 0.05f);
        var fogNear = AsFloat(Pick("FogNear", inheritFogNear)) ?? DefaultFogNear;
        var fogFar = AsFloat(Pick("FogFar", inheritFogFar)) ?? DefaultFogFar;
        // Key-name trap: the XCLL schema names it FogPow, the LGTM DATA schema FogPower — check both.
        var fogPower = AsFloat(Pick("FogPow", inheritFogPower))
                       ?? AsFloat(Pick("FogPower", inheritFogPower)) ?? 1f;
        var fade = AsFloat(Pick("DirectionalFade", inheritFade)) ?? 1f;
        var rotXy = AsFloat(Pick("DirectionalRotationXY", inheritRotation)) ?? 0f;
        var rotZ = AsFloat(Pick("DirectionalRotationZ", inheritRotation)) ?? 0f;

        // Directional-light direction from the authored XY/Z rotations (degrees). Structural stand-in
        // pending an engine decompile of the interior directional setup: yaw = Z, pitch = XY below
        // horizontal, defaulting to a steep top-down light (rot 0/0 → straight down) — the engine's
        // interior directional is a downlight unless authored otherwise. Returned as a TO-LIGHT vector
        // to match the exterior sun convention.
        var pitchRad = (90f - Math.Clamp(rotXy, -89f, 89f)) * (MathF.PI / 180f);
        var yawRad = rotZ * (MathF.PI / 180f);
        var dir = new Vector3(
            MathF.Cos(pitchRad) * MathF.Cos(yawRad),
            MathF.Cos(pitchRad) * MathF.Sin(yawRad),
            MathF.Sin(pitchRad));
        dir = dir.LengthSquared() > 1e-6f ? Vector3.Normalize(dir) : Vector3.UnitZ;

        if (fogFar <= fogNear)
        {
            fogFar = fogNear + 1f;
        }

        var sunColor = lightingEnabled ? directional * Math.Clamp(fade, 0f, 4f) : Vector3.Zero;
        return new Resolved(
            dir,
            sunColor,
            lightingEnabled ? 1f : 0f,
            ambient,
            fogColor,   // interiors have no sky: the fog color doubles as the void/background tint
            fogColor,
            fogColor,
            fogColor,
            fogNear,
            fogFar,
            MathF.Max(fogPower, 0.01f),
            1f);
    }

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

    // How far below the horizon the sun keeps feeding the horizon glow (civil twilight ends at ~6° solar
    // depression). Converted to a time window via the arc's horizon-crossing descent rate, this gives a
    // ~half-hour afterglow for a 14-hour day instead of the glow vanishing the instant the clock passes
    // sunset-end.
    private const float CivilTwilightDepthRadians = 6f * (MathF.PI / 180f);

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
        bool lightingEnabled = true,
        Vector3? moonlightDirection = null,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        var hour = WrapHour(gameHour);
        var (srB, srE, ssB, ssE) = NormalizeWindows(climate ?? ClimateTiming.Default);

        // Skyrim widens weather color/fog interpolation by fDaytimeColorExtension=.5h. This is
        // independent of the sun's real horizon/daylight window: colors begin fading in before sunrise
        // and finish after sunset, while directional intensity continues to use the unextended span.
        var weatherSrB = game == BethesdaGame.Skyrim ? MathF.Max(0f, srB - 0.5f) : srB;
        var weatherSsE = game == BethesdaGame.Skyrim ? MathF.Min(23.99f, ssE + 0.5f) : ssE;

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
        Vector3 sunColorBase, ambient, skyTop, skyHorizon, fogColor, fogFarColor;
        if (weather is { Colors.Count: > 0 } wc)
        {
            sunColorBase = BandOr(wc, WeatherColorType.Sunlight, hour, weatherSrB, srE, ssB, weatherSsE, DaySun);
            ambient = BandOr(wc, WeatherColorType.Ambient, hour, weatherSrB, srE, ssB, weatherSsE, Vector3.Lerp(NightAmbient, DayAmbient, day));
            skyTop = BandOr(wc, WeatherColorType.SkyUpper, hour, weatherSrB, srE, ssB, weatherSsE, Vector3.Lerp(NightTint, DaySkyTop, day));
            var skyLowerBand = BandOr(wc, WeatherColorType.SkyLower, hour, weatherSrB, srE, ssB, weatherSsE, Vector3.Lerp(NightTint, DaySkyHorizon, day));
            // Horizon glow: FNV authors the warm sunrise/sunset horizon in the SEPARATE NAM0 "Horizon"
            // band (index 8) — e.g. NVWastelandClear Sunset Horizon=(219,192,174) warm vs its blue
            // SkyUpper/SkyLower — which the dome gradient (SkyUpper↔SkyLower) otherwise drops, leaving the
            // sky blue at sunset ("no sunset lighting"). Fold the Horizon band into the dome's horizon,
            // gated by sun elevation so it only appears when the sun is low (zero at noon → the daytime
            // sky is unchanged), reproducing the warm low-sun horizon glow.
            var horizonBand = BandOr(wc, WeatherColorType.Horizon, hour, weatherSrB, srE, ssB, weatherSsE, skyLowerBand);
            // Glow tracks the sun's PROXIMITY to the horizon (|Z|), peaking at dawn/dusk and fading to
            // zero both at noon and in deep night. The old `1 - Z*1.5` gate saturated for the whole night
            // (sun far below ⇒ Z ≈ -1), folding the Horizon band into the dome at full strength after
            // dark — harmless while the night fold was the dark Night column, but the 6-band blend also
            // reaches the Midnight column, which weathers author as junk for this category (e.g.
            // NVWastelandClear Horizon Midnight = (43,200,213) teal) since the engine only shows the
            // horizon glow around the low sun.
            var horizonGlow = HorizonGlow(hour, srB, ssE, sunDir.Z);
            skyHorizon = Vector3.Lerp(skyLowerBand, horizonBand, horizonGlow * HorizonGlowStrength);
            fogColor = BandOr(wc, WeatherColorType.Fog, hour, weatherSrB, srE, ssB, weatherSsE, Vector3.Lerp(NightTint, DayFog, day));
            // Skyrim's NiFogProperty receives two distinct authored colors. Category 12 is far fog;
            // category 2 is unrelated (and all-zero in SkyrimClear). Older ten-category weather falls
            // back to near=far, preserving its single-color fog path.
            fogFarColor = BandOr(wc, WeatherColorType.FogFar, hour, weatherSrB, srE, ssB, weatherSsE, fogColor);
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
            fogFarColor = fogColor;
        }

        // Skyrim+/FO4 light exteriors from the weather's DALC directional-ambient cube, not the
        // NAM0 "Ambient" row — FO4 authors that row near-black (CommonwealthClear Night = (2,2,2)
        // vs its DALC night mean ≈ (18,26,34)), which rendered FO4 nights pitch black. The parse
        // reduces the six-direction cube to its per-band mean; when present it wins over the NAM0
        // row / placeholder. FO3/FNV weathers carry no DALC, so their ambient is unchanged.
        if (weather?.DirectionalAmbient is { } ambientCube)
        {
            ambient = SampleBand(ambientCube, hour, weatherSrB, srE, ssB, weatherSsE);
        }

        // Directional light color + direction, per engine family:
        //
        // FO4-family (weather carries DALC — the modern-weather marker): DECOMPILE-GROUNDED in FO4
        // Sun::Update (tools/GhidraProject/fo4_lighting_decompiled.txt) — the scene's directional light
        // follows ONE CONTINUOUS 24-hour path; there is NO sun→moon handover and no dusk snap:
        //   * horizontal x runs +1→−1 across the "day leg" (sunriseMid−1h .. sunsetMid+1h, the
        //     fSunAlphaTransTime=2h padding) and wraps −1→+1 across the night leg — continuous at
        //     both edges (the night light re-crosses the zenith ~1am, which reads as moonlight);
        //   * position = (x·fSunXExtreme, fSunYExtreme, |fSunXExtreme|−|x·fSunXExtreme|) — a
        //     triangle-wave arc, near-overhead at noon (defaults 400/25 → apex ≈ 86°);
        //   * the normalized z is FLOORED at fSunShadowMinAngle(30°)·DEG_TO_RAD — the light (and its
        //     shadows) never drops below ~31° elevation. The engine compares the unit-vector z against
        //     radians verbatim; reproduced exactly.
        //   * the color is the UNSCALED NAM0 Sunlight band — the night rows carry the authored
        //     moonlight blue (CommonwealthClear (53,70,87)); the engine never multiplies it by a
        //     daylight factor (day/night look = band authoring). The previous hard flip to the moon
        //     billboard's direction at day==0 was the round-11 stand-in — it caused the user-visible
        //     "lighting snaps to the moon" and is refuted by the decompile.
        //
        // FO3/FNV (no DALC): unchanged — Sky::UpdateColors modulates the Sunlight colour by the
        // daylight factor (atmosphere_decompiled.txt), so the directional fades out at night and the
        // ambient-only night stands (both grounded in the FNV S3 audit).
        Vector3 sunColor;
        var sunIntensity = lightingEnabled ? day : 0f;
        if (lightingEnabled && weather?.DirectionalAmbient is not null)
        {
            sunDir = Fo4SunPathDirection(hour, srB, srE, ssB, ssE);
            sunColor = sunColorBase;
        }
        else
        {
            // FNV/FO3: the engine's triangle-wave sun arc with the FNV GMST constants (noon apex ≈ 83°,
            // slight −Y/south offset) replaces the fitted 50° analytic arc. Other non-DALC games keep the
            // analytic arc until their constants are extracted.
            if (game is BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3)
            {
                sunDir = FnvSunPathDirection(hour, srB, srE, ssB, ssE);
            }

            sunColor = lightingEnabled ? sunColorBase * day : Vector3.Zero;
        }

        // Distance fog (grounded in Sky::UpdateFog): resolve WTHR FNAM day/night pairs for near, far,
        // power, and (when present) maximum opacity. Skyrim uses the same fDaytimeColorExtension=.5h
        // window as its colors; older families retain the existing daylight factor. A 2-float FNAM is
        // used verbatim; with none, scene-scale defaults stand in.
        var fogNear = DefaultFogNear;
        var fogFar = DefaultFogFar;
        var fogPower = 1f;
        var fogMaxOpacity = 1f;
        var weatherDay = game == BethesdaGame.Skyrim
            ? DaylightFraction(hour, weatherSrB, srE, ssB, weatherSsE)
            : day;
        if (weather is { FogDistances.Count: >= 4 } wf)
        {
            fogNear = Lerp1(wf.FogDistances[2], wf.FogDistances[0], weatherDay); // night → day
            fogFar = Lerp1(wf.FogDistances[3], wf.FogDistances[1], weatherDay);
            if (wf.FogDistances.Count >= 6)
            {
                fogPower = Lerp1(wf.FogDistances[5], wf.FogDistances[4], weatherDay);
            }

            if (wf.FogDistances.Count >= 8)
            {
                fogMaxOpacity = Lerp1(wf.FogDistances[7], wf.FogDistances[6], weatherDay);
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
        fogMaxOpacity = Math.Clamp(fogMaxOpacity, 0f, 1f);

        return new Resolved(sunDir, sunColor, sunIntensity, ambient, skyTop, skyHorizon,
            fogColor, fogFarColor, fogNear, fogFar, fogPower, fogMaxOpacity);
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
    /// <summary>
    ///     Horizon-glow gate: how strongly the warm NAM0 "Horizon" band folds into the dome's horizon.
    ///     Inside the daylight span this is the sun's proximity to the horizon (1 − |sin el|·1.5,
    ///     unchanged). Outside it, the old gate snapped to zero the instant the clock crossed
    ///     sunriseBegin/sunsetEnd — SunDirection returns (0,0,−1) there, so a capture at exactly
    ///     sunset-end showed no warm band at all while real twilight lingers. The glow now fades
    ///     linearly over the sun's first ~6° of descent below the horizon (civil twilight), converted
    ///     to hours via the arc's horizon-crossing rate (≈ half an hour for a 14-hour day), then stays
    ///     zero all night — which the Midnight junk-teal gating (see the call site) relies on.
    ///     Continuous at both window edges: |sin el| → 0 approaching the edge from daylight ⇒ glow → 1,
    ///     and hoursBelow → 0 approaching from night ⇒ glow → 1.
    /// </summary>
    internal static float HorizonGlow(float hour, float srB, float ssE, float sunDirZ)
    {
        if (hour > srB && hour < ssE)
        {
            return Math.Clamp(1f - (MathF.Abs(sunDirZ) * 1.5f), 0f, 1f);
        }

        // Hours past the nearest daylight edge, wrap-aware (past sunset going forward, before
        // sunrise going backward — deep night is far from both).
        var pastSunset = hour >= ssE ? hour - ssE : hour + 24f - ssE;
        var beforeSunrise = hour <= srB ? srB - hour : srB + 24f - hour;
        var hoursBelowHorizon = MathF.Min(pastSunset, beforeSunrise);

        // Solar descent rate at the horizon crossing: d/dt [sin(t01·π)·Peak] at t01 ∈ {0,1} is
        // π·Peak per unit t01, over a daylight span of (ssE − srB) hours.
        var descentRadiansPerHour = MathF.PI * PeakSunElevation / MathF.Max(ssE - srB, 0.1f);
        var twilightHours = CivilTwilightDepthRadians / descentRadiansPerHour;
        return Math.Clamp(1f - (hoursBelowHorizon / twilightHours), 0f, 1f);
    }

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

    // --- Engine triangle-wave directional-light path (Sun::Update) --------------------------------------
    // FO4 (fo4_lighting_decompiled.txt) and FNV (atmosphere_decompiled.txt:1855-1896) share the same
    // sun-path shape: position = (x·fSunXExtreme, fSunYExtreme, |fSunXExtreme| − |x·fSunXExtreme|), with
    // x sweeping +1→−1 across the padded day leg and wrapping overnight.
    // FO4 engine defaults read from the Fallout4.exe image (SettingT statics; see the audit notes):
    // fSunXExtreme=400, fSunYExtreme=25, fSunAlphaTransTime=2h, fSunShadowMinAngle=30(°),
    // fSunShadowScale=0. The FO4 unit z is floored at fSunShadowMinAngle·DEG_TO_RAD — the engine compares
    // the unit-vector component against radians verbatim (≈ sin θ for small angles), reproduced exactly.
    private const float Fo4SunXExtreme = 400f;
    private const float Fo4SunYExtreme = 25f;
    private const float Fo4SunAlphaTransTimeHours = 2f;
    private const float Fo4SunShadowMinAngleDegrees = 30f;

    // FNV GMSTs verified from FalloutNV.esm (2026-07-13): fSunXExtreme=800, fSunYExtreme=−100 → noon
    // apex atan(800/100) ≈ 83° with the sun slightly on the −Y (south) side — supersedes the fitted 50°
    // arc (that figure was a stand-in guess; the engine constants are authored). FO3 shares the engine
    // path; its GMSTs are unverified, so it rides the FNV values as a labeled stand-in. No z floor:
    // FNV has no shadow-min-angle clamp (FO4-only), and the night leg is unused (SunDirection's night
    // return keeps the (0,0,−1) convention the horizon-glow gating depends on).
    private const float FnvSunXExtreme = 800f;
    private const float FnvSunYExtreme = -100f;

    internal static Vector3 Fo4SunPathDirection(float hour, float srB, float srE, float ssB, float ssE)
    {
        var p = EngineSunTrianglePosition(hour, srB, srE, ssB, ssE, Fo4SunXExtreme, Fo4SunYExtreme);
        p = p.LengthSquared() > 1e-6f ? Vector3.Normalize(p) : Vector3.UnitZ;
        // Engine: z += fSunShadowScale·DEG_TO_RAD (default 0), then floor at fSunShadowMinAngle·DEG_TO_RAD.
        p.Z = MathF.Max(p.Z, Fo4SunShadowMinAngleDegrees * (MathF.PI / 180f));
        return Vector3.Normalize(p);
    }

    /// <summary>
    ///     FNV/FO3 daytime sun direction: the engine triangle wave with the FNV GMST constants. Only the
    ///     day leg is served (night keeps the analytic path's (0,0,−1) so the horizon-glow/midnight gates
    ///     behave); callers pass the same climate windows as <see cref="SunDirection" />.
    /// </summary>
    internal static Vector3 FnvSunPathDirection(float hour, float srB, float srE, float ssB, float ssE)
    {
        if (hour <= srB || hour >= ssE)
        {
            return new Vector3(0f, 0f, -1f); // night: below the horizon (directional colour fades to 0)
        }

        var p = EngineSunTrianglePosition(hour, srB, srE, ssB, ssE, FnvSunXExtreme, FnvSunYExtreme);
        return p.LengthSquared() > 1e-6f ? Vector3.Normalize(p) : Vector3.UnitZ;
    }

    private static Vector3 EngineSunTrianglePosition(
        float hour, float srB, float srE, float ssB, float ssE, float sunXExtreme, float sunYExtreme)
    {
        var dayStart = ((srB + srE) * 0.5f) - (Fo4SunAlphaTransTimeHours * 0.5f);
        var dayEnd = ((ssB + ssE) * 0.5f) + (Fo4SunAlphaTransTimeHours * 0.5f);
        float x;
        if (hour >= dayStart && hour <= dayEnd)
        {
            x = 1f - ((hour - dayStart) / (dayEnd - dayStart)) * 2f;      // +1 at dawn → −1 at dusk
        }
        else
        {
            var sinceDusk = hour > dayEnd ? hour - dayEnd : (hour + 24f) - dayEnd;
            x = (sinceDusk / (24f - (dayEnd - dayStart))) * 2f - 1f;      // −1 at dusk → +1 at dawn
        }

        var px = x * sunXExtreme;
        return new Vector3(px, sunYExtreme, MathF.Abs(sunXExtreme) - MathF.Abs(px));
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
