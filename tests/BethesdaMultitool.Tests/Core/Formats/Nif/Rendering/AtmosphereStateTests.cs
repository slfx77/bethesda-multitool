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
        // Apex is ~50° (sin 50° ≈ 0.766), NOT near-zenith: the engine's sun path (a triangle wave with a
        // constant lateral offset) never reaches overhead, and a zenith sun zeroes N·L on every vertical
        // surface — the "too dark at midday" bug. See AtmosphereState.PeakSunElevation.
        Assert.True(a.SunWorldDirection.Z is > 0.7f and < 0.85f,
            $"noon sun should sit at the ~50° apex, got {a.SunWorldDirection}");
        Assert.True(a.SunColor.X > 0.5f, "noon sun should be bright");
    }

    [Fact]
    public void Resolve_Midnight_SunDownAndDark()
    {
        var a = AtmosphereState.Resolve(0f);

        Assert.Equal(0f, a.SunIntensity);
        // The directional COLOUR is faded to 0 by the daylight fraction at night (Resolve), so no
        // below-horizon vector can light mesh undersides. The night direction itself is moot (colour 0).
        Assert.Equal(Vector3.Zero, a.SunColor);
        Assert.True(a.SunWorldDirection.Z < 0f, "midnight sun is below the horizon (its colour is faded to 0)");
        // Ambient/sky still resolve (the scene isn't pitch black) but are dimmer than the day palette.
        Assert.True(a.AmbientColor.Length() < AtmosphereState.Resolve(12f).AmbientColor.Length());
    }

    [Fact]
    public void Resolve_PrefersDalcDirectionalAmbient_OverNam0AmbientRow()
    {
        // FO4 authors the NAM0 Ambient row near-black at night (CommonwealthClear Night = (2,2,2))
        // and lights exteriors from the DALC directional-ambient cube instead (night mean ≈
        // (18,26,34)). When the weather carries a DirectionalAmbient, it must win over the row —
        // sourcing ambient from NAM0 rendered FO4 nights pitch black.
        var black = new WeatherRgba(2, 2, 2, 255);
        var nam0 = new WeatherColor(black, black, black, black);
        var dalcNight = new WeatherRgba(18, 26, 34, 255);
        var weather = new WeatherRecord
        {
            // Ambient is NAM0 category 3 — fill 0..3 so the index resolves.
            Colors = [nam0, nam0, nam0, nam0],
            DirectionalAmbient = new WeatherColor(dalcNight, dalcNight, dalcNight, dalcNight)
        };

        var midnight = AtmosphereState.Resolve(0f, weather, CleanTiming);

        Assert.Equal(18f / 255f, midnight.AmbientColor.X, 3);
        Assert.Equal(26f / 255f, midnight.AmbientColor.Y, 3);
        Assert.Equal(34f / 255f, midnight.AmbientColor.Z, 3);
    }

    [Fact]
    public void Resolve_ModernWeatherAtNight_HandsDirectionalToTheMoon()
    {
        // Skyrim+/FO4: once the sun is fully down, the scene's directional light aims at the moon,
        // colored by the NAM0 Sunlight NIGHT band (CommonwealthClear: (53,70,87)). Gated on the
        // weather carrying DALC — the modern-weather marker.
        var moonBlue = new WeatherRgba(53, 70, 87, 255);
        var sunlight = new WeatherColor(moonBlue, new WeatherRgba(225, 225, 225, 255), moonBlue, moonBlue);
        var gray = new WeatherRgba(64, 64, 64, 255);
        var filler = new WeatherColor(gray, gray, gray, gray);
        var dalc = new WeatherRgba(18, 26, 34, 255);
        var weather = new WeatherRecord
        {
            Colors = [filler, filler, filler, filler, sunlight],
            DirectionalAmbient = new WeatherColor(dalc, dalc, dalc, dalc)
        };
        var moonDir = Vector3.Normalize(new Vector3(0.2f, 0.3f, 0.8f));

        var midnight = AtmosphereState.Resolve(0f, weather, CleanTiming, moonlightDirection: moonDir);

        Assert.Equal(moonDir.X, midnight.SunWorldDirection.X, 4);
        Assert.Equal(moonDir.Z, midnight.SunWorldDirection.Z, 4);
        Assert.Equal(53f / 255f, midnight.SunColor.X, 3);
        Assert.Equal(87f / 255f, midnight.SunColor.Z, 3);
        // Sun-keyed consumers (specular scale, moon-billboard fade) must still read "night".
        Assert.Equal(0f, midnight.SunIntensity);
    }

    [Fact]
    public void Resolve_MoonlightIgnored_WithoutDalc_AndBelowHorizon()
    {
        // FNV/FO3 weathers carry no DALC — their decompile-grounded ambient-only night must not
        // gain a moon directional. And a set moon (Z ≤ 0) never lights the scene.
        var gray = new WeatherRgba(64, 64, 64, 255);
        var filler = new WeatherColor(gray, gray, gray, gray);
        var fnvLike = new WeatherRecord { Colors = [filler, filler, filler, filler, filler] };

        var noDalc = AtmosphereState.Resolve(0f, fnvLike, CleanTiming,
            moonlightDirection: Vector3.UnitZ);
        Assert.Equal(Vector3.Zero, noDalc.SunColor);

        var dalc = new WeatherRgba(18, 26, 34, 255);
        var modern = fnvLike with { DirectionalAmbient = new WeatherColor(dalc, dalc, dalc, dalc) };
        var moonSet = AtmosphereState.Resolve(0f, modern, CleanTiming,
            moonlightDirection: new Vector3(0.7f, 0.7f, -0.2f));
        Assert.Equal(Vector3.Zero, moonSet.SunColor);
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
    // The model cross-fades only within the sunrise/sunset windows; between them Day/Night hold solid
    // UNLESS the FNV High Noon / Midnight peak slots are authored (see the peak-slot tests above — these
    // 4-band records leave the peaks zero, so the solid segments are pinned here). The earlier
    // continuous-lerp model would instead return a partial sunrise→day blend at 10:00.

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

    [Fact]
    public void SampleCloudColor_BlendsBandsByHour_KeepsAlphaAsOpacity()
    {
        // A cloud layer's PNAM color: RGB tint + A opacity, sampled by the SAME band blend as the sky.
        var c = new WeatherColor(
            new WeatherRgba(50, 50, 50, 50),    // sunrise
            new WeatherRgba(10, 20, 30, 200),   // day
            new WeatherRgba(60, 60, 60, 60),    // sunset
            new WeatherRgba(1, 2, 3, 40));      // night

        var noon = AtmosphereState.SampleCloudColor(c, 12f, CleanTiming);
        Assert.Equal(10f / 255f, noon.X, 3);
        Assert.Equal(20f / 255f, noon.Y, 3);
        Assert.Equal(30f / 255f, noon.Z, 3);
        Assert.Equal(200f / 255f, noon.W, 3); // alpha = layer opacity, NOT dropped

        var midnight = AtmosphereState.SampleCloudColor(c, 0f, CleanTiming);
        Assert.Equal(1f / 255f, midnight.X, 3);
        Assert.Equal(40f / 255f, midnight.W, 3);
    }

    // --- FNV High Noon / Midnight peak slots (6-band NAM0, Sky::FillColorBlend's noon/midnight pivots) ---
    // An authored peak cross-fades Day↔HighNoon around solar noon and Night↔Midnight around solar
    // midnight; an all-zero peak is "unused" (fopdoc) and keeps the old solid Day/Night segments — the
    // 4-band tests above pin that unchanged behavior.

    [Fact]
    public void Resolve_SolarNoon_PicksAuthoredHighNoonPeak()
    {
        // CleanTiming: solar noon = (srB + ssE)/2 = (5+19)/2 = 12 → exactly the High Noon color.
        var w = WeatherWithAmbient6(
            new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255),
            new WeatherRgba(90, 120, 150, 255), new WeatherRgba(2, 3, 4, 255));
        var a = AtmosphereState.Resolve(12f, w, CleanTiming);
        AssertColor(90, 120, 150, a.AmbientColor);
    }

    [Fact]
    public void Resolve_MidMorning_BlendsDayTowardHighNoon()
    {
        // 9.5h is halfway between sunriseEnd (7) and solar noon (12) → Day/HighNoon midpoint, not solid Day.
        var w = WeatherWithAmbient6(
            new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255),
            new WeatherRgba(100, 110, 120, 255), new WeatherRgba(0, 0, 0, 0));
        var a = AtmosphereState.Resolve(9.5f, w, CleanTiming);
        AssertColor(150, 160, 170, a.AmbientColor); // lerp(Day, HighNoon, 0.5)
    }

    [Fact]
    public void Resolve_SolarMidnight_PicksAuthoredMidnightPeak_AndWrapsContinuously()
    {
        // CleanTiming: solar midnight = solar noon + 12 = 24 ≡ 0 → exactly the Midnight color there,
        // and the 23.99↔0.01 wrap must stay continuous.
        var w = WeatherWithAmbient6(
            new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(80, 80, 100, 255),
            new WeatherRgba(0, 0, 0, 0), new WeatherRgba(2, 2, 4, 255));

        var justBefore = AtmosphereState.Resolve(23.99f, w, CleanTiming).AmbientColor;
        var justAfter = AtmosphereState.Resolve(0.01f, w, CleanTiming).AmbientColor;
        Assert.True((justBefore - new Vector3(2 / 255f, 2 / 255f, 4 / 255f)).Length() < 0.02f,
            "solar midnight must sit on the Midnight peak");
        Assert.True((justBefore - justAfter).Length() < 0.02f, "midnight wrap must stay continuous");

        // Halfway through the evening night span (19 → 24): Night/Midnight midpoint.
        var evening = AtmosphereState.Resolve(21.5f, w, CleanTiming).AmbientColor;
        AssertColor(41, 41, 52, evening); // lerp(Night(80,80,100), Midnight(2,3,4), 0.5)
    }

    [Fact]
    public void Resolve_ZeroPeaks_KeepSolidDayAndNight()
    {
        // All-zero HighNoon/Midnight = unauthored → identical to the 4-band record at noon and midnight.
        var four = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var six = WeatherWithAmbient6(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255),
            new WeatherRgba(0, 0, 0, 0), new WeatherRgba(0, 0, 0, 0));

        Assert.Equal(
            AtmosphereState.Resolve(12f, four, CleanTiming).AmbientColor,
            AtmosphereState.Resolve(12f, six, CleanTiming).AmbientColor);
        Assert.Equal(
            AtmosphereState.Resolve(0f, four, CleanTiming).AmbientColor,
            AtmosphereState.Resolve(0f, six, CleanTiming).AmbientColor);
    }

    private static WeatherRecord WeatherWithAmbient6(WeatherRgba sunrise, WeatherRgba day, WeatherRgba sunset,
        WeatherRgba night, WeatherRgba highNoon, WeatherRgba midnight)
    {
        var zero = new WeatherRgba(0, 0, 0, 0);
        var colors = new WeatherColor[15];
        for (var i = 0; i < colors.Length; i++)
        {
            colors[i] = new WeatherColor(zero, zero, zero, zero);
        }

        colors[(int)WeatherColorType.Ambient] = new WeatherColor(sunrise, day, sunset, night, highNoon, midnight);
        return new WeatherRecord { Colors = colors };
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