using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class WeatherImageSpaceEvaluatorTests
{
    private static readonly AtmosphereState.ClimateTiming Timing = new(6f, 8f, 18f, 20f);

    [Fact]
    public void RecoveredComposition_UsesWeightedAbsoluteMultiplyThenAdd()
    {
        var modifier = Modifier(0x100, ImageSpaceModifierParameter.EyeAdaptSpeed,
            [new ImageSpaceModifierFloatKey(0f, 2f)], [new ImageSpaceModifierFloatKey(0f, 3f)]);
        var weather = Weather(0x200, 0x100, 0x100, 0x100, 0x100,
            0x100, 0xDEADBEEF);
        var baseSettings = GpuTonemapSettings.EngineExteriorDefaults with { EyeAdaptSpeed = 10f };

        var result = WeatherImageSpaceEvaluator.Evaluate(baseSettings, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [modifier.FormId] = modifier });

        // Independent recovered oracle: 10 * (1 * 2) + (1 * 3) = 23.
        Assert.Equal(23f, result.Settings.EyeAdaptSpeed, 6);
        Assert.Single(result.Contributions);
        Assert.Equal(WeatherImageSpaceBand.Night, result.Contributions[0].Band);
        Assert.DoesNotContain(result.Contributions, c => c.ModifierFormId == 0xDEADBEEF);
    }

    [Fact]
    public void RecoveredCinematicOrdinals_MapPivotContrastAndBrightnessWithoutAlias()
    {
        // Recovered TESImageSpaceModifier enum/PDB order is saturation=17, contrast pivot=18,
        // contrast=19, brightness=20. FO3 WastelandDayISFX is the retail sentinel: ordinal 18
        // begins at zero while ordinal 20 adds 1.1, so aliasing 18 to brightness blackens noon.
        Assert.Equal(17, (int)ImageSpaceModifierParameter.CinematicSaturation);
        Assert.Equal(18, (int)ImageSpaceModifierParameter.CinematicContrastAvgLum);
        Assert.Equal(19, (int)ImageSpaceModifierParameter.CinematicContrast);
        Assert.Equal(20, (int)ImageSpaceModifierParameter.CinematicBrightness);

        var timelines = Enumerable.Range(0, 21)
            .Select(i => new ImageSpaceModifierParameterTimeline((ImageSpaceModifierParameter)i, [], []))
            .ToArray();
        timelines[18] = new ImageSpaceModifierParameterTimeline(ImageSpaceModifierParameter.CinematicContrastAvgLum,
            [new ImageSpaceModifierFloatKey(0f, 2f)], [new ImageSpaceModifierFloatKey(0f, 0.1f)]);
        timelines[19] = new ImageSpaceModifierParameterTimeline(ImageSpaceModifierParameter.CinematicContrast,
            [new ImageSpaceModifierFloatKey(0f, 3f)], [new ImageSpaceModifierFloatKey(0f, 0.2f)]);
        timelines[20] = new ImageSpaceModifierParameterTimeline(ImageSpaceModifierParameter.CinematicBrightness,
            [new ImageSpaceModifierFloatKey(0f, 4f)], [new ImageSpaceModifierFloatKey(0f, 0.3f)]);
        var modifier = new ImageSpaceModifierRecord { FormId = 0x100, Parameters = timelines };
        var weather = Weather(0x200, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var basis = GpuTonemapSettings.EngineExteriorDefaults with
        {
            ContrastAvgLum = 0.25f,
            Contrast = 0.5f,
            Brightness = 0.75f
        };

        var result = WeatherImageSpaceEvaluator.Evaluate(basis, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [modifier.FormId] = modifier });

        Assert.Equal(0.6f, result.Settings.ContrastAvgLum, 6); // .25*2 + .1
        Assert.Equal(1.7f, result.Settings.Contrast, 6); // .5*3 + .2
        Assert.Equal(3.3f, result.Settings.Brightness, 6); // .75*4 + .3
    }

    [Fact]
    public void RecoveredHdrOrdinals_RouteEmissiveBrightScaleAndClamp_NotSkinDimmer()
    {
        var timelines = Enumerable.Range(0, 21)
            .Select(i => new ImageSpaceModifierParameterTimeline((ImageSpaceModifierParameter)i, [], []))
            .ToArray();
        // Slot 2 is SkinDimmer and is intentionally not a display-tonemap field. A zero sentinel here
        // must not overwrite BrightClamp (the old ordinal alias did exactly that).
        timelines[2] = new ImageSpaceModifierParameterTimeline(ImageSpaceModifierParameter.HdrSkinDimmer,
            [new ImageSpaceModifierFloatKey(0f, 0f)], [new ImageSpaceModifierFloatKey(0f, 0f)]);
        timelines[3] = new ImageSpaceModifierParameterTimeline(ImageSpaceModifierParameter.HdrEmissiveMult,
            [new ImageSpaceModifierFloatKey(0f, 2f)], [new ImageSpaceModifierFloatKey(0f, 1f)]);
        timelines[6] = new ImageSpaceModifierParameterTimeline(ImageSpaceModifierParameter.HdrBrightScale,
            [new ImageSpaceModifierFloatKey(0f, 3f)], [new ImageSpaceModifierFloatKey(0f, 2f)]);
        timelines[7] = new ImageSpaceModifierParameterTimeline(ImageSpaceModifierParameter.HdrBrightClamp,
            [new ImageSpaceModifierFloatKey(0f, 4f)], [new ImageSpaceModifierFloatKey(0f, 3f)]);
        var modifier = new ImageSpaceModifierRecord { FormId = 0x100, Parameters = timelines };
        var weather = Weather(0x200, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var basis = GpuTonemapSettings.EngineExteriorDefaults with
        {
            EmissiveMult = 1f,
            BrightScale = 2f,
            BrightClamp = 0.5f
        };

        var result = WeatherImageSpaceEvaluator.Evaluate(basis, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [modifier.FormId] = modifier });

        Assert.Equal(3f, result.Settings.EmissiveMult, 6); // 1*2 + 1
        Assert.Equal(8f, result.Settings.BrightScale, 6); // 2*3 + 2
        Assert.Equal(5f, result.Settings.BrightClamp, 6); // .5*4 + 3, not slot-2 zero
    }

    [Fact]
    public void RecoveredSunlightDimmer_RoutesClassicSlot11ToDirectionalSceneScale()
    {
        var modifier = Modifier(0x100, ImageSpaceModifierParameter.HdrSunlightDimmer,
            [new ImageSpaceModifierFloatKey(0f, 1.1f)], [new ImageSpaceModifierFloatKey(0f, 0f)]);
        var weather = Weather(0x200, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var basis = GpuTonemapSettings.EngineExteriorDefaults with { SunlightScale = 1.1f };

        var result = WeatherImageSpaceEvaluator.Evaluate(basis, 12f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [modifier.FormId] = modifier });

        // Retail Wasteland contract: NVDefaultExterior 1.1 * NVWastelandIS 1.1 = 1.21.
        Assert.Equal(1.21f, result.Settings.SunlightScale, 6);
        Assert.Contains("scene(sun=1.21[1.1+0]", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void WeatherTransition_ProducesTwoBandsTimesTwoWeathers()
    {
        var current = Weather(1, 10, 11, 12, 13, 14, 15);
        var outgoing = Weather(2, 20, 21, 22, 23, 24, 25);

        // 06:30 is halfway from Night to the 07:00 Sunrise peak. Current/outgoing = 0.6/0.4.
        var result = WeatherImageSpaceEvaluator.Evaluate(GpuTonemapSettings.EngineExteriorDefaults,
            6.5f, Timing, current, outgoing, 0.6f, new Dictionary<uint, ImageSpaceModifierRecord>());

        Assert.Equal(4, result.Contributions.Count);
        Assert.Equal(1f, result.Contributions.Sum(c => c.Weight), 6);
        Assert.Contains(result.Contributions, c => c.WeatherFormId == 1 && c.Band == WeatherImageSpaceBand.Sunrise
                                                                        && Math.Abs(c.Weight - 0.3f) < 1e-6f);
        Assert.Contains(result.Contributions, c => c.WeatherFormId == 1 && c.Band == WeatherImageSpaceBand.Night
                                                                        && Math.Abs(c.Weight - 0.3f) < 1e-6f);
        Assert.Contains(result.Contributions, c => c.WeatherFormId == 2 && c.Band == WeatherImageSpaceBand.Sunrise
                                                                        && Math.Abs(c.Weight - 0.2f) < 1e-6f);
        Assert.Contains(result.Contributions, c => c.WeatherFormId == 2 && c.Band == WeatherImageSpaceBand.Night
                                                                        && Math.Abs(c.Weight - 0.2f) < 1e-6f);
    }

    [Fact]
    public void DayPeaksAtNoon_HighNoonOccupiesShoulders_AndMidnightNeverIsSelected()
    {
        var weather = Weather(1, 10, 11, 12, 13, 14, 15);
        var noon = WeatherImageSpaceEvaluator.Evaluate(GpuTonemapSettings.EngineExteriorDefaults,
            12f, Timing, weather, null, 1f, new Dictionary<uint, ImageSpaceModifierRecord>());
        var morningShoulder = WeatherImageSpaceEvaluator.Evaluate(GpuTonemapSettings.EngineExteriorDefaults,
            10f, Timing, weather, null, 1f, new Dictionary<uint, ImageSpaceModifierRecord>());
        var afternoonShoulder = WeatherImageSpaceEvaluator.Evaluate(GpuTonemapSettings.EngineExteriorDefaults,
            15f, Timing, weather, null, 1f, new Dictionary<uint, ImageSpaceModifierRecord>());
        var sunsetBegin = WeatherImageSpaceEvaluator.Evaluate(GpuTonemapSettings.EngineExteriorDefaults,
            18f, Timing, weather, null, 1f, new Dictionary<uint, ImageSpaceModifierRecord>());
        var midnight = WeatherImageSpaceEvaluator.Evaluate(GpuTonemapSettings.EngineExteriorDefaults,
            0f, Timing, weather, null, 1f, new Dictionary<uint, ImageSpaceModifierRecord>());

        Assert.Single(noon.Contributions);
        Assert.Equal(WeatherImageSpaceBand.Day, noon.Contributions[0].Band);
        Assert.Equal(11u, noon.Contributions[0].ModifierFormId);
        Assert.Contains(morningShoulder.Contributions,
            c => c.Band == WeatherImageSpaceBand.Day && Math.Abs(c.Weight - 0.5f) < 1e-6f);
        Assert.Contains(morningShoulder.Contributions,
            c => c.Band == WeatherImageSpaceBand.HighNoon && Math.Abs(c.Weight - 0.5f) < 1e-6f);
        Assert.Contains(afternoonShoulder.Contributions,
            c => c.Band == WeatherImageSpaceBand.Day && Math.Abs(c.Weight - 0.5f) < 1e-6f);
        Assert.Contains(afternoonShoulder.Contributions,
            c => c.Band == WeatherImageSpaceBand.HighNoon && Math.Abs(c.Weight - 0.5f) < 1e-6f);
        Assert.Single(sunsetBegin.Contributions);
        Assert.Equal(WeatherImageSpaceBand.Day, sunsetBegin.Contributions[0].Band);
        Assert.Equal(11u, sunsetBegin.Contributions[0].ModifierFormId);
        Assert.Single(midnight.Contributions);
        Assert.Equal(13u, midnight.Contributions[0].ModifierFormId);
        Assert.DoesNotContain(midnight.Contributions, c => c.ModifierFormId == 15);
    }

    [Fact]
    public void AbsentHighNoon_FallsBackToDayInsteadOfNeutralModifier()
    {
        var weather = new WeatherRecord
        {
            FormId = 1,
            // FO3 authors only these four bands. HighNoon must remain absent (null), not an
            // authored zero FormID, so the classic daytime pivot retains the Day modifier.
            ImageSpaceModifiers = new WeatherTimeBands<uint>(10, 11, 12, 13)
        };

        var shoulder = WeatherImageSpaceEvaluator.Evaluate(GpuTonemapSettings.EngineExteriorDefaults,
            10f, Timing, weather, null, 1f, new Dictionary<uint, ImageSpaceModifierRecord>());

        Assert.Equal(2, shoulder.Contributions.Count);
        Assert.Contains(shoulder.Contributions,
            c => c.Band == WeatherImageSpaceBand.HighNoon && c.ModifierFormId == 11u);
        Assert.Contains(shoulder.Contributions,
            c => c.Band == WeatherImageSpaceBand.Day && c.ModifierFormId == 11u);
        Assert.Equal(1f, shoulder.Contributions.Sum(c => c.Weight), 6);
    }

    [Fact]
    public void AnimatableTimeline_InterpolatesIndependentlyAtElapsedTime()
    {
        var modifier = Modifier(0x100, ImageSpaceModifierParameter.HdrBrightScale,
            [new ImageSpaceModifierFloatKey(0f, 1f), new ImageSpaceModifierFloatKey(1f, 3f)],
            [new ImageSpaceModifierFloatKey(0f, 0f), new ImageSpaceModifierFloatKey(1f, 4f)],
            true, 4f);
        var weather = Weather(1, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var baseSettings = GpuTonemapSettings.EngineExteriorDefaults with { BrightScale = 2f };

        var result = WeatherImageSpaceEvaluator.Evaluate(baseSettings, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [0x100] = modifier },
            1f);

        // t=0.25: mult=1.5, add=1.0 => 2*1.5+1 = 4.
        Assert.Equal(4f, result.Settings.BrightScale, 6);
        Assert.Equal(0.25f, result.Contributions[0].TimelineTime!.Value, 6);
    }

    [Fact]
    public void AnimatableTimeline_WithUnknownElapsedClock_IsExplicitlyNeutral()
    {
        var modifier = Modifier(0x100, ImageSpaceModifierParameter.HdrBrightScale,
                [new ImageSpaceModifierFloatKey(0f, 9f), new ImageSpaceModifierFloatKey(1f, 11f)],
                [new ImageSpaceModifierFloatKey(0f, 7f), new ImageSpaceModifierFloatKey(1f, 8f)],
                true, 4f) with
            {
                TintColorTimeline =
                [
                    new ImageSpaceModifierColorKey(0f, 1f, 0f, 0f, 0.25f),
                    new ImageSpaceModifierColorKey(1f, 0f, 1f, 0f, 0.75f)
                ]
            };
        var weather = Weather(1, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var baseSettings = GpuTonemapSettings.EngineExteriorDefaults with
        {
            BrightScale = 2f,
            TintR = 0.2f,
            TintG = 0.3f,
            TintB = 0.4f,
            TintAmount = 0.5f
        };

        var result = WeatherImageSpaceEvaluator.Evaluate(baseSettings, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [0x100] = modifier });

        Assert.Equal(2f, result.Settings.BrightScale, 6);
        Assert.Equal(0.2f, result.Settings.TintR, 6);
        Assert.Equal(0.3f, result.Settings.TintG, 6);
        Assert.Equal(0.4f, result.Settings.TintB, 6);
        Assert.Equal(0.5f, result.Settings.TintAmount, 6);
        Assert.Null(result.Contributions[0].TimelineTime);
        Assert.Contains("[t=unknown]", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void AnimatableTimeline_WithUnknownElapsedClock_RetainsTimeInvariantScalarCurves()
    {
        var modifier = Modifier(0x100, ImageSpaceModifierParameter.HdrBrightScale,
            [new ImageSpaceModifierFloatKey(0f, 3f)],
            [
                new ImageSpaceModifierFloatKey(0f, 4f), new ImageSpaceModifierFloatKey(0.4f, 4f),
                new ImageSpaceModifierFloatKey(1f, 4f)
            ],
            true, 4f);
        var weather = Weather(1, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var baseSettings = GpuTonemapSettings.EngineExteriorDefaults with { BrightScale = 2f };

        var result = WeatherImageSpaceEvaluator.Evaluate(baseSettings, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [0x100] = modifier });

        // Both authored curves are independent of time: 2*3+4 = 10.
        Assert.Equal(10f, result.Settings.BrightScale, 6);
        Assert.Equal(new WeatherImageSpaceChannel(3f, 4f),
            result.Channels[ImageSpaceModifierParameter.HdrBrightScale]);
    }

    [Fact]
    public void AnimatableTimeline_WithUnknownElapsedClock_NeutralizesOnlyVaryingScalarComponent()
    {
        var modifier = Modifier(0x100, ImageSpaceModifierParameter.HdrBrightScale,
            [new ImageSpaceModifierFloatKey(0f, 3f), new ImageSpaceModifierFloatKey(1f, 3f)],
            [new ImageSpaceModifierFloatKey(0f, 4f), new ImageSpaceModifierFloatKey(1f, 8f)],
            true, 4f);
        var weather = Weather(1, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var baseSettings = GpuTonemapSettings.EngineExteriorDefaults with { BrightScale = 2f };

        var result = WeatherImageSpaceEvaluator.Evaluate(baseSettings, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [0x100] = modifier });

        // Multiply is provably 3 at every time; the varying add curve contributes neutral zero.
        Assert.Equal(6f, result.Settings.BrightScale, 6);
        Assert.Equal(new WeatherImageSpaceChannel(3f, 0f),
            result.Channels[ImageSpaceModifierParameter.HdrBrightScale]);
    }

    [Fact]
    public void AnimatableTimeline_WithUnknownElapsedClock_RetainsTimeInvariantTint()
    {
        var modifier = Modifier(0x100, ImageSpaceModifierParameter.EyeAdaptSpeed, [], [],
                true, 4f) with
            {
                TintColorTimeline =
                [
                    new ImageSpaceModifierColorKey(0f, 1f, 0f, 0f, 0.5f),
                    new ImageSpaceModifierColorKey(1f, 1f, 0f, 0f, 0.5f)
                ]
            };
        var weather = Weather(1, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var settings = GpuTonemapSettings.EngineExteriorDefaults with
        {
            TintR = 0f, TintG = 0f, TintB = 1f, TintAmount = 0.25f
        };

        var result = WeatherImageSpaceEvaluator.Evaluate(settings, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [0x100] = modifier });

        // Independent premultiplied oracle: ((red*.5) + (blue*.25)) / .75.
        Assert.Equal(2f / 3f, result.Settings.TintR, 6);
        Assert.Equal(0f, result.Settings.TintG, 6);
        Assert.Equal(1f / 3f, result.Settings.TintB, 6);
        Assert.Equal(0.5f, result.Settings.TintAmount, 6);
    }

    [Fact]
    public void Tint_ComposesLikeRecoveredManagerPremultiplyAndMaxAlpha()
    {
        var modifier = Modifier(0x100, ImageSpaceModifierParameter.EyeAdaptSpeed, [], []) with
        {
            TintColorTimeline = [new ImageSpaceModifierColorKey(0f, 1f, 0f, 0f, 0.5f)]
        };
        var weather = Weather(1, 0x100, 0x100, 0x100, 0x100, 0x100, 0);
        var settings = GpuTonemapSettings.EngineExteriorDefaults with
        {
            TintR = 0f, TintG = 0f, TintB = 1f, TintAmount = 0.25f
        };

        var result = WeatherImageSpaceEvaluator.Evaluate(settings, 2f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord> { [0x100] = modifier });

        // Independent manager oracle: (base.rgb*.25 + weather.rgb*.5) / .75; alpha=max(.25,.5).
        Assert.Equal(2f / 3f, result.Settings.TintR, 6);
        Assert.Equal(0f, result.Settings.TintG, 6);
        Assert.Equal(1f / 3f, result.Settings.TintB, 6);
        Assert.Equal(0.5f, result.Settings.TintAmount, 6);
        Assert.Contains("bands=[", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void Tint_PremultipliesAggregateWeatherColorAfterBandAccumulation()
    {
        var sunrise = Modifier(0x101, ImageSpaceModifierParameter.EyeAdaptSpeed, [], []) with
        {
            TintColorTimeline = [new ImageSpaceModifierColorKey(0f, 1f, 0f, 0f, 1f)]
        };
        var night = Modifier(0x102, ImageSpaceModifierParameter.EyeAdaptSpeed, [], []) with
        {
            TintColorTimeline = [new ImageSpaceModifierColorKey(0f, 0f, 1f, 0f, 0.25f)]
        };
        var weather = Weather(1, 0x101, 0, 0, 0x102, 0, 0);
        var settings = GpuTonemapSettings.EngineExteriorDefaults with
        {
            TintR = 0f, TintG = 0f, TintB = 1f, TintAmount = 0.25f
        };

        // At 06:30 Sunrise and Night each have weight 0.5. ApplyWeather first accumulates raw
        // weather RGBA=(.5,.5,0,.625), then the manager premultiplies that aggregate once:
        // weather=(.3125,.3125,0), base=(0,0,.25), denominator=.875.
        var result = WeatherImageSpaceEvaluator.Evaluate(settings, 6.5f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceModifierRecord>
            {
                [sunrise.FormId] = sunrise,
                [night.FormId] = night
            });

        Assert.Equal(5f / 14f, result.Settings.TintR, 6);
        Assert.Equal(5f / 14f, result.Settings.TintG, 6);
        Assert.Equal(2f / 7f, result.Settings.TintB, 6);
        Assert.Equal(0.625f, result.Settings.TintAmount, 6);
    }

    [Fact]
    public void ScalarSampler_ClampsAndLinearlyInterpolatesWithoutProductionExpectedValues()
    {
        ImageSpaceModifierFloatKey[] keys = [new(0f, 2f), new(0.5f, 4f), new(1f, 10f)];
        Assert.Equal(2f, WeatherImageSpaceEvaluator.Sample(keys, -1f, 99f));
        Assert.Equal(3f, WeatherImageSpaceEvaluator.Sample(keys, 0.25f, 99f), 6);
        Assert.Equal(7f, WeatherImageSpaceEvaluator.Sample(keys, 0.75f, 99f), 6);
        Assert.Equal(10f, WeatherImageSpaceEvaluator.Sample(keys, 2f, 99f));
        Assert.Equal(99f, WeatherImageSpaceEvaluator.Sample([], 0.5f, 99f));
    }

    [Fact]
    public void ModernEightBandSchedule_BlendsAdjacentAuthoredImageSpaces()
    {
        var weather = new WeatherRecord
        {
            FormId = 0x200,
            ImageSpaceModifiers = new WeatherTimeBands<uint>(10, 11, 12, 13)
            {
                EarlySunrise = 14,
                LateSunrise = 15,
                EarlySunset = 16,
                LateSunset = 17
            }
        };
        var spaces = Enumerable.Range(10, 8).ToDictionary(i => (uint)i, i => ModernSpace((uint)i, i));
        var basis = GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4);

        // Sunrise window 6..8 is split into four equal segments. 06:25 is halfway Night(13)
        // -> EarlySunrise(14), so the recovered WeightedAdd result is 13.5.
        var result = WeatherImageSpaceEvaluator.EvaluateModern(
            basis, 6.25f, Timing, weather, null, 1f, spaces);

        Assert.Equal(2, result.Contributions.Count);
        Assert.Equal(13.5f, result.Settings.SunlightScale, 6);
        Assert.Contains(result.Contributions, c => c.Band == WeatherImageSpaceBand.Night
                                                   && Math.Abs(c.Weight - 0.5f) < 1e-6f);
        Assert.Contains(result.Contributions, c => c.Band == WeatherImageSpaceBand.EarlySunrise
                                                   && Math.Abs(c.Weight - 0.5f) < 1e-6f);
        Assert.Contains("retained-only", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void ModernFourBandSchedule_DoesNotInventHighNoon()
    {
        var weather = new WeatherRecord
        {
            FormId = 0x200,
            ImageSpaceModifiers = new WeatherTimeBands<uint>(10, 11, 12, 13)
        };
        var spaces = Enumerable.Range(10, 4).ToDictionary(i => (uint)i, i => ModernSpace((uint)i, i));

        var result = WeatherImageSpaceEvaluator.EvaluateModern(
            GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4),
            12f, Timing, weather, null, 1f, spaces);

        Assert.Single(result.Contributions);
        Assert.Equal(WeatherImageSpaceBand.Day, result.Contributions[0].Band);
        Assert.Equal(11u, result.Contributions[0].ModifierFormId);
        Assert.DoesNotContain(result.Contributions, c => c.Band == WeatherImageSpaceBand.HighNoon);
    }

    [Fact]
    public void ModernEvaluation_IsSemanticAndPreservesGammaAcesDisplayMode()
    {
        var weather = new WeatherRecord
        {
            FormId = 0x200,
            ImageSpaceModifiers = new WeatherTimeBands<uint>(10, 11, 12, 13)
        };
        var day = new ImageSpaceRecord
        {
            FormId = 11,
            ModernHdr = new ImageSpaceModernHdr
            {
                Family = ImageSpaceModernFamily.Skyrim,
                EyeAdaptSpeed = 45f,
                BloomBlurRadius = 7f,
                BloomThreshold = 0.65f,
                BloomScale = 4f,
                ReceiveBloomThreshold = 0.625f,
                White = 1f,
                SunlightScale = 2.7f,
                SkyScale = 0.235f,
                EyeAdaptStrength = 5f
            },
            Cinematic = new ImageSpaceCinematic
            {
                Saturation = 0.8f,
                Brightness = 1.035f,
                Contrast = 1.25f
            },
            Tint = new ImageSpaceTint
            {
                Amount = 0.42f,
                Red = 0.894118f,
                Green = 0.839216f,
                Blue = 0.772549f
            }
        };
        var basis = GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Skyrim) with
        {
            Mode = GpuTonemapMode.GammaAces
        };

        var result = WeatherImageSpaceEvaluator.EvaluateModern(
            basis, 12f, Timing, weather, null, 1f,
            new Dictionary<uint, ImageSpaceRecord> { [day.FormId] = day });

        Assert.Equal(GpuTonemapMode.GammaAces, result.Settings.Mode);
        Assert.Equal(ImageSpaceModernFamily.Skyrim, result.Settings.ModernFamily);
        Assert.Equal(2.7f, result.Settings.SunlightScale);
        Assert.Equal(0.235f, result.Settings.SkyScale);
        Assert.Equal(0.8f, result.Settings.Saturation);
        Assert.Equal(1.035f, result.Settings.Brightness);
        Assert.Equal(1.25f, result.Settings.Contrast);
        Assert.Single(result.Contributions);
        Assert.Equal(WeatherImageSpaceBand.Day, result.Contributions[0].Band);
    }

    [Fact]
    public void ModernChannelMapping_UsesFamilySpecificOrdinalsWithoutClassicAliases()
    {
        var channels = Enumerable.Range(0, 21).ToDictionary(
            i => (ImageSpaceModifierParameter)i, _ => new WeatherImageSpaceChannel(1f, 0f));
        channels[ImageSpaceModifierParameter.HdrBlurRadius] = new WeatherImageSpaceChannel(2f, 1f);
        channels[ImageSpaceModifierParameter.HdrTargetLum] = new WeatherImageSpaceChannel(3f, 2f);
        channels[ImageSpaceModifierParameter.HdrLumRampNoTex] = new WeatherImageSpaceChannel(4f, 3f);

        var fo4 = WeatherImageSpaceEvaluator.ApplyModernChannels(
            GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Fallout4) with
            {
                TonemapE = 5f, AutoExposureMax = 7f, MiddleGray = 11f
            }, channels);
        Assert.Equal(11f, fo4.TonemapE); // 5*2+1
        Assert.Equal(23f, fo4.AutoExposureMax); // 7*3+2
        Assert.Equal(47f, fo4.MiddleGray); // 11*4+3

        var skyrim = WeatherImageSpaceEvaluator.ApplyModernChannels(
            GpuTonemapSettings.ModernNeutralDefaults(ImageSpaceModernFamily.Skyrim) with
            {
                BlurRadius = 5f, ReceiveBloomThreshold = 7f, EyeAdaptStrength = 11f
            }, channels);
        Assert.Equal(11f, skyrim.BlurRadius);
        Assert.Equal(23f, skyrim.ReceiveBloomThreshold);
        Assert.Equal(47f, skyrim.EyeAdaptStrength);
    }

    private static ImageSpaceRecord ModernSpace(uint formId, float value)
    {
        return new ImageSpaceRecord
        {
            FormId = formId,
            ModernHdr = new ImageSpaceModernHdr
            {
                Family = ImageSpaceModernFamily.Fallout4,
                EyeAdaptSpeed = value,
                TonemapE = value,
                BloomThreshold = value,
                BloomScale = value,
                AutoExposureMax = value,
                AutoExposureMin = value,
                SunlightScale = value,
                SkyScale = value,
                MiddleGray = value
            },
            Cinematic = new ImageSpaceCinematic { Saturation = value, Brightness = value, Contrast = value },
            Tint = new ImageSpaceTint { Amount = value, Red = value, Green = value, Blue = value },
            LutTexturePath = $"textures\\lut{formId}.dds"
        };
    }

    private static WeatherRecord Weather(
        uint formId, uint sunrise, uint day, uint sunset, uint night, uint highNoon, uint midnight)
    {
        return new WeatherRecord
        {
            FormId = formId,
            ImageSpaceModifiers = new WeatherTimeBands<uint>(sunrise, day, sunset, night)
            {
                HighNoon = highNoon,
                Midnight = midnight
            }
        };
    }

    private static ImageSpaceModifierRecord Modifier(
        uint formId,
        ImageSpaceModifierParameter parameter,
        IReadOnlyList<ImageSpaceModifierFloatKey> multiply,
        IReadOnlyList<ImageSpaceModifierFloatKey> add,
        bool animatable = false,
        float duration = 1f)
    {
        var timelines = Enumerable.Range(0, 21)
            .Select(i => new ImageSpaceModifierParameterTimeline((ImageSpaceModifierParameter)i, [], []))
            .ToArray();
        timelines[(int)parameter] = new ImageSpaceModifierParameterTimeline(parameter, multiply, add);
        return new ImageSpaceModifierRecord
        {
            FormId = formId,
            Data = new ImageSpaceModifierData { AnimatableFlag = animatable ? 1u : 0u, Duration = duration },
            Parameters = timelines
        };
    }
}