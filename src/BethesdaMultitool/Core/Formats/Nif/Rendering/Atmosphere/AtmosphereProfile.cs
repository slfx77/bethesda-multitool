using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>Which recovered sun-path implementation drives the scene sun direction.</summary>
public enum SunPathModel
{
    /// <summary>
    ///     Fitted 50° analytic arc — the stand-in for games whose path constants have not been
    ///     extracted from a binary yet (Morrowind, Oblivion, Unknown).
    /// </summary>
    AnalyticArc,

    /// <summary>FNV/FO3 triangle-wave arc with the FNV GMST constants (noon apex ≈ 83°, slight −Y offset).</summary>
    FnvTriangleWave,

    /// <summary>
    ///     TES4's original triangle-wave arc, recovered from the symbolized Oblivion Remastered
    ///     Sun::Update + retail Oblivion.exe Setting defaults: day leg padded around the raw climate
    ///     sunrise-begin/sunset-end (not midpoints), folded constants 400/25 → noon apex ≈ 86°.
    /// </summary>
    Tes4TriangleWave,

    /// <summary>Skyrim's triangle-wave variant of the same Sun::Update contract.</summary>
    SkyrimTriangleWave,

    /// <summary>FO4-family Sun::Update continuous 24-hour path (DALC-era; drives the night moonlight rows).</summary>
    Fo4Continuous
}

/// <summary>How the star-controller alpha is scheduled over the day.</summary>
public enum StarVisibilityModel
{
    /// <summary>Daylight-derived fade — the fallback until a game's star controller is recovered.</summary>
    DaylightFade,

    /// <summary>
    ///     FO4 <c>Stars::Update</c> / Skyrim schedule: fade against the extended sunrise/sunset
    ///     color windows (see <c>AtmosphereState.ComputeCreationStarVisibility</c>).
    /// </summary>
    CreationColorWindows
}

/// <summary>
///     Per-game atmosphere behavior recovered from binaries — the sun-path model, whether the
///     sunlight band is modulated by the daylight factor, the <c>fDaytimeColorExtension</c> window
///     widening, and the star-visibility schedule. Sibling of <see cref="Atmosphere.SkySunProfile" /> /
///     <see cref="Atmosphere.SkyMoonProfile" />; selected once per resolve by
///     <see cref="AtmosphereState" />. Fields move here only with decompile evidence — a game keeps
///     the stand-in/fallback value until its binary is audited.
/// </summary>
public sealed record AtmosphereProfile
{
    /// <summary>Analytic-arc stand-in (Morrowind, Oblivion, Unknown) — daylight-scaled, no extensions.</summary>
    private static readonly AtmosphereProfile Legacy = new()
    {
        SunPath = SunPathModel.AnalyticArc,
        SunColorScaledByDaylight = true,
        ExtendsWeatherColorWindow = false,
        WeatherDayUsesExtendedWindows = false,
        StarVisibility = StarVisibilityModel.DaylightFade
    };

    /// <summary>FNV/FO3: recovered triangle-wave sun path; classic daylight-scaled sunlight band.</summary>
    private static readonly AtmosphereProfile Fnv = Legacy with
    {
        SunPath = SunPathModel.FnvTriangleWave
    };

    /// <summary>
    ///     TES4 (Oblivion): recovered triangle-wave sun path (see <see cref="SunPathModel.Tes4TriangleWave" />);
    ///     classic daylight-scaled sunlight band (Sky::UpdateColors lineage, like FNV/FO3).
    /// </summary>
    private static readonly AtmosphereProfile Tes4 = Legacy with
    {
        SunPath = SunPathModel.Tes4TriangleWave
    };

    private static readonly AtmosphereProfile Skyrim = new()
    {
        SunPath = SunPathModel.SkyrimTriangleWave,
        SunColorScaledByDaylight = true,
        ExtendsWeatherColorWindow = true,
        WeatherDayUsesExtendedWindows = true,
        StarVisibility = StarVisibilityModel.CreationColorWindows
    };

    private static readonly AtmosphereProfile Fallout4 = new()
    {
        SunPath = SunPathModel.Fo4Continuous,
        SunColorScaledByDaylight = false,
        ExtendsWeatherColorWindow = true,
        WeatherDayUsesExtendedWindows = false,
        StarVisibility = StarVisibilityModel.CreationColorWindows
    };

    /// <summary>
    ///     FO76 (and provisionally Starfield): FO4's record layout and continuous sun path, but the
    ///     color-window extension and star controller have not been recovered from their binaries —
    ///     both stay on the conservative fallbacks until audited.
    /// </summary>
    private static readonly AtmosphereProfile Fallout76 = Fallout4 with
    {
        ExtendsWeatherColorWindow = false,
        StarVisibility = StarVisibilityModel.DaylightFade
    };

    /// <summary>The recovered sun-path implementation for the scene sun direction.</summary>
    public required SunPathModel SunPath { get; init; }

    /// <summary>
    ///     True when Sky::UpdateColors multiplies the NAM0 Sunlight band by the daylight factor
    ///     (Gamebryo-era: the directional fades out at night and ambient-only night stands). False
    ///     for the FO4 family, whose engine never scales the band — the night look (moonlight blue)
    ///     is authored into the rows themselves.
    /// </summary>
    public required bool SunColorScaledByDaylight { get; init; }

    /// <summary>
    ///     True when weather color windows are widened by <c>fDaytimeColorExtension</c> (0.5 h).
    ///     Recovered ONLY from Skyrim and FO4 binaries; FO76/Starfield share the modern record
    ///     layout but stay on authored CLMT bounds until their binaries are audited.
    /// </summary>
    public required bool ExtendsWeatherColorWindow { get; init; }

    /// <summary>
    ///     True when the fog day fraction samples the extended weather windows instead of the plain
    ///     climate daylight factor. Skyrim-only today (Sky::UpdateFog uses the same
    ///     fDaytimeColorExtension window as its colors); FO4's fog keeps the plain factor.
    /// </summary>
    public required bool WeatherDayUsesExtendedWindows { get; init; }

    /// <summary>The star-controller schedule for this game.</summary>
    public required StarVisibilityModel StarVisibility { get; init; }

    public static AtmosphereProfile ForGame(BethesdaGame game)
    {
        return game switch
        {
            BethesdaGame.Skyrim => Skyrim,
            BethesdaGame.Fallout4 => Fallout4,
            BethesdaGame.Fallout76 or BethesdaGame.Starfield => Fallout76,
            BethesdaGame.FalloutNewVegas or BethesdaGame.Fallout3 => Fnv,
            BethesdaGame.Oblivion => Tes4,
            _ => Legacy
        };
    }
}
