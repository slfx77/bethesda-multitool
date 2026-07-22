using System.Numerics;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;

/// <summary>
///     Authored sun-quad geometry and path defaults for a retail engine family. The recovered engines
///     build the base and glare meshes directly at <c>±fSunBaseSize</c>/<c>±fSunGlareSize</c>, then place
///     their billboard nodes at the unnormalized triangle-path position. Moving that center to the
///     viewer's sky radius therefore requires the same uniform scale for the quad.
/// </summary>
public sealed record SkySunProfile
{
    private const float MinimumPathLength = 1e-4f;

    /// <summary>The existing calibrated viewer size retained where the executable path is unrecovered.</summary>
    public const float LegacyDiscHalfSizeFraction = 0.040f;

    /// <summary>The existing calibrated viewer glare size retained where the executable path is unrecovered.</summary>
    public const float LegacyGlareHalfSizeFraction = 0.160f;

    /// <summary>Whether this profile has a recovered direct-quad + triangle-path projection.</summary>
    public bool HasRecoveredTriangleProjection { get; init; }

    /// <summary>Retail default for the <c>fSunBaseSize:Weather</c> INI setting, in engine units.</summary>
    public float DefaultDiscHalfExtent { get; init; }

    /// <summary>Retail default for the <c>fSunGlareSize:Weather</c> INI setting, in engine units.</summary>
    public float DefaultGlareHalfExtent { get; init; }

    /// <summary>Fallback <c>fSunXExtreme</c> GMST when the loaded plugin does not retain it.</summary>
    public float DefaultSunXExtreme { get; init; }

    /// <summary>Fallback <c>fSunYExtreme</c> GMST when the loaded plugin does not retain it.</summary>
    public float DefaultSunYExtreme { get; init; }

    /// <summary>Fallback <c>fSunAlphaTransTime</c> GMST used to pad both ends of the day leg.</summary>
    public float DefaultAlphaTransitionHours { get; init; } = 2f;

    /// <summary>
    ///     TES4 (Oblivion) <c>Sun::Update</c> pads the day leg around the raw climate sunrise BEGIN and
    ///     sunset END hours, not the sunrise/sunset midpoints the later engines use — recovered from the
    ///     Oblivion Remastered symbolized decompile (the remaster ships the original engine logic;
    ///     <c>docs/research/tes4_celestial_pipeline.md</c>).
    /// </summary>
    public bool TriangleWindowUsesBeginEnd { get; init; }

    /// <summary>
    ///     TES4 sun visibility ramps across the climate HALF-WINDOWS themselves (fade out from sunset
    ///     begin to the sunset midpoint, fade in from the sunrise midpoint to sunrise end), with no
    ///     <c>fSunAlphaTransTime</c> involvement — recovered from the retail Oblivion.exe sun-visibility
    ///     decompile. Later engines center a fixed-width transition on the midpoints instead.
    /// </summary>
    public bool VisibilityUsesClimateHalfWindows { get; init; }

    /// <summary>
    ///     Resolves the maximum (unoccluded) base and glare quad half-extents at the viewer's sky radius.
    ///     GMST overrides remain plugin-aware; optional INI overrides leave a future settings reader able
    ///     to reproduce a user's runtime configuration without changing this contract.
    /// </summary>
    public SunBillboardHalfSizes ResolveBillboardHalfSizes(
        float viewerRadius,
        float gameHour,
        AtmosphereState.ClimateTiming climate,
        float? sunXExtreme = null,
        float? sunYExtreme = null,
        float? discHalfExtent = null,
        float? glareHalfExtent = null,
        float? alphaTransitionHours = null)
        => ResolveBillboardProjection(
            viewerRadius,
            gameHour,
            climate,
            sunXExtreme,
            sunYExtreme,
            discHalfExtent,
            glareHalfExtent,
            alphaTransitionHours).HalfSizes;

    /// <summary>
    ///     Resolves the raw engine billboard direction and the uniformly rescaled authored quad. The
    ///     directional-light shadow floor is deliberately not part of this value: Fallout 4 writes the
    ///     unfloored <c>SunPos</c> to both billboard nodes, then modifies a normalized copy for its light.
    /// </summary>
    public SunBillboardProjection ResolveBillboardProjection(
        float viewerRadius,
        float gameHour,
        AtmosphereState.ClimateTiming climate,
        float? sunXExtreme = null,
        float? sunYExtreme = null,
        float? discHalfExtent = null,
        float? glareHalfExtent = null,
        float? alphaTransitionHours = null)
    {
        if (!float.IsFinite(viewerRadius) || viewerRadius <= 0f)
        {
            return default;
        }

        if (!HasRecoveredTriangleProjection)
        {
            return new SunBillboardProjection(
                Vector3.Zero,
                0f,
                new SunBillboardHalfSizes(
                    viewerRadius * LegacyDiscHalfSizeFraction,
                    viewerRadius * LegacyGlareHalfSizeFraction));
        }

        var position = ResolveTrianglePosition(
            gameHour,
            climate,
            sunXExtreme,
            sunYExtreme,
            alphaTransitionHours);
        var distance = position.Length();
        if (!float.IsFinite(distance) || distance <= MinimumPathLength)
        {
            return new SunBillboardProjection(
                Vector3.Zero,
                distance,
                new SunBillboardHalfSizes(
                    viewerRadius * LegacyDiscHalfSizeFraction,
                    viewerRadius * LegacyGlareHalfSizeFraction));
        }

        // SettingT does not clamp these values before Sun::Initialize builds the vertices. Preserve
        // every finite override, including zero, and reject only non-finite values that cannot form a
        // usable renderer transform.
        var disc = FiniteOr(discHalfExtent, DefaultDiscHalfExtent);
        var glare = FiniteOr(glareHalfExtent, DefaultGlareHalfExtent);
        var scale = viewerRadius / distance;
        return new SunBillboardProjection(
            position / distance,
            distance,
            new SunBillboardHalfSizes(disc * scale, glare * scale));
    }

    /// <summary>
    ///     Evaluates the alpha written by recovered <c>Sun::Update</c>: the climate midpoints are
    ///     expanded by half of <c>fSunAlphaTransTime</c>, independently of the authored color windows.
    /// </summary>
    public float ResolveVisibility(
        float gameHour,
        AtmosphereState.ClimateTiming climate,
        float? alphaTransitionHours = null)
    {
        if (VisibilityUsesClimateHalfWindows)
        {
            return ComputeTes4Visibility(
                gameHour,
                climate.SunriseBeginHour,
                climate.SunriseEndHour,
                climate.SunsetBeginHour,
                climate.SunsetEndHour);
        }

        var transition = FiniteOr(alphaTransitionHours, DefaultAlphaTransitionHours);
        return ComputeVisibility(
            gameHour,
            climate.SunriseBeginHour,
            climate.SunriseEndHour,
            climate.SunsetBeginHour,
            climate.SunsetEndHour,
            transition);
    }

    /// <summary>Evaluates the raw local translation written by the recovered <c>Sun::Update</c>.</summary>
    public Vector3 ResolveTrianglePosition(
        float gameHour,
        AtmosphereState.ClimateTiming climate,
        float? sunXExtreme = null,
        float? sunYExtreme = null,
        float? alphaTransitionHours = null)
    {
        var xExtreme = FiniteOr(sunXExtreme, DefaultSunXExtreme);
        var yExtreme = FiniteOr(sunYExtreme, DefaultSunYExtreme);
        // Zero is an authored, meaningful value: it removes the half-transition padding and places
        // the path edges exactly at the climate sunrise/sunset midpoints. The recovered code does not
        // clamp this SettingT, so retain any finite override.
        var transition = FiniteOr(alphaTransitionHours, DefaultAlphaTransitionHours);
        return ComputeTrianglePosition(
            gameHour,
            climate.SunriseBeginHour,
            climate.SunriseEndHour,
            climate.SunsetBeginHour,
            climate.SunsetEndHour,
            xExtreme,
            yExtreme,
            transition,
            TriangleWindowUsesBeginEnd);
    }

    /// <summary>
    ///     Pure recovered triangle wave. Day: +X at the padded sunrise edge to −X at the padded sunset
    ///     edge. Night: −X back to +X across the wrapped complement. Z is the authored triangular arch.
    /// </summary>
    public static Vector3 ComputeTrianglePosition(
        float gameHour,
        float sunriseBegin,
        float sunriseEnd,
        float sunsetBegin,
        float sunsetEnd,
        float sunXExtreme,
        float sunYExtreme,
        float alphaTransitionHours = 2f,
        bool windowUsesBeginEnd = false)
    {
        var hour = WrapHour(gameHour);
        // TES4 Sun::Update pads the raw climate sunrise-begin/sunset-end; FNV and the Creation
        // engines pad the window midpoints. Same triangle shape either way.
        var dayStart = windowUsesBeginEnd
            ? sunriseBegin - (alphaTransitionHours * 0.5f)
            : ((sunriseBegin + sunriseEnd) * 0.5f) - (alphaTransitionHours * 0.5f);
        var dayEnd = windowUsesBeginEnd
            ? sunsetEnd + (alphaTransitionHours * 0.5f)
            : ((sunsetBegin + sunsetEnd) * 0.5f) + (alphaTransitionHours * 0.5f);
        var daySpan = dayEnd - dayStart;
        if (!float.IsFinite(daySpan) || daySpan <= MinimumPathLength || daySpan >= 24f)
        {
            return new Vector3(0f, sunYExtreme, MathF.Abs(sunXExtreme));
        }

        float x;
        if (hour >= dayStart && hour <= dayEnd)
        {
            x = 1f - ((hour - dayStart) / daySpan) * 2f;
        }
        else
        {
            var sinceDusk = hour > dayEnd ? hour - dayEnd : (hour + 24f) - dayEnd;
            x = (sinceDusk / (24f - daySpan)) * 2f - 1f;
        }

        var px = x * sunXExtreme;
        return new Vector3(px, sunYExtreme, MathF.Abs(sunXExtreme) - MathF.Abs(px));
    }

    /// <summary>Pure recovered daylight-alpha curve, independent of production atmosphere sampling.</summary>
    public static float ComputeVisibility(
        float gameHour,
        float sunriseBegin,
        float sunriseEnd,
        float sunsetBegin,
        float sunsetEnd,
        float alphaTransitionHours = 2f)
    {
        var hour = WrapHour(gameHour);
        var sunriseMid = (sunriseBegin + sunriseEnd) * 0.5f;
        var sunsetMid = (sunsetBegin + sunsetEnd) * 0.5f;
        var halfTransition = alphaTransitionHours * 0.5f;
        var fadeInBegin = sunriseMid - halfTransition;
        var fadeInEnd = sunriseMid + halfTransition;
        var fadeOutBegin = sunsetMid - halfTransition;
        var fadeOutEnd = sunsetMid + halfTransition;

        if (!float.IsFinite(fadeInBegin) || !float.IsFinite(fadeInEnd) ||
            !float.IsFinite(fadeOutBegin) || !float.IsFinite(fadeOutEnd))
        {
            return 0f;
        }

        // A zero transition is authored and produces a hard midpoint-to-midpoint day leg. Negative
        // transition widths cannot form the recovered ordered windows; retain a deterministic hard leg.
        if (alphaTransitionHours <= MinimumPathLength)
        {
            return hour >= sunriseMid && hour <= sunsetMid ? 1f : 0f;
        }

        if (hour < fadeInBegin || hour > fadeOutEnd)
        {
            return 0f;
        }

        if (hour < fadeInEnd)
        {
            return Math.Clamp((hour - fadeInBegin) / (fadeInEnd - fadeInBegin), 0f, 1f);
        }

        if (hour > fadeOutBegin)
        {
            return Math.Clamp(1f - ((hour - fadeOutBegin) / (fadeOutEnd - fadeOutBegin)), 0f, 1f);
        }

        return 1f;
    }

    /// <summary>
    ///     TES4-exact sun visibility (retail Oblivion.exe decompile; confirmed by the symbolized
    ///     remaster): fade OUT across the first half of the sunset window (begin → midpoint), solid
    ///     night until the sunrise midpoint, fade IN across the second half of the sunrise window
    ///     (midpoint → end), and full day elsewhere. No <c>fSunAlphaTransTime</c> term.
    /// </summary>
    public static float ComputeTes4Visibility(
        float gameHour,
        float sunriseBegin,
        float sunriseEnd,
        float sunsetBegin,
        float sunsetEnd)
    {
        var hour = WrapHour(gameHour);
        var sunriseMid = (sunriseBegin + sunriseEnd) * 0.5f;
        var sunsetMid = (sunsetBegin + sunsetEnd) * 0.5f;
        if (!float.IsFinite(sunriseMid) || !float.IsFinite(sunsetMid))
        {
            return 0f;
        }

        if (hour > sunsetBegin && hour < sunsetMid && sunsetMid > sunsetBegin)
        {
            return Math.Clamp((sunsetMid - hour) / (sunsetMid - sunsetBegin), 0f, 1f);
        }

        if (hour >= sunsetMid || hour <= sunriseMid)
        {
            return 0f;
        }

        if (hour < sunriseEnd && sunriseEnd > sunriseMid)
        {
            return Math.Clamp((hour - sunriseMid) / (sunriseEnd - sunriseMid), 0f, 1f);
        }

        return 1f;
    }

    private static float FiniteOr(float? candidate, float fallback) =>
        candidate is { } value && float.IsFinite(value) ? value : fallback;

    private static float WrapHour(float hour)
    {
        hour %= 24f;
        return hour < 0f ? hour + 24f : hour;
    }

    private static readonly SkySunProfile Legacy = new();

    // Fallout 3 / New Vegas retail PC defaults are authored in Fallout_default.ini [Weather]. Their
    // shared symbol-backed implementation builds the quads directly from these values. The fallback
    // path GMSTs are FNV's shipped ESM values; a loaded FO3/FNV GMST overrides them at the call site.
    private static readonly SkySunProfile Fallout = new()
    {
        HasRecoveredTriangleProjection = true,
        DefaultDiscHalfExtent = 750f,
        DefaultGlareHalfExtent = 800f,
        DefaultSunXExtreme = 800f,
        DefaultSunYExtreme = -100f,
        DefaultAlphaTransitionHours = 2f,
    };

    // TESV.exe 1.9.32 and Fallout4.exe independently initialize these SettingT values and create the
    // same direct ±extent quads. Their Sun::Update implementations write the raw triangle position to
    // both billboard nodes. Plugin GMSTs still take precedence over these executable defaults.
    private static readonly SkySunProfile Creation = new()
    {
        HasRecoveredTriangleProjection = true,
        DefaultDiscHalfExtent = 425f,
        DefaultGlareHalfExtent = 600f,
        DefaultSunXExtreme = 400f,
        DefaultSunYExtreme = 25f,
        DefaultAlphaTransitionHours = 2f,
    };

    // TES4 (Oblivion): recovered 2026-07-20 from the symbolized Oblivion Remastered Sun::Update
    // (the remaster runs the original engine logic) + retail Oblivion.exe Setting defaults
    // (docs/research/tes4_celestial_pipeline.md). Retail authored fSunXExtreme=−400 with the stored
    // X negated in Sun::Update — folded here to the positive convention the shared triangle uses
    // (dawn = +X/east). fSunYExtreme=25, fSunAlphaTransTime=2, fSunBaseSize=250, fSunGlareSize=350.
    // Noon apex atan(400/25) ≈ 86° — near-zenith, which the previous 50° analytic stand-in missed
    // (the reported "sun never appears overhead").
    private static readonly SkySunProfile Tes4 = new()
    {
        HasRecoveredTriangleProjection = true,
        DefaultDiscHalfExtent = 250f,
        DefaultGlareHalfExtent = 350f,
        DefaultSunXExtreme = 400f,
        DefaultSunYExtreme = 25f,
        DefaultAlphaTransitionHours = 2f,
        TriangleWindowUsesBeginEnd = true,
        VisibilityUsesClimateHalfWindows = true,
    };

    /// <summary>
    ///     Returns the objective per-game profile. FO76 retains the previous calibrated fractions
    ///     until its complete path/projection chain has an independent binary oracle.
    /// </summary>
    public static SkySunProfile ForGame(BethesdaGame game) => game switch
    {
        BethesdaGame.Oblivion => Tes4,
        BethesdaGame.Fallout3 => Fallout,
        BethesdaGame.FalloutNewVegas => Fallout,
        BethesdaGame.Skyrim => Creation,
        BethesdaGame.Fallout4 => Creation,
        _ => Legacy,
    };
}

/// <summary>Viewer-space half-extents for the sun base and its maximum glare quad.</summary>
public readonly record struct SunBillboardHalfSizes(float Disc, float Glare);

/// <summary>
///     Recovered raw billboard direction plus authored quad extents at the viewer replacement radius.
/// </summary>
public readonly record struct SunBillboardProjection(
    Vector3 Direction,
    float RawPathLength,
    SunBillboardHalfSizes HalfSizes);
