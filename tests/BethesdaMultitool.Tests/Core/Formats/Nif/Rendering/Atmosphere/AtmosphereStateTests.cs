using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Atmosphere;

/// <summary>
///     Locks the public contract of <see cref="AtmosphereState.Resolve" /> — the shared atmosphere
///     model the lighting / sky / water shaders read. Grounded in the engine decompile (atmosphere P2b):
///     the sun intensity is the daylight fraction (0 below the horizon, flat across the day), and the
///     WTHR NAM0 bands cross-fade within the sunrise/sunset windows; FNV's optional HighNoon slot also
///     peaks at the engine's fixed 12:00 member. These invariants (noon sun high + bright, night sun down + dark,
///     lighting-off zeroes the
///     sun, FNAM drives fog, unit sun direction, hour wraps) must hold.
/// </summary>
public sealed class AtmosphereStateTests
{
    // --- P4: WTHR NAM0 time-band color blend ----------------------------------------------------

    // Timing with clean keyframe hours: sunriseMid = 6, noon = 12, sunsetMid = 18.
    private static readonly AtmosphereState.ClimateTiming CleanTiming = new(5f, 7f, 17f, 19f);

    [Fact]
    public void ApplySkyColorScale_ScalesAllFourSkyColorsOnly()
    {
        var original = AtmosphereState.Resolve(12f);
        const float scale = 0.375f;

        var scaled = AtmosphereState.ApplySkyColorScale(original, scale);

        Assert.Equal(original.SkyTopColor * scale, scaled.SkyTopColor);
        Assert.Equal(original.SkyLowerColor * scale, scaled.SkyLowerColor);
        Assert.Equal(original.AuthoredHorizonColor * scale, scaled.AuthoredHorizonColor);
        Assert.Equal(original.SkyHorizonColor * scale, scaled.SkyHorizonColor);
        Assert.Equal(original.SunColor, scaled.SunColor);
        Assert.Equal(original.AmbientColor, scaled.AmbientColor);
        Assert.Equal(original.FogColor, scaled.FogColor);
        Assert.Equal(original.DirectionalAmbient, scaled.DirectionalAmbient);
    }

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
    public void Resolve_PreservesAndEvaluatesEveryDalcDirection()
    {
        var cube = new WeatherAmbientCube
        {
            PositiveX = new WeatherRgba(255, 0, 0, 255),
            NegativeX = new WeatherRgba(0, 255, 0, 255),
            PositiveY = new WeatherRgba(0, 0, 255, 255),
            NegativeY = new WeatherRgba(255, 255, 0, 255),
            PositiveZ = new WeatherRgba(255, 0, 255, 255),
            NegativeZ = new WeatherRgba(0, 255, 255, 255)
        };
        var weather = new WeatherRecord
        {
            DirectionalAmbientCubes = new WeatherTimeBands<WeatherAmbientCube>(cube, cube, cube, cube),
            // Compatibility mean deliberately disagrees: the lossless cube must win.
            DirectionalAmbient = new WeatherColor(
                new WeatherRgba(1, 1, 1, 255), new WeatherRgba(1, 1, 1, 255),
                new WeatherRgba(1, 1, 1, 255), new WeatherRgba(1, 1, 1, 255))
        };

        var resolved = AtmosphereState.Resolve(12f, weather, CleanTiming, game: BethesdaGame.Fallout4);
        var sampled = Assert.IsType<AtmosphereState.ResolvedAmbientCube>(resolved.DirectionalAmbient);

        Assert.Equal(new Vector3(1f, 0f, 0f), sampled.Evaluate(Vector3.UnitX));
        Assert.Equal(new Vector3(0f, 1f, 1f), sampled.Evaluate(-Vector3.UnitZ));
        // normalize(1,1,0) has squared component weights (0.5,0.5,0), selecting +X/+Y.
        var diagonal = sampled.Evaluate(new Vector3(1f, 1f, 0f));
        Assert.Equal(0.5f, diagonal.X, 6);
        Assert.Equal(0f, diagonal.Y, 6);
        Assert.Equal(0.5f, diagonal.Z, 6);
        Assert.Equal(sampled.Mean, resolved.AmbientColor);
    }

    [Fact]
    public void SampleDirectionalAmbientCube_UsesModernQuarterBandsWithoutCollapsingFaces()
    {
        static WeatherAmbientCube Solid(byte value)
        {
            return new WeatherAmbientCube
            {
                PositiveX = new WeatherRgba(value, 0, 0, 255),
                NegativeX = new WeatherRgba(0, value, 0, 255),
                PositiveY = new WeatherRgba(0, 0, value, 255),
                NegativeY = new WeatherRgba(value, value, 0, 255),
                PositiveZ = new WeatherRgba(value, 0, value, 255),
                NegativeZ = new WeatherRgba(0, value, value, 255)
            };
        }

        var bands = new WeatherTimeBands<WeatherAmbientCube>(Solid(64), Solid(192), Solid(96), Solid(16))
        {
            EarlySunrise = Solid(32),
            LateSunrise = Solid(128),
            EarlySunset = Solid(160),
            LateSunset = Solid(48)
        };

        // Sunrise window 5..7: hour 5.5 is exactly its first quarter key (Early Sunrise).
        var sampled = AtmosphereState.SampleDirectionalAmbientCube(bands, 5.5f, 5f, 7f, 17f, 19f);

        Assert.Equal(32f / 255f, sampled.PositiveX.X, 6);
        Assert.Equal(32f / 255f, sampled.NegativeX.Y, 6);
        Assert.Equal(32f / 255f, sampled.PositiveY.Z, 6);
        Assert.Equal(Vector3.Zero, new Vector3(sampled.PositiveX.Y, sampled.PositiveX.Z, sampled.NegativeX.X));
    }

    [Fact]
    public void Resolve_ModernWeatherAtNight_KeepsTheEngineDirectionalPath()
    {
        // FO4-family (DALC weather): the directional light follows FO4 Sun::Update's CONTINUOUS
        // 24h path — no sun→moon handover exists in the engine (the round-11 hard flip to the moon
        // billboard was refuted by the decompile). At night the light keeps the NAM0 Sunlight NIGHT
        // band color (CommonwealthClear (53,70,87)) UNSCALED, and its elevation is floored at
        // fSunShadowMinAngle (unit z ≥ 30·π/180 ≈ 0.5236) so it never grazes the horizon.
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

        var midnight = AtmosphereState.Resolve(0f, weather, CleanTiming,
            game: BethesdaGame.Fallout4);

        Assert.Equal(53f / 255f, midnight.SunColor.X, 3);
        Assert.Equal(87f / 255f, midnight.SunColor.Z, 3);
        Assert.True(midnight.SunWorldDirection.Z >= 0.52f,
            $"night directional must stay above the shadow floor, got {midnight.SunWorldDirection}");
        Assert.Equal(1f, midnight.SunWorldDirection.Length(), 3);
        // Sun-keyed consumers (specular scale, moon-billboard fade) must still read "night".
        Assert.Equal(0f, midnight.SunIntensity);
    }

    [Fact]
    public void Resolve_SkyrimDalc_DoesNotSelectFo4DirectionalFamily()
    {
        var moonBlue = new WeatherRgba(53, 70, 87, 255);
        var filler = new WeatherColor(moonBlue, moonBlue, moonBlue, moonBlue);
        var weather = new WeatherRecord
        {
            Colors = [filler, filler, filler, filler, filler],
            DirectionalAmbient = filler
        };

        var midnight = AtmosphereState.Resolve(0f, weather, CleanTiming, game: BethesdaGame.Skyrim);

        Assert.Equal(Vector3.Zero, midnight.SunColor);
        Assert.True(midnight.SunWorldDirection.Z < 0f,
            $"Skyrim DALC must not route through FO4's above-horizon night light: {midnight.SunWorldDirection}");
    }

    [Fact]
    public void Resolve_PrefersExplicitSunAndMoonGlareRows()
    {
        var red = new WeatherRgba(255, 0, 0, 255);
        var green = new WeatherRgba(0, 255, 0, 255);
        var weather = new WeatherRecord
        {
            SunGlareColor = new WeatherColor(red, red, red, red),
            MoonGlareColor = new WeatherColor(green, green, green, green)
        };

        var resolved = AtmosphereState.Resolve(12f, weather, CleanTiming, game: BethesdaGame.Skyrim);

        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), resolved.SunGlareColor);
        Assert.Equal(new Vector4(0f, 1f, 0f, 1f), resolved.MoonGlareColor);
    }

    [Fact]
    public void Resolve_Fo4ConsumesAuthoredSunStarsAndWidenedGlareRows()
    {
        var zero = new WeatherRgba(0, 0, 0, 0);
        var empty = new WeatherColor(zero, zero, zero, zero);
        var colors = Enumerable.Repeat(empty, 17).ToArray();
        var red = new WeatherRgba(255, 0, 0, 255);
        var green = new WeatherRgba(0, 255, 0, 128);
        var blue = new WeatherRgba(0, 0, 255, 64);
        var yellow = new WeatherRgba(255, 255, 0, 192);
        colors[(int)WeatherColorType.Sun] = new WeatherColor(red, red, red, red);
        colors[(int)WeatherColorType.Stars] = new WeatherColor(green, green, green, green);
        colors[(int)WeatherColorType.SunGlare] = new WeatherColor(blue, blue, blue, blue);
        colors[(int)WeatherColorType.MoonGlare] = new WeatherColor(yellow, yellow, yellow, yellow);
        var weather = new WeatherRecord
        {
            Colors = colors,
            Data = new WeatherData { SunGlare = 128 }
        };

        var resolved = AtmosphereState.Resolve(12f, weather, CleanTiming, game: BethesdaGame.Fallout4);

        Assert.Equal(new Vector4(1f, 0f, 0f, 1f), resolved.SunDiscColor);
        Assert.Equal(new Vector4(0f, 1f, 0f, 128f / 255f), resolved.StarsColor);
        Assert.Equal(new Vector4(0f, 0f, 1f, 64f / 255f), resolved.SunGlareColor);
        Assert.Equal(new Vector4(1f, 1f, 0f, 192f / 255f), resolved.MoonGlareColor);
        Assert.Equal(128f / 255f, resolved.SunGlareIntensity, 6);
    }

    [Fact]
    public void Resolve_CelestialRgbxPaddingSurvivesButNeverGatesVisibility()
    {
        var zero = new WeatherRgba(0, 0, 0, 0);
        var empty = new WeatherColor(zero, zero, zero, zero);
        var colors = Enumerable.Repeat(empty, 17).ToArray();
        var sunRgbx = new WeatherRgba(255, 192, 128, 0);
        var starsRgbx = new WeatherRgba(96, 128, 160, 0);
        var sunGlareRgbx = new WeatherRgba(240, 220, 180, 0);
        var moonGlareRgbx = new WeatherRgba(160, 180, 255, 0);
        colors[(int)WeatherColorType.Sun] = new WeatherColor(sunRgbx, sunRgbx, sunRgbx, sunRgbx);
        colors[(int)WeatherColorType.Stars] = new WeatherColor(starsRgbx, starsRgbx, starsRgbx, starsRgbx);
        var weather = new WeatherRecord
        {
            Colors = colors,
            // Skyrim NAM2/NAM3 are explicit RGBX rows whose fourth byte is padding.
            SunGlareColor = new WeatherColor(sunGlareRgbx, sunGlareRgbx, sunGlareRgbx, sunGlareRgbx),
            MoonGlareColor = new WeatherColor(moonGlareRgbx, moonGlareRgbx, moonGlareRgbx, moonGlareRgbx),
            Data = new WeatherData { SunGlare = 128 }
        };

        var noon = AtmosphereState.Resolve(12f, weather, CleanTiming, game: BethesdaGame.Skyrim);
        var midnight = AtmosphereState.Resolve(0f, weather, CleanTiming, game: BethesdaGame.Skyrim);

        // Lossless state retains all four raw X bytes.
        Assert.Equal(0f, noon.SunDiscColor.W);
        Assert.Equal(0f, noon.StarsColor.W);
        Assert.Equal(0f, noon.SunGlareColor.W);
        Assert.Equal(0f, noon.MoonGlareColor.W);

        // Independent visibility/controller state remains authoritative despite zero padding.
        Assert.Equal(1f, noon.SunDiscDrawAlpha);
        Assert.Equal(128f / 255f, noon.SunGlareDrawAlpha, 6);
        Assert.Equal(0f, noon.StarsDrawAlpha);
        Assert.Equal(1f, midnight.StarsDrawAlpha);
        Assert.Equal(0.75f, noon.MoonDiscDrawAlpha(0.75f));
    }

    [Theory]
    [InlineData(4.0f, 1f)]
    [InlineData(4.5f, 1f)]
    [InlineData(5.125f, 0.5f)]
    [InlineData(5.75f, 0f)]
    [InlineData(12f, 0f)]
    [InlineData(18.25f, 0f)]
    [InlineData(18.875f, 0.5f)]
    [InlineData(19.5f, 1f)]
    [InlineData(22f, 1f)]
    public void ComputeCreationStarVisibility_MatchesRecoveredFo4Controller(float hour, float expected)
    {
        // Independent FO4 Stars::Update vector for CleanTiming after fDaytimeColorExtension=.5:
        // BeginSunriseColors=4.5, sunrise fade midpoint=(4.5+7)/2=5.75;
        // sunset fade midpoint=(17+19.5)/2=18.25, EndSunsetColors=19.5.
        var actual = AtmosphereState.ComputeCreationStarVisibility(
            hour, 4.5f, 7f, 17f, 19.5f);

        Assert.Equal(expected, actual, 6);
    }

    [Fact]
    public void Resolve_Fo4StarsUseColorWindowMidpointsInsteadOfSunIntensityMultiplier()
    {
        // At 05:30 the recovered star fade is 20%: 1-(5.5-4.5)/(5.75-4.5). The previous
        // clamp(1-SunIntensity*1.5) approximation produced 62.5% for the same fixed state.
        var resolved = AtmosphereState.Resolve(
            5.5f, climate: CleanTiming, game: BethesdaGame.Fallout4);

        Assert.Equal(0.2f, resolved.StarVisibility, 6);
        Assert.Equal(0.2f, resolved.StarsDrawAlpha, 6);
    }

    [Fact]
    public void Resolve_Fo4DirectionalPath_IsContinuousAcrossDusk_AndNearOverheadAtNoon()
    {
        // Sun::Update: the day leg spans sunriseMid−1h .. sunsetMid+1h (fSunAlphaTransTime=2h) and the
        // night leg wraps continuously — there is NO discontinuity at the dusk edge (the user-visible
        // "snap to the moon" came from the old hard handover). Apex check: at noon the path
        // (0, fSunYExtreme=25, fSunXExtreme=400) normalizes to z ≈ 0.998 (≈86°) — NOT the FNV 50°.
        var before = AtmosphereState.Fo4SunPathDirection(18.999f, 5f, 7f, 17f, 19f); // dusk edge = 19
        var after = AtmosphereState.Fo4SunPathDirection(19.001f, 5f, 7f, 17f, 19f);
        Assert.True(Vector3.Distance(before, after) < 0.01f,
            $"directional must be continuous across dusk: {before} vs {after}");

        var noon = AtmosphereState.Fo4SunPathDirection(12f, 5f, 7f, 17f, 19f);
        Assert.True(noon.Z > 0.99f, $"FO4 noon sun is near-overhead, got {noon}");

        // Deep night: the wrapped leg re-crosses the zenith at solar midnight-ish, and the
        // fSunShadowMinAngle floor keeps the light WELL above the horizon at every hour. The engine
        // renormalizes AFTER flooring z, so at the path extremes (x=±fSunXExtreme) the unit z lands at
        // 0.5236/√(1²+0.0625²+0.5236²) ≈ 0.463 — the effective minimum elevation (~27.6°).
        for (var h = 0f; h < 24f; h += 0.5f)
        {
            var d = AtmosphereState.Fo4SunPathDirection(h, 5f, 7f, 17f, 19f);
            Assert.True(d.Z >= 0.45f, $"hour {h}: z {d.Z} fell below the shadow floor");
        }
    }

    [Fact]
    public void Resolve_FnvNightStaysAmbientOnly_WithoutDalc()
    {
        // FNV/FO3 weathers carry no DALC — their decompile-grounded ambient-only night is unchanged:
        // the directional colour fades to zero with the daylight fraction.
        var gray = new WeatherRgba(64, 64, 64, 255);
        var filler = new WeatherColor(gray, gray, gray, gray);
        var fnvLike = new WeatherRecord { Colors = [filler, filler, filler, filler, filler] };

        var noDalc = AtmosphereState.Resolve(0f, fnvLike, CleanTiming);
        Assert.Equal(Vector3.Zero, noDalc.SunColor);
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
    public void Resolve_SkyrimClear_UsesEightFieldFnamAndDistinctNearFarColors()
    {
        var neutral = new WeatherRgba(1, 1, 1, 255);
        var filler = new WeatherColor(neutral, neutral, neutral, neutral);
        var colors = Enumerable.Repeat(filler, 13).ToArray();
        colors[(int)WeatherColorType.Fog] = new WeatherColor(
            new WeatherRgba(0x2F, 0x4E, 0x5B, 255), new WeatherRgba(0x28, 0x6F, 0x8C, 255),
            new WeatherRgba(0x26, 0x44, 0x43, 255), new WeatherRgba(0x18, 0x2D, 0x3D, 255));
        colors[(int)WeatherColorType.FogFar] = new WeatherColor(
            new WeatherRgba(0x88, 0x8A, 0x99, 255), new WeatherRgba(0x8E, 0xC9, 0xE6, 255),
            new WeatherRgba(0x4E, 0x56, 0x5C, 255), new WeatherRgba(0x08, 0x1C, 0x21, 255));
        var skyrimClear = new WeatherRecord
        {
            Colors = colors,
            FogDistances = [1200f, 80000f, 1200f, 40000f, 0.4f, 0.4f, 0.85f, 0.85f]
        };

        var noon = AtmosphereState.Resolve(12f, skyrimClear, CleanTiming, game: BethesdaGame.Skyrim);
        Assert.Equal(1200f, noon.FogNear);
        Assert.Equal(80000f, noon.FogFar);
        Assert.Equal(0.4f, noon.FogPower, 3);
        Assert.Equal(0.85f, noon.FogMaxOpacity, 3);
        AssertColor(0x28, 0x6F, 0x8C, noon.FogColor);
        AssertColor(0x8E, 0xC9, 0xE6, noon.FogFarColor);

        var midnight = AtmosphereState.Resolve(0f, skyrimClear, CleanTiming, game: BethesdaGame.Skyrim);
        Assert.Equal(40000f, midnight.FogFar);
        AssertColor(0x18, 0x2D, 0x3D, midnight.FogColor);
        AssertColor(0x08, 0x1C, 0x21, midnight.FogFarColor);
    }

    [Fact]
    public void Resolve_SkyrimFogDayWeight_StartsAtHalfHourExtension()
    {
        // CleanTiming begins sunrise at 5.0 and reaches day at 7.0. Skyrim's exact extension starts
        // fog interpolation at A=4.5, so hour 5.0 has day weight (5-4.5)/(7-4.5)=0.2.
        var weather = new WeatherRecord
        {
            FogDistances = [1000f, 5000f, 0f, 1000f, 1f, 3f, 0.8f, 0.2f]
        };

        var skyrim = AtmosphereState.Resolve(5f, weather, CleanTiming, game: BethesdaGame.Skyrim);
        Assert.Equal(200f, skyrim.FogNear, 2);
        Assert.Equal(1800f, skyrim.FogFar, 2);
        Assert.Equal(2.6f, skyrim.FogPower, 2);
        Assert.Equal(0.32f, skyrim.FogMaxOpacity, 2);

        // Other engine families retain their previously grounded, unextended daylight window.
        var legacy = AtmosphereState.Resolve(5f, weather, CleanTiming);
        Assert.Equal(0f, legacy.FogNear);
        Assert.Equal(1000f, legacy.FogFar);
        Assert.Equal(0.2f, legacy.FogMaxOpacity, 2);
    }

    [Fact]
    public void Resolve_LegacyFogFallsBackToSingleColorAndFullOpacity()
    {
        var fog = new WeatherRgba(20, 30, 40, 255);
        var filler = new WeatherColor(fog, fog, fog, fog);
        var weather = new WeatherRecord
        {
            Colors = [filler, filler],
            FogDistances = [100f, 200f, 100f, 200f, 1f, 1f]
        };

        var resolved = AtmosphereState.Resolve(12f, weather, CleanTiming);
        Assert.Equal(resolved.FogColor, resolved.FogFarColor);
        Assert.Equal(1f, resolved.FogMaxOpacity);
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
            new WeatherRgba(50, 50, 50, 50), // sunrise
            new WeatherRgba(10, 20, 30, 200), // day
            new WeatherRgba(60, 60, 60, 60), // sunset
            new WeatherRgba(1, 2, 3, 40)); // night

        var noon = AtmosphereState.SampleCloudColor(c, 12f, CleanTiming);
        Assert.Equal(10f / 255f, noon.X, 3);
        Assert.Equal(20f / 255f, noon.Y, 3);
        Assert.Equal(30f / 255f, noon.Z, 3);
        Assert.Equal(200f / 255f, noon.W, 3); // alpha = layer opacity, NOT dropped

        var midnight = AtmosphereState.SampleCloudColor(c, 0f, CleanTiming);
        Assert.Equal(1f / 255f, midnight.X, 3);
        Assert.Equal(40f / 255f, midnight.W, 3);
    }

    // --- FNV optional fields --------------------------------------------------------------------------
    // High Noon is an authored daytime color. Midnight is retained by the parser but is not a color band;
    // the runtime holds the Night color outside the daylight span.

    [Fact]
    public void Resolve_SolarNoon_PicksAuthoredHighNoonPeak()
    {
        // Sky::Sky stores the fixed 12:00 color pivot independently of the climate windows.
        var w = WeatherWithAmbient6(
            new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255),
            new WeatherRgba(90, 120, 150, 255), new WeatherRgba(2, 3, 4, 255));
        var a = AtmosphereState.Resolve(12f, w, CleanTiming);
        AssertColor(90, 120, 150, a.AmbientColor);
    }

    [Fact]
    public void Resolve_ClassicHighNoonPeak_UsesFixedTwelve_NotDaylightMidpoint()
    {
        // Mojave-style 6/8/18/20 has a daylight midpoint of 13:00. Retail FillColorBlend still
        // reaches the authored HighNoon color at the fixed Sky member 12:00.
        var asymmetricTiming = new AtmosphereState.ClimateTiming(6f, 8f, 18f, 20f);
        var w = WeatherWithAmbient6(
            new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255),
            new WeatherRgba(90, 120, 150, 255), new WeatherRgba(2, 3, 4, 255));

        var atTwelve = AtmosphereState.Resolve(
            12f, w, asymmetricTiming, game: BethesdaGame.FalloutNewVegas);
        var atThirteen = AtmosphereState.Resolve(
            13f, w, asymmetricTiming, game: BethesdaGame.FalloutNewVegas);

        AssertColor(90, 120, 150, atTwelve.AmbientColor);
        Assert.NotEqual(atTwelve.AmbientColor, atThirteen.AmbientColor);
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
    public void Resolve_MidnightField_IsNotUsedAsAColorBand()
    {
        var w = WeatherWithAmbient6(
            new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(80, 80, 100, 255),
            new WeatherRgba(0, 0, 0, 0), new WeatherRgba(2, 2, 4, 255));

        var justBefore = AtmosphereState.Resolve(23.99f, w, CleanTiming).AmbientColor;
        var justAfter = AtmosphereState.Resolve(0.01f, w, CleanTiming).AmbientColor;
        AssertColor(80, 80, 100, justBefore);
        AssertColor(80, 80, 100, justAfter);
        var evening = AtmosphereState.Resolve(21.5f, w, CleanTiming).AmbientColor;
        AssertColor(80, 80, 100, evening);
    }

    [Fact]
    public void Resolve_ModernWeatherSamplesEverySunriseTransitionBand()
    {
        var zero = new WeatherRgba(0, 0, 0, 0);
        var colors = Enumerable.Repeat(new WeatherColor(zero, zero, zero, zero), 15).ToArray();
        colors[(int)WeatherColorType.Ambient] = new WeatherColor(new WeatherTimeBands<WeatherRgba>(
            new WeatherRgba(80, 0, 0, 255),
            new WeatherRgba(160, 0, 0, 255),
            new WeatherRgba(96, 0, 0, 255),
            zero)
        {
            EarlySunrise = new WeatherRgba(20, 0, 0, 255),
            LateSunrise = new WeatherRgba(120, 0, 0, 255),
            EarlySunset = new WeatherRgba(144, 0, 0, 255),
            LateSunset = new WeatherRgba(40, 0, 0, 255)
        });
        var weather = new WeatherRecord { Colors = colors };

        // FO4 retail GetBeginSunriseColors/GetEndSunsetColors extend the climate windows by 0.5h.
        // CleanTiming therefore samples the five sunrise keys over 4.5..7.0 and the five sunset keys
        // over 17.0..19.5. GetTimes subdivides each interval into four exactly equal segments.
        AssertColor(20, 0, 0,
            AtmosphereState.Resolve(5.125f, weather, CleanTiming, game: BethesdaGame.Fallout4).AmbientColor);
        AssertColor(80, 0, 0,
            AtmosphereState.Resolve(5.75f, weather, CleanTiming, game: BethesdaGame.Fallout4).AmbientColor);
        AssertColor(120, 0, 0,
            AtmosphereState.Resolve(6.375f, weather, CleanTiming, game: BethesdaGame.Fallout4).AmbientColor);
        AssertColor(144, 0, 0,
            AtmosphereState.Resolve(17.625f, weather, CleanTiming, game: BethesdaGame.Fallout4).AmbientColor);
        AssertColor(96, 0, 0,
            AtmosphereState.Resolve(18.25f, weather, CleanTiming, game: BethesdaGame.Fallout4).AmbientColor);
        AssertColor(40, 0, 0,
            AtmosphereState.Resolve(18.875f, weather, CleanTiming, game: BethesdaGame.Fallout4).AmbientColor);
    }

    [Fact]
    public void ModernCloudBands_UseTheSameExtendedFo4ColorWindow()
    {
        var zero = new WeatherRgba(0, 0, 0, 0);
        var color = new WeatherColor(new WeatherTimeBands<WeatherRgba>(
            new WeatherRgba(80, 0, 0, 255), new WeatherRgba(160, 0, 0, 255), zero, zero)
        {
            EarlySunrise = new WeatherRgba(20, 0, 0, 255),
            LateSunrise = new WeatherRgba(120, 0, 0, 255),
            EarlySunset = zero,
            LateSunset = zero
        });
        var alpha = new WeatherCloudAlpha(new WeatherTimeBands<float>(0.4f, 0.8f, 0f, 0f)
        {
            EarlySunrise = 0.1f,
            LateSunrise = 0.6f,
            EarlySunset = 0f,
            LateSunset = 0f
        });

        var tint = AtmosphereState.SampleCloudColor(
            color, 5.125f, CleanTiming, BethesdaGame.Fallout4);
        var opacity = AtmosphereState.SampleCloudAlpha(
            alpha, 5.125f, CleanTiming, BethesdaGame.Fallout4);

        Assert.Equal(20f / 255f, tint.X, 5);
        Assert.Equal(0.1f, opacity, 5);
    }

    [Fact]
    public void Resolve_ZeroHighNoon_IsStillAuthored_WhileMidnightIsNotAColorEndpoint()
    {
        // Retail FillColorBlend reads a present HighNoon slot even when every byte is zero. Midnight
        // remains stored metadata and never replaces the Night color outside the daylight span.
        var four = WeatherWithAmbient(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255));
        var six = WeatherWithAmbient6(new WeatherRgba(10, 12, 14, 255), new WeatherRgba(200, 210, 220, 255),
            new WeatherRgba(50, 40, 30, 255), new WeatherRgba(5, 5, 8, 255),
            new WeatherRgba(0, 0, 0, 0), new WeatherRgba(0, 0, 0, 0));

        AssertColor(200, 210, 220, AtmosphereState.Resolve(12f, four, CleanTiming).AmbientColor);
        AssertColor(0, 0, 0, AtmosphereState.Resolve(12f, six, CleanTiming).AmbientColor);
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

    // --- HorizonGlow: the warm-band gate must be continuous across the daylight edges -------------
    // The old gate read |sunDir.Z| from SunDirection, which returns (0,0,-1) the instant the hour
    // leaves [sunriseBegin, sunsetEnd] — so a capture at exactly sunset-end (hour 19 with the default
    // timing) showed NO warm band while 18:59 showed a full one. The glow must instead decay over a
    // civil-twilight window (~6° of solar descent) past the edge, and stay zero in deep night (the
    // Midnight junk-teal fold protection).

    [Fact]
    public void HorizonGlow_ContinuousAcrossSunsetEnd()
    {
        // Just inside daylight the sun is at the horizon (sunDirZ ~ 0) => glow ~ 1.
        var inside = AtmosphereState.HorizonGlow(18.99f, 5f, 19f, 0.01f);
        // Just past sunset-end the twilight decay has barely started => glow ~ 1, NOT 0.
        var outside = AtmosphereState.HorizonGlow(19.01f, 5f, 19f, -1f);

        Assert.True(inside > 0.9f, $"glow just before sunset-end should be near 1, got {inside}");
        Assert.True(outside > 0.9f, $"glow just after sunset-end should be near 1, got {outside}");
        Assert.True(MathF.Abs(inside - outside) < 0.1f,
            $"glow must not step across sunset-end (inside {inside} vs outside {outside})");
    }

    [Fact]
    public void HorizonGlow_FadesOverCivilTwilight_ThenStaysZeroAllNight()
    {
        // Timing (5, 19): 14h daylight => descent rate π·50°/14h => ~0.53h twilight window.
        var fifteenMinutesAfter = AtmosphereState.HorizonGlow(19.25f, 5f, 19f, -1f);
        var oneHourAfter = AtmosphereState.HorizonGlow(20f, 5f, 19f, -1f);
        var deepNight = AtmosphereState.HorizonGlow(1f, 5f, 19f, -1f);

        Assert.True(fifteenMinutesAfter is > 0.2f and < 0.8f,
            $"afterglow should be mid-decay 15 min past sunset-end, got {fifteenMinutesAfter}");
        Assert.Equal(0f, oneHourAfter);
        Assert.Equal(0f, deepNight);
    }

    [Fact]
    public void HorizonGlow_PreSunriseMirrorsPostSunset_AndWrapsMidnight()
    {
        // 15 min before sunrise-begin: same decay as 15 min past sunset-end (dawn glow).
        var beforeSunrise = AtmosphereState.HorizonGlow(4.75f, 5f, 19f, -1f);
        var afterSunset = AtmosphereState.HorizonGlow(19.25f, 5f, 19f, -1f);
        Assert.Equal(afterSunset, beforeSunrise, 3);

        // Midnight wrap: hour 0 is 5 hours past sunset AND 5 hours before sunrise — zero either way.
        Assert.Equal(0f, AtmosphereState.HorizonGlow(0f, 5f, 19f, -1f));
    }

    [Fact]
    public void HorizonGlow_NoonUnchanged()
    {
        // Inside daylight the gate is the sun's horizon proximity — high sun => no glow (the daytime
        // sky must be untouched). sin(50°) ≈ 0.766 is the apex sunDir.Z.
        Assert.Equal(0f, AtmosphereState.HorizonGlow(12f, 5f, 19f, 0.766f));
    }

    // --- FNV/FO3 engine sun path (both retail GMST sets: fSunXExtreme=800 / fSunYExtreme=−100) -------

    [Fact]
    public void FnvSunPath_NoonApex_MatchesEngineConstants()
    {
        // FNV climate windows 6/8/18/20 → padded day leg 6..20, x=0 (solar noon) at hour 13.
        // Apex = atan(800/100) ≈ 83°: sunDir.Z = 800/√(800²+100²) ≈ 0.9923, Y on the −south side.
        var dir = AtmosphereState.FnvSunPathDirection(13f, 6f, 8f, 18f, 20f);
        Assert.Equal(0.9923f, dir.Z, 3);
        Assert.True(dir.Y < 0f, "engine fSunYExtreme is −100 — the sun sits slightly south of zenith");
        Assert.Equal(0f, dir.X, 3);
    }

    [Fact]
    public void FnvSunPath_NightKeepsBelowHorizonConvention()
    {
        // Outside the daylight span the analytic (0,0,−1) convention is preserved — the horizon-glow
        // midnight gating depends on |sunDir.Z| being 1 at night.
        Assert.Equal(new Vector3(0f, 0f, -1f),
            AtmosphereState.FnvSunPathDirection(2f, 6f, 8f, 18f, 20f));
    }

    [Fact]
    public void Resolve_FalloutNewVegas_UsesEngineTriangleWaveAtNoon()
    {
        var fnv = AtmosphereState.Resolve(13f, climate: FnvTiming(), game: BethesdaGame.FalloutNewVegas);
        var generic = AtmosphereState.Resolve(13f, climate: FnvTiming());

        Assert.True(fnv.SunWorldDirection.Z > 0.98f, "FNV noon apex ≈ 83° (engine GMSTs)");
        Assert.True(generic.SunWorldDirection.Z < 0.85f, "non-FNV games keep the analytic 50° arc");
    }

    private static AtmosphereState.ClimateTiming FnvTiming()
    {
        return AtmosphereState.ClimateTiming.FromClimateData(
            new ClimateTimingData(36, 48, 108, 120, 0, 0x83));
    }

    // --- Interior XCLL/LGTM lighting (time-of-day independent) ---------------------------------

    // GSProspectorSaloonInterior's real XCLL (FalloutNV.esm 0x00106185): packed colors are
    // R | G<<8 | B<<16.
    private static Dictionary<string, object?> SaloonXcll()
    {
        return new Dictionary<string, object?>
        {
            ["AmbientColor"] = (uint)(30 | (41 << 8) | (77 << 16)),
            ["DirectionalColor"] = (uint)(26 | (32 << 8) | (49 << 16)),
            ["FogColor"] = (uint)(55 | (55 << 8) | (94 << 16)),
            ["FogNear"] = 64f,
            ["FogFar"] = 3750f,
            ["DirectionalRotationXY"] = 0,
            ["DirectionalRotationZ"] = 250,
            ["DirectionalFade"] = 1.0f,
            ["FogClipDistance"] = 6600f,
            ["FogPow"] = 1.25f
        };
    }

    [Fact]
    public void ResolveInterior_UsesAuthoredXcll_TimeIndependent()
    {
        var r = AtmosphereState.ResolveInterior(SaloonXcll(), null);

        Assert.Equal(30 / 255f, r.AmbientColor.X, 3);
        Assert.Equal(41 / 255f, r.AmbientColor.Y, 3);
        Assert.Equal(77 / 255f, r.AmbientColor.Z, 3);
        Assert.Equal(26 / 255f, r.SunColor.X, 3); // directional = the interior "sun"
        Assert.Equal(64f, r.FogNear);
        Assert.Equal(3750f, r.FogFar);
        Assert.Equal(1.25f, r.FogPower, 3);
        Assert.Equal(1f, r.SunIntensity); // never fades with game hour
        Assert.Equal(r.FogColor, r.SkyTopColor); // no sky: fog doubles as backdrop
    }

    [Fact]
    public void ResolveInterior_FallsBackToTemplate_AndHonorsInheritBits()
    {
        var template = new Dictionary<string, object?>
        {
            ["AmbientColor"] = (uint)(60 | (60 << 8) | (60 << 16)),
            ["FogPower"] = 1.5f // LGTM DATA spelling (vs XCLL "FogPow") — the dual-key trap
        };

        // No XCLL at all → template values drive.
        var fromTemplate = AtmosphereState.ResolveInterior(null, template);
        Assert.Equal(60 / 255f, fromTemplate.AmbientColor.X, 3);
        Assert.Equal(1.5f, fromTemplate.FogPower, 3);

        // XCLL present but ambient inherit bit (0x01) set → template ambient wins, XCLL fog stays.
        var inherited = AtmosphereState.ResolveInterior(SaloonXcll(), template, 0x01);
        Assert.Equal(60 / 255f, inherited.AmbientColor.X, 3);
        Assert.Equal(64f, inherited.FogNear);

        // Inherit bits authored on a template-LESS cell (Saloon ships LNAM=0x9F, LTMP=NULL) must
        // still resolve from the XCLL, not to defaults.
        var noTemplate = AtmosphereState.ResolveInterior(SaloonXcll(), null, 0x9F);
        Assert.Equal(30 / 255f, noTemplate.AmbientColor.X, 3);
    }

    [Theory]
    [InlineData(5.0f, AtmosphereState.WeatherBandKind.Night,
        AtmosphereState.WeatherBandKind.EarlySunrise, 0f)]
    [InlineData(5.5f, AtmosphereState.WeatherBandKind.EarlySunrise,
        AtmosphereState.WeatherBandKind.Sunrise, 0f)]
    [InlineData(6.0f, AtmosphereState.WeatherBandKind.Sunrise,
        AtmosphereState.WeatherBandKind.LateSunrise, 0f)]
    [InlineData(6.5f, AtmosphereState.WeatherBandKind.LateSunrise,
        AtmosphereState.WeatherBandKind.Day, 0f)]
    [InlineData(18.5f, AtmosphereState.WeatherBandKind.LateSunset,
        AtmosphereState.WeatherBandKind.Night, 0f)]
    public void SelectWeatherBandBlend_ReportsModernAuthoredSegments(
        float hour,
        AtmosphereState.WeatherBandKind expectedFrom,
        AtmosphereState.WeatherBandKind expectedTo,
        float expectedWeight)
    {
        // FO76 intentionally uses the authored 5/7/17/19 windows without FO4's recovered ±0.5h
        // extension. Quarter-window vectors therefore land exactly on each five-band segment edge.
        var blend = AtmosphereState.SelectWeatherBandBlend(
            hour, CleanTiming, BethesdaGame.Fallout76,
            true, false);

        Assert.Equal(expectedFrom, blend.From);
        Assert.Equal(expectedTo, blend.To);
        Assert.Equal(expectedWeight, blend.ToWeight, 6);
    }

    [Theory]
    [InlineData(9.5f, AtmosphereState.WeatherBandKind.Day,
        AtmosphereState.WeatherBandKind.HighNoon, 0.5f)]
    [InlineData(14.5f, AtmosphereState.WeatherBandKind.HighNoon,
        AtmosphereState.WeatherBandKind.Day, 0.5f)]
    [InlineData(2f, AtmosphereState.WeatherBandKind.Night,
        AtmosphereState.WeatherBandKind.Night, 0f)]
    public void SelectWeatherBandBlend_ReportsClassicHighNoonWithoutUsingMidnight(
        float hour,
        AtmosphereState.WeatherBandKind expectedFrom,
        AtmosphereState.WeatherBandKind expectedTo,
        float expectedWeight)
    {
        var blend = AtmosphereState.SelectWeatherBandBlend(
            hour, CleanTiming, BethesdaGame.FalloutNewVegas,
            false, true);

        Assert.Equal(expectedFrom, blend.From);
        Assert.Equal(expectedTo, blend.To);
        Assert.Equal(expectedWeight, blend.ToWeight, 6);
    }

    [Fact]
    public void SelectWeatherBandBlend_ClassicHighNoonPeaksAtFixedTwelve()
    {
        var asymmetricTiming = new AtmosphereState.ClimateTiming(6f, 8f, 18f, 20f);

        var blend = AtmosphereState.SelectWeatherBandBlend(
            12f, asymmetricTiming, BethesdaGame.FalloutNewVegas,
            false, true);

        Assert.Equal(AtmosphereState.WeatherBandKind.Day, blend.From);
        Assert.Equal(AtmosphereState.WeatherBandKind.HighNoon, blend.To);
        Assert.Equal(1f, blend.ToWeight, 6);
    }
}
