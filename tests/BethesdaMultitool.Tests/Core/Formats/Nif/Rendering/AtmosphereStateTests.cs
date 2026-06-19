using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Locks the public contract of <see cref="AtmosphereState.Resolve" /> — the shared atmosphere
///     model the lighting / sky / water shaders read. Grounded in the engine decompile (atmosphere P2b):
///     the sun intensity is the daylight fraction (0 below the horizon, flat across the day), and the
///     WTHR NAM0 bands cross-fade only within the sunrise/sunset windows and are held steady between
///     them. These invariants (noon sun high + bright, night sun down + dark, lighting-off zeroes the
///     sun, FNAM drives fog, unit sun direction, hour wraps) must hold.
/// </summary>
public sealed class AtmosphereStateTests
{
    // --- P4: WTHR NAM0 time-band color blend ----------------------------------------------------

    // Timing with clean keyframe hours: sunriseMid = 6, noon = 12, sunsetMid = 18.
    private static readonly AtmosphereState.ClimateTiming CleanTiming = new(5f, 7f, 17f, 19f);

    [Fact]
    public void Resolve_Noon_SunHighAndBright()
    {
        var a = AtmosphereState.Resolve(12f);

        Assert.True(a.SunIntensity > 0.9f, $"noon sun should be near peak intensity, got {a.SunIntensity}");
        Assert.True(a.SunWorldDirection.Z > 0.9f,
            $"noon sun should point nearly straight up, got {a.SunWorldDirection}");
        Assert.True(a.SunColor.X > 0.5f, "noon sun should be bright");
    }

    [Fact]
    public void Resolve_Midnight_SunDownAndDark()
    {
        var a = AtmosphereState.Resolve(0f);

        Assert.Equal(0f, a.SunIntensity);
        Assert.Equal(Vector3.Zero, a.SunColor);
        Assert.True(a.SunWorldDirection.Z < 0f, "midnight sun must be below the horizon");
        // Ambient/sky still resolve (the scene isn't pitch black) but are dimmer than the day palette.
        Assert.True(a.AmbientColor.Length() < AtmosphereState.Resolve(12f).AmbientColor.Length());
    }

    [Fact]
    public void Resolve_LightingDisabled_ZeroesSunButKeepsSky()
    {
        var a = AtmosphereState.Resolve(12f, lightingEnabled: false);

        Assert.Equal(0f, a.SunIntensity);
        Assert.Equal(Vector3.Zero, a.SunColor);
        // Sky/fog are unaffected by the lighting toggle (they come from the sky/weather, not the sun).
        Assert.True(a.SkyTopColor.Length() > 0f);
    }

    [Theory]
    [InlineData(7f)]
    [InlineData(10f)]
    [InlineData(12f)]
    [InlineData(15f)]
    [InlineData(17f)]
    public void Resolve_DaytimeSunDirectionIsUnitLength(float hour)
    {
        var a = AtmosphereState.Resolve(hour);
        Assert.Equal(1f, a.SunWorldDirection.Length(), 3);
    }

    [Fact]
    public void Resolve_UsesWeatherFogDistances_WhenPresent()
    {
        var weather = new WeatherRecord { FogDistances = new[] { 1000f, 5000f } };
        var a = AtmosphereState.Resolve(12f, weather);

        Assert.Equal(1000f, a.FogNear, 3);
        Assert.Equal(5000f, a.FogFar, 3);
    }

    [Fact]
    public void Resolve_FallsBackToDefaultFog_WhenWeatherHasNone()
    {
        var withNone = AtmosphereState.Resolve(12f, new WeatherRecord());
        var withNull = AtmosphereState.Resolve(12f);

        Assert.True(withNone.FogFar > withNone.FogNear);
        Assert.Equal(withNull.FogNear, withNone.FogNear, 3);
        Assert.Equal(withNull.FogFar, withNone.FogFar, 3);
    }

    [Fact]
    public void Resolve_FogDistances_BlendDayNightByDaylightFraction()
    {
        // Six-float FNAM: [0]=DayNear [1]=DayFar [2]=NightNear [3]=NightFar [4]=DayPower [5]=NightPower.
        // The engine (Sky::UpdateFog) blends day↔night by the daylight fraction — full day at noon,
        // full night at midnight (CleanTiming sunrise 5..7, sunset 17..19).
        var w = new WeatherRecord { FogDistances = new[] { 1000f, 5000f, 200f, 2000f, 1f, 3f } };

        var noon = AtmosphereState.Resolve(12f, w, CleanTiming);
        Assert.Equal(1000f, noon.FogNear, 1);
        Assert.Equal(5000f, noon.FogFar, 1);
        Assert.Equal(1f, noon.FogPower, 2);

        var midnight = AtmosphereState.Resolve(0f, w, CleanTiming);
        Assert.Equal(200f, midnight.FogNear, 1);
        Assert.Equal(2000f, midnight.FogFar, 1);
        Assert.Equal(3f, midnight.FogPower, 2);
    }

    [Fact]
    public void Resolve_HourWrapsModulo24()
    {
        var noon = AtmosphereState.Resolve(12f);
        var wrapped = AtmosphereState.Resolve(36f); // 36 % 24 == 12

        Assert.Equal(noon.SunIntensity, wrapped.SunIntensity, 5);
        Assert.Equal(noon.SunWorldDirection.Z, wrapped.SunWorldDirection.Z, 5);
    }

    [Fact]
    public void Resolve_DawnIsDimmerThanNoon()
    {
        // 06:00 is the midpoint of the default sunrise window, where the engine daylight fraction is
        // half. The engine ramps directional intensity across that window, so dawn is dimmer than day.
        var dawn = AtmosphereState.Resolve(6f);
        var noon = AtmosphereState.Resolve(12f);

        Assert.True(dawn.SunIntensity < noon.SunIntensity, "dawn sun should be lower/dimmer than noon");
    }

    [Fact]
    public void Resolve_DaylightFraction_ZeroAtNight_FullMidday()
    {
        // Engine daylight fraction (Sun::Update): 0 below the horizon, 1 through the day.
        Assert.Equal(0f, AtmosphereState.Resolve(2f).SunIntensity);
        Assert.Equal(1f, AtmosphereState.Resolve(12f).SunIntensity, 3);
    }

    [Fact]
    public void Resolve_Noon_PicksWeatherDayBand()
    {
        var w = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var a = AtmosphereState.Resolve(12f, w, CleanTiming);
        AssertColor(200, 210, 220, a.AmbientColor);
    }

    [Fact]
    public void Resolve_Midnight_PicksWeatherNightBand()
    {
        var w = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var a = AtmosphereState.Resolve(0f, w, CleanTiming);
        AssertColor(5, 5, 8, a.AmbientColor);
    }

    [Fact]
    public void Resolve_SunriseMidpoint_PicksWeatherSunriseBand()
    {
        var w = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var a = AtmosphereState.Resolve(6f, w, CleanTiming); // sunriseMid for CleanTiming
        AssertColor(10, 12, 14, a.AmbientColor);
    }

    [Fact]
    public void Resolve_WeatherWithoutNam0_KeepsPlaceholderColors()
    {
        // Empty Colors must behave exactly like no weather (the NAM0 branch is additive).
        var withEmpty = AtmosphereState.Resolve(12f, new WeatherRecord());
        var withNull = AtmosphereState.Resolve(12f);

        Assert.Equal(withNull.AmbientColor, withEmpty.AmbientColor);
        Assert.Equal(withNull.SkyTopColor, withEmpty.SkyTopColor);
        Assert.Equal(withNull.SkyHorizonColor, withEmpty.SkyHorizonColor);
        Assert.Equal(withNull.FogColor, withEmpty.FogColor);
    }

    [Fact]
    public void Resolve_Nam0BandBlend_IsContinuousAcrossMidnight()
    {
        var w = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var justBefore = AtmosphereState.Resolve(23.99f, w, CleanTiming).AmbientColor;
        var justAfter = AtmosphereState.Resolve(0.01f, w, CleanTiming).AmbientColor;
        Assert.True((justBefore - justAfter).Length() < 0.02f, "Night↔Night wrap must be continuous at midnight");
    }

    [Fact]
    public void ClimateTiming_FromClimateData_ConvertsBytesToHours()
    {
        var data = new ClimateTimingData(36, 42, 108, 114, 0, 0); // ÷6 → 6, 7, 18, 19
        var t = AtmosphereState.ClimateTiming.FromClimateData(data);

        Assert.Equal(6f, t.SunriseBeginHour, 3);
        Assert.Equal(7f, t.SunriseEndHour, 3);
        Assert.Equal(18f, t.SunsetBeginHour, 3);
        Assert.Equal(19f, t.SunsetEndHour, 3);
        Assert.Equal(AtmosphereState.ClimateTiming.Default, AtmosphereState.ClimateTiming.FromClimateData(null));
    }

    // --- P2b: windowed band blend (grounded in Sky::FillColorBlend) -------------------------------
    // The model cross-fades only within the sunrise/sunset windows and holds bands steady between them
    // (Day held solid through midday is a deliberate simplification of the engine's noon-pivot daytime
    // cross-fade — see AtmosphereState.SampleBand). These pin that contract: the earlier continuous-lerp
    // model would instead return a partial sunrise→day blend at 10:00.

    [Fact]
    public void Resolve_MidMorning_HoldsSolidDayBand()
    {
        // 10:00 is past sunriseEnd (7) and before sunsetBegin (17) → solid Day, not a sunrise→day blend.
        var w = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var a = AtmosphereState.Resolve(10f, w, CleanTiming);
        AssertColor(200, 210, 220, a.AmbientColor);
    }

    [Fact]
    public void Resolve_LateEvening_HoldsSolidNightBand()
    {
        // 22:00 is past sunsetEnd (19) → solid Night.
        var w = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var a = AtmosphereState.Resolve(22f, w, CleanTiming);
        AssertColor(5, 5, 8, a.AmbientColor);
    }

    [Fact]
    public void Resolve_SunsetMidpoint_PicksWeatherSunsetBand()
    {
        // 18:00 is the sunset-window midpoint for CleanTiming → exactly the Sunset band.
        var w = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var a = AtmosphereState.Resolve(18f, w, CleanTiming);
        AssertColor(50, 40, 30, a.AmbientColor);
    }

    private static WeatherRecord WeatherWithAmbient(WeatherRgba sunrise, WeatherRgba day, WeatherRgba sunset,
        WeatherRgba night)
    {
        var zero = new WeatherRgba(0, 0, 0, 0);
        var colors = new WeatherColor[15];
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = new WeatherColor(zero, zero, zero, zero);
        }

        colors[(int)WeatherColorType.Ambient] = new WeatherColor(sunrise, day, sunset, night);
        return new WeatherRecord { Colors = colors };
    }

    private static void AssertColor(byte r, byte g, byte b, Vector3 actual)
    {
        Assert.Equal(r / 255f, actual.X, 3);
        Assert.Equal(g / 255f, actual.Y, 3);
        Assert.Equal(b / 255f, actual.Z, 3);
    }
}