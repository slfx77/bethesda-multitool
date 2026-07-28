using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Pins every game's <see cref="AtmosphereProfile" /> — these fields reproduce the inline
///     per-game branches that previously lived in <c>AtmosphereState</c>, so a change here changes
///     the sun path, sunlight-band scaling, fDaytimeColorExtension widening, and star schedule for
///     that game. FO76/Starfield deliberately stay on the conservative fallbacks (no extension,
///     daylight-fade stars) until their binaries are audited — do not "fix" them to match FO4
///     without decompile evidence.
/// </summary>
public sealed class AtmosphereProfileTests
{
    [Theory]
    [InlineData(BethesdaGame.Unknown, SunPathModel.AnalyticArc, true, false, false, StarVisibilityModel.DaylightFade)]
    [InlineData(BethesdaGame.Morrowind, SunPathModel.AnalyticArc, true, false, false, StarVisibilityModel.DaylightFade)]
    [InlineData(BethesdaGame.Oblivion, SunPathModel.Tes4TriangleWave, true, false, false,
        StarVisibilityModel.DaylightFade)]
    [InlineData(BethesdaGame.Fallout3, SunPathModel.FnvTriangleWave, true, false, false,
        StarVisibilityModel.DaylightFade)]
    [InlineData(BethesdaGame.FalloutNewVegas, SunPathModel.FnvTriangleWave, true, false, false,
        StarVisibilityModel.DaylightFade)]
    [InlineData(BethesdaGame.Skyrim, SunPathModel.SkyrimTriangleWave, true, true, true,
        StarVisibilityModel.CreationColorWindows)]
    [InlineData(BethesdaGame.Fallout4, SunPathModel.Fo4Continuous, false, true, false,
        StarVisibilityModel.CreationColorWindows)]
    [InlineData(BethesdaGame.Fallout76, SunPathModel.Fo4Continuous, false, false, false,
        StarVisibilityModel.DaylightFade)]
    [InlineData(BethesdaGame.Starfield, SunPathModel.Fo4Continuous, false, false, false,
        StarVisibilityModel.DaylightFade)]
    public void ForGame_PinsRecoveredAtmosphereBehavior(
        BethesdaGame game,
        SunPathModel sunPath,
        bool sunColorScaledByDaylight,
        bool extendsWeatherColorWindow,
        bool weatherDayUsesExtendedWindows,
        StarVisibilityModel starVisibility)
    {
        var profile = AtmosphereProfile.ForGame(game);
        Assert.Equal(sunPath, profile.SunPath);
        Assert.Equal(sunColorScaledByDaylight, profile.SunColorScaledByDaylight);
        Assert.Equal(extendsWeatherColorWindow, profile.ExtendsWeatherColorWindow);
        Assert.Equal(weatherDayUsesExtendedWindows, profile.WeatherDayUsesExtendedWindows);
        Assert.Equal(starVisibility, profile.StarVisibility);
    }

    [Fact]
    public void ForGame_CoversEveryEnumValue()
    {
        foreach (var game in Enum.GetValues<BethesdaGame>())
        {
            Assert.NotNull(AtmosphereProfile.ForGame(game));
        }
    }

    /// <summary>
    ///     The extended-window fog day fraction is a Skyrim-only recovery (Sky::UpdateFog); FO4's
    ///     fog keeps the plain daylight factor even though its color windows are extended.
    /// </summary>
    [Fact]
    public void WeatherDayExtension_IsSkyrimOnly()
    {
        var withExtendedFog = Enum.GetValues<BethesdaGame>()
            .Where(g => AtmosphereProfile.ForGame(g).WeatherDayUsesExtendedWindows)
            .ToArray();
        Assert.Equal([BethesdaGame.Skyrim], withExtendedFog);
    }
}