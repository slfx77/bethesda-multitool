using System.Globalization;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.Misc;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;

internal enum WeatherImageSpaceBand
{
    Sunrise,
    Day,
    Sunset,
    Night,
    HighNoon,
    EarlySunrise,
    LateSunrise,
    EarlySunset,
    LateSunset,
}

internal readonly record struct WeatherImageSpaceContribution(
    uint WeatherFormId,
    WeatherImageSpaceBand Band,
    uint ModifierFormId,
    float Weight,
    float? TimelineTime);

internal readonly record struct WeatherImageSpaceChannel(float Multiply, float Add);

internal sealed record WeatherImageSpaceEvaluation(
    GpuTonemapSettings Settings,
    IReadOnlyList<WeatherImageSpaceContribution> Contributions,
    IReadOnlyDictionary<ImageSpaceModifierParameter, WeatherImageSpaceChannel> Channels,
    string Telemetry);

/// <summary>
///     CPU reference for the classic Sky::UpdateHDRValues → TESImageSpaceModifier::ApplyWeather path.
///     The recovered manager accumulates absolute multiply/add samples from up to four instances and
///     applies <c>base * sum(weight*multiply) + sum(weight*add)</c>. Midnight is authored by FNV but is
///     not selected by this path.
/// </summary>
internal static class WeatherImageSpaceEvaluator
{
    private readonly record struct BandWeight(WeatherImageSpaceBand Band, float Weight);

    internal static WeatherImageSpaceEvaluation Evaluate(
        GpuTonemapSettings baseSettings,
        float gameHour,
        AtmosphereState.ClimateTiming timing,
        WeatherRecord? currentWeather,
        WeatherRecord? outgoingWeather,
        float currentWeatherWeight,
        IReadOnlyDictionary<uint, ImageSpaceModifierRecord> modifiersByFormId,
        float? modifierElapsedSeconds = null)
    {
        var currentWeight = outgoingWeather is null ? 1f : Math.Clamp(currentWeatherWeight, 0f, 1f);
        var outgoingWeight = outgoingWeather is null ? 0f : 1f - currentWeight;
        var bands = SelectBands(gameHour, timing);
        var contributions = new List<WeatherImageSpaceContribution>(4);
        AddWeatherContributions(currentWeather, currentWeight, bands, modifiersByFormId,
            modifierElapsedSeconds, contributions);
        AddWeatherContributions(outgoingWeather, outgoingWeight, bands, modifiersByFormId,
            modifierElapsedSeconds, contributions);

        var channels = new Dictionary<ImageSpaceModifierParameter, WeatherImageSpaceChannel>(21);
        for (var parameterIndex = 0; parameterIndex < 21; parameterIndex++)
        {
            var parameter = (ImageSpaceModifierParameter)parameterIndex;
            var multiply = 0f;
            var add = 0f;
            foreach (var contribution in contributions)
            {
                if (contribution.Weight <= 0f) continue;
                if (contribution.ModifierFormId != 0
                    && modifiersByFormId.TryGetValue(contribution.ModifierFormId, out var modifier))
                {
                    var timeline = parameterIndex < modifier.Parameters.Count
                        ? modifier.Parameters[parameterIndex]
                        : null;
                    // The FNV Sky snapshot does not yet recover the separate IMAD elapsed clock.
                    // A genuinely animated curve is neutral instead of being silently fabricated
                    // at t=0. Curves whose authored values are invariant do not depend on that
                    // missing clock, so retain those independently for multiply and add.
                    if (contribution.TimelineTime is { } time)
                    {
                        multiply += contribution.Weight * Sample(
                            timeline?.Multiply, time, neutral: 1f);
                        add += contribution.Weight * Sample(
                            timeline?.Add, time, neutral: 0f);
                    }
                    else
                    {
                        multiply += contribution.Weight * SampleTimeInvariant(
                            timeline?.Multiply, neutral: 1f);
                        add += contribution.Weight * SampleTimeInvariant(
                            timeline?.Add, neutral: 0f);
                    }
                }
                else
                {
                    // TESImageSpaceModifier::sDefault supplies the neutral absolute multiply curve.
                    multiply += contribution.Weight;
                }
            }

            // No active weather is also neutral. Contributions normally sum to one, but retain a
            // neutral remainder for malformed transitions rather than darkening every HDR parameter.
            var totalWeight = contributions.Sum(c => c.Weight);
            if (totalWeight < 1f) multiply += 1f - totalWeight;
            channels[parameter] = new WeatherImageSpaceChannel(multiply, add);
        }

        float Apply(ImageSpaceModifierParameter p, float value)
        {
            var c = channels[p];
            return value * c.Multiply + c.Add;
        }

        var settings = baseSettings with
        {
            EyeAdaptSpeed = Apply(ImageSpaceModifierParameter.EyeAdaptSpeed, baseSettings.EyeAdaptSpeed),
            BlurRadius = Apply(ImageSpaceModifierParameter.HdrBlurRadius, baseSettings.BlurRadius),
            EmissiveMult = Apply(ImageSpaceModifierParameter.HdrEmissiveMult, baseSettings.EmissiveMult),
            TargetLum = Apply(ImageSpaceModifierParameter.HdrTargetLum, baseSettings.TargetLum),
            UpperLumClamp = Apply(ImageSpaceModifierParameter.HdrUpperLumClamp,
                baseSettings.UpperLumClamp),
            BrightScale = Apply(ImageSpaceModifierParameter.HdrBrightScale, baseSettings.BrightScale),
            BrightClamp = Apply(ImageSpaceModifierParameter.HdrBrightClamp, baseSettings.BrightClamp),
            SunlightScale = Apply(ImageSpaceModifierParameter.HdrSunlightDimmer,
                baseSettings.SunlightScale),
            Saturation = Apply(ImageSpaceModifierParameter.CinematicSaturation, baseSettings.Saturation),
            ContrastAvgLum = Apply(ImageSpaceModifierParameter.CinematicContrastAvgLum,
                baseSettings.ContrastAvgLum),
            Contrast = Apply(ImageSpaceModifierParameter.CinematicContrast, baseSettings.Contrast),
            Brightness = Apply(ImageSpaceModifierParameter.CinematicBrightness, baseSettings.Brightness),
        };

        settings = ApplyTint(settings, contributions, modifiersByFormId);
        var telemetry = FormatTelemetry(gameHour, contributions, settings, channels);
        return new WeatherImageSpaceEvaluation(settings, contributions, channels, telemetry);
    }

    /// <summary>
    ///     Modern WTHR IMSP references point at IMGS (not IMAD) records. FO4 Sky::UpdateHDRValues
    ///     zeroes an ImageSpaceBaseData and WeightedAdd's the two selected bands × current/outgoing
    ///     weather, so blend every authored scalar directly and retain each LUT path/weight in telemetry.
    /// </summary>
    internal static WeatherImageSpaceEvaluation EvaluateModern(
        GpuTonemapSettings baseSettings,
        float gameHour,
        AtmosphereState.ClimateTiming timing,
        WeatherRecord? currentWeather,
        WeatherRecord? outgoingWeather,
        float currentWeatherWeight,
        IReadOnlyDictionary<uint, ImageSpaceRecord> imageSpacesByFormId)
    {
        var currentWeight = outgoingWeather is null ? 1f : Math.Clamp(currentWeatherWeight, 0f, 1f);
        var outgoingWeight = outgoingWeather is null ? 0f : 1f - currentWeight;
        var bands = SelectModernBands(gameHour, timing,
            currentWeather?.ImageSpaceModifiers?.HasModernTransitions == true
            || outgoingWeather?.ImageSpaceModifiers?.HasModernTransitions == true);
        var contributions = new List<WeatherImageSpaceContribution>(4);
        AddModernContributions(currentWeather, currentWeight, bands, contributions);
        AddModernContributions(outgoingWeather, outgoingWeight, bands, contributions);

        var weighted = new List<(GpuTonemapSettings Settings, float Weight, string? Lut)>(contributions.Count);
        foreach (var contribution in contributions)
        {
            var s = baseSettings;
            string? lut = null;
            if (contribution.ModifierFormId != 0
                && imageSpacesByFormId.TryGetValue(contribution.ModifierFormId, out var imgs))
            {
                s = GpuTonemapSettings.ApplyModernImageSpace(s, imgs);
                lut = imgs.LutTexturePath;
            }
            weighted.Add((s, contribution.Weight, lut));
        }

        if (weighted.Count == 0) weighted.Add((baseSettings, 1f, baseSettings.LutTexturePath));
        var settings = BlendModernSettings(baseSettings, weighted);
        var channels = NeutralChannels();
        var telemetry = FormatModernTelemetry(gameHour, contributions, settings, weighted);
        return new WeatherImageSpaceEvaluation(settings, contributions, channels, telemetry);
    }

    /// <summary>Maps all 21 recovered IMAD channels onto the corresponding modern semantic slots.</summary>
    internal static GpuTonemapSettings ApplyModernChannels(
        GpuTonemapSettings settings,
        IReadOnlyDictionary<ImageSpaceModifierParameter, WeatherImageSpaceChannel> channels)
    {
        float Apply(ImageSpaceModifierParameter p, float value) => channels.TryGetValue(p, out var c)
            ? value * c.Multiply + c.Add
            : value;

        settings = settings with
        {
            EyeAdaptSpeed = Apply(ImageSpaceModifierParameter.EyeAdaptSpeed, settings.EyeAdaptSpeed),
            BlurRadius = Apply(ImageSpaceModifierParameter.HdrBlurRadius, settings.BlurRadius),
            EmissiveMult = Apply(ImageSpaceModifierParameter.HdrEmissiveMult, settings.EmissiveMult),
            BrightClamp = Apply(ImageSpaceModifierParameter.HdrBrightClamp, settings.BrightClamp),
            BrightScale = Apply(ImageSpaceModifierParameter.HdrBrightScale, settings.BrightScale),
            SunlightScale = Apply(ImageSpaceModifierParameter.HdrSunlightDimmer, settings.SunlightScale),
            Saturation = Apply(ImageSpaceModifierParameter.CinematicSaturation, settings.Saturation),
            ContrastAvgLum = Apply(ImageSpaceModifierParameter.CinematicContrastAvgLum,
                settings.ContrastAvgLum),
            Contrast = Apply(ImageSpaceModifierParameter.CinematicContrast, settings.Contrast),
            Brightness = Apply(ImageSpaceModifierParameter.CinematicBrightness, settings.Brightness),
        };
        return settings.ModernFamily == ImageSpaceModernFamily.Skyrim
            ? settings with
            {
                ReceiveBloomThreshold = Apply(ImageSpaceModifierParameter.HdrTargetLum,
                    settings.ReceiveBloomThreshold),
                White = Apply(ImageSpaceModifierParameter.HdrUpperLumClamp, settings.White),
                EyeAdaptStrength = Apply(ImageSpaceModifierParameter.HdrLumRampNoTex,
                    settings.EyeAdaptStrength),
            }
            : settings with
            {
                TonemapE = Apply(ImageSpaceModifierParameter.HdrBlurRadius, settings.TonemapE),
                AutoExposureMax = Apply(ImageSpaceModifierParameter.HdrTargetLum,
                    settings.AutoExposureMax),
                AutoExposureMin = Apply(ImageSpaceModifierParameter.HdrUpperLumClamp,
                    settings.AutoExposureMin),
                MiddleGray = Apply(ImageSpaceModifierParameter.HdrLumRampNoTex, settings.MiddleGray),
            };
    }

    private static Dictionary<ImageSpaceModifierParameter, WeatherImageSpaceChannel> NeutralChannels()
    {
        var result = new Dictionary<ImageSpaceModifierParameter, WeatherImageSpaceChannel>(21);
        for (var i = 0; i < 21; i++) result[(ImageSpaceModifierParameter)i] = new(1f, 0f);
        return result;
    }

    private static void AddModernContributions(
        WeatherRecord? weather, float weatherWeight, IReadOnlyList<BandWeight> bands,
        List<WeatherImageSpaceContribution> destination)
    {
        if (weather?.ImageSpaceModifiers is not { } ids || weatherWeight <= 0f) return;
        foreach (var band in bands)
        {
            destination.Add(new WeatherImageSpaceContribution(weather.FormId, band.Band,
                ResolveModifierId(ids, band.Band), weatherWeight * band.Weight, 0f));
        }
    }

    private static GpuTonemapSettings BlendModernSettings(
        GpuTonemapSettings template,
        IReadOnlyList<(GpuTonemapSettings Settings, float Weight, string? Lut)> sources)
    {
        float W(Func<GpuTonemapSettings, float> select) => sources.Sum(s => select(s.Settings) * s.Weight);
        var dominant = sources.OrderByDescending(s => s.Weight).First();
        return template with
        {
            EyeAdaptSpeed = W(static s => s.EyeAdaptSpeed),
            BlurRadius = W(static s => s.BlurRadius),
            BrightClamp = W(static s => s.BrightClamp),
            BrightScale = W(static s => s.BrightScale),
            TonemapE = W(static s => s.TonemapE),
            AutoExposureMin = W(static s => s.AutoExposureMin),
            AutoExposureMax = W(static s => s.AutoExposureMax),
            MiddleGray = W(static s => s.MiddleGray),
            White = W(static s => s.White),
            EyeAdaptStrength = W(static s => s.EyeAdaptStrength),
            ReceiveBloomThreshold = W(static s => s.ReceiveBloomThreshold),
            SunlightScale = W(static s => s.SunlightScale),
            SkyScale = W(static s => s.SkyScale),
            Saturation = W(static s => s.Saturation),
            Brightness = W(static s => s.Brightness),
            Contrast = W(static s => s.Contrast),
            ContrastAvgLum = W(static s => s.ContrastAvgLum),
            TintAmount = W(static s => s.TintAmount),
            TintR = W(static s => s.TintR),
            TintG = W(static s => s.TintG),
            TintB = W(static s => s.TintB),
            LutTexturePath = dominant.Lut,
            BloomEnabled = false,
        };
    }

    private static IReadOnlyList<BandWeight> SelectModernBands(
        float gameHour, AtmosphereState.ClimateTiming timing, bool eightBands)
    {
        var hour = gameHour % 24f;
        if (hour < 0f) hour += 24f;
        var srB = timing.SunriseBeginHour;
        var srE = Math.Max(srB, timing.SunriseEndHour);
        var ssB = Math.Max(srE, timing.SunsetBeginHour);
        var ssE = Math.Max(ssB, timing.SunsetEndHour);
        if (!eightBands)
        {
            // Modern four-band WTHR records have no FNV HighNoon slot. Interpolate through the
            // authored Night/Sunrise/Day and Day/Sunset/Night triplets without inventing one.
            if (hour < srB || hour >= ssE)
                return [new BandWeight(WeatherImageSpaceBand.Night, 1f)];
            if (hour < srE && srE > srB)
            {
                var midpoint = (srB + srE) * 0.5f;
                if (hour <= midpoint)
                {
                    var risingWeight = (hour - srB) / Math.Max(midpoint - srB, float.Epsilon);
                    return Pair(WeatherImageSpaceBand.Sunrise, risingWeight, WeatherImageSpaceBand.Night);
                }

                var fallingWeight = (srE - hour) / Math.Max(srE - midpoint, float.Epsilon);
                return Pair(WeatherImageSpaceBand.Sunrise, fallingWeight, WeatherImageSpaceBand.Day);
            }
            if (hour < ssB) return [new BandWeight(WeatherImageSpaceBand.Day, 1f)];
            if (ssE > ssB)
            {
                var midpoint = (ssB + ssE) * 0.5f;
                if (hour <= midpoint)
                {
                    var risingWeight = (hour - ssB) / Math.Max(midpoint - ssB, float.Epsilon);
                    return Pair(WeatherImageSpaceBand.Sunset, risingWeight, WeatherImageSpaceBand.Day);
                }

                var fallingWeight = (ssE - hour) / Math.Max(ssE - midpoint, float.Epsilon);
                return Pair(WeatherImageSpaceBand.Sunset, fallingWeight, WeatherImageSpaceBand.Night);
            }
            return [new BandWeight(WeatherImageSpaceBand.Night, 1f)];
        }

        if (hour < srB || hour >= ssE) return [new BandWeight(WeatherImageSpaceBand.Night, 1f)];
        if (hour < srE && srE > srB)
        {
            return SelectFive([
                WeatherImageSpaceBand.Night, WeatherImageSpaceBand.EarlySunrise,
                WeatherImageSpaceBand.Sunrise, WeatherImageSpaceBand.LateSunrise,
                WeatherImageSpaceBand.Day], (hour - srB) / (srE - srB));
        }
        if (hour < ssB) return [new BandWeight(WeatherImageSpaceBand.Day, 1f)];
        if (ssE > ssB)
        {
            return SelectFive([
                WeatherImageSpaceBand.Day, WeatherImageSpaceBand.EarlySunset,
                WeatherImageSpaceBand.Sunset, WeatherImageSpaceBand.LateSunset,
                WeatherImageSpaceBand.Night], (hour - ssB) / (ssE - ssB));
        }
        return [new BandWeight(WeatherImageSpaceBand.Night, 1f)];
    }

    private static IReadOnlyList<BandWeight> SelectFive(WeatherImageSpaceBand[] values, float t)
    {
        var x = Math.Clamp(t, 0f, 1f) * 4f;
        var first = Math.Min((int)MathF.Floor(x), 3);
        var nextWeight = x - first;
        return Pair(values[first + 1], nextWeight, values[first]);
    }

    private static void AddWeatherContributions(
        WeatherRecord? weather,
        float weatherWeight,
        IReadOnlyList<BandWeight> bands,
        IReadOnlyDictionary<uint, ImageSpaceModifierRecord> modifiersByFormId,
        float? modifierElapsedSeconds,
        List<WeatherImageSpaceContribution> destination)
    {
        if (weather is null || weatherWeight <= 0f) return;
        foreach (var band in bands)
        {
            var id = ResolveModifierId(weather.ImageSpaceModifiers, band.Band);
            float? normalizedTime = 0f;
            if (id != 0 && modifiersByFormId.TryGetValue(id, out var modifier)
                        && modifier.Data is { IsAnimatable: true, Duration: > 0f } data)
            {
                normalizedTime = modifierElapsedSeconds is { } elapsed
                    ? Math.Clamp(elapsed / data.Duration, 0f, 1f)
                    : null;
            }
            destination.Add(new WeatherImageSpaceContribution(
                weather.FormId, band.Band, id, weatherWeight * band.Weight, normalizedTime));
        }
    }

    private static uint ResolveModifierId(WeatherTimeBands<uint>? bands, WeatherImageSpaceBand band)
    {
        if (bands is null) return 0;
        return band switch
        {
            WeatherImageSpaceBand.Sunrise => bands.Sunrise,
            WeatherImageSpaceBand.Day => bands.Day,
            WeatherImageSpaceBand.Sunset => bands.Sunset,
            WeatherImageSpaceBand.Night => bands.Night,
            WeatherImageSpaceBand.HighNoon => bands.HighNoon ?? bands.Day,
            WeatherImageSpaceBand.EarlySunrise => bands.EarlySunrise ?? bands.Sunrise,
            WeatherImageSpaceBand.LateSunrise => bands.LateSunrise ?? bands.Sunrise,
            WeatherImageSpaceBand.EarlySunset => bands.EarlySunset ?? bands.Sunset,
            WeatherImageSpaceBand.LateSunset => bands.LateSunset ?? bands.Sunset,
            _ => 0,
        };
    }

    private static IReadOnlyList<BandWeight> SelectBands(float gameHour, AtmosphereState.ClimateTiming timing)
    {
        var hour = gameHour % 24f;
        if (hour < 0f) hour += 24f;
        var sunriseBegin = timing.SunriseBeginHour;
        var sunriseEnd = Math.Max(sunriseBegin, timing.SunriseEndHour);
        var sunsetBegin = Math.Max(sunriseEnd, timing.SunsetBeginHour);
        var sunsetEnd = Math.Max(sunsetBegin, timing.SunsetEndHour);
        var highNoon = 12f;

        if (hour >= sunriseBegin && hour <= sunriseEnd && sunriseEnd > sunriseBegin)
        {
            var midpoint = (sunriseBegin + sunriseEnd) * 0.5f;
            if (hour <= midpoint)
            {
                var sunrise = (hour - sunriseBegin) / Math.Max(midpoint - sunriseBegin, float.Epsilon);
                return Pair(WeatherImageSpaceBand.Sunrise, sunrise, WeatherImageSpaceBand.Night);
            }
            else
            {
                var sunrise = (sunriseEnd - hour) / Math.Max(sunriseEnd - midpoint, float.Epsilon);
                return Pair(WeatherImageSpaceBand.Sunrise, sunrise, WeatherImageSpaceBand.Day);
            }
        }

        if (hour >= sunriseEnd && hour <= highNoon && highNoon > sunriseEnd)
        {
            // Sky::UpdateHDRValues deliberately uses the inverse endpoint identity from
            // Sky::FillColorBlend here: HighNoon is the daylight shoulder and Day reaches
            // full weight at noon.
            var dayWeight = (hour - sunriseEnd) / (highNoon - sunriseEnd);
            return Pair(WeatherImageSpaceBand.Day, dayWeight, WeatherImageSpaceBand.HighNoon);
        }

        if (hour >= highNoon && hour < sunsetBegin && sunsetBegin > highNoon)
        {
            var highNoonWeight = (hour - highNoon) / (sunsetBegin - highNoon);
            return Pair(WeatherImageSpaceBand.HighNoon, highNoonWeight, WeatherImageSpaceBand.Day);
        }

        if (hour >= sunsetBegin && hour <= sunsetEnd && sunsetEnd > sunsetBegin)
        {
            var midpoint = (sunsetBegin + sunsetEnd) * 0.5f;
            if (hour <= midpoint)
            {
                var sunset = (hour - sunsetBegin) / Math.Max(midpoint - sunsetBegin, float.Epsilon);
                return Pair(WeatherImageSpaceBand.Sunset, sunset, WeatherImageSpaceBand.Day);
            }
            else
            {
                var sunset = (sunsetEnd - hour) / Math.Max(sunsetEnd - midpoint, float.Epsilon);
                return Pair(WeatherImageSpaceBand.Sunset, sunset, WeatherImageSpaceBand.Night);
            }
        }

        return [new BandWeight(WeatherImageSpaceBand.Night, 1f)];
    }

    private static IReadOnlyList<BandWeight> Pair(
        WeatherImageSpaceBand primary, float primaryWeight, WeatherImageSpaceBand secondary)
    {
        primaryWeight = Math.Clamp(primaryWeight, 0f, 1f);
        if (primaryWeight <= 0f) return [new BandWeight(secondary, 1f)];
        if (primaryWeight >= 1f) return [new BandWeight(primary, 1f)];
        return [new BandWeight(primary, primaryWeight), new BandWeight(secondary, 1f - primaryWeight)];
    }

    internal static float Sample(
        IReadOnlyList<ImageSpaceModifierFloatKey>? keys, float time, float neutral)
    {
        if (keys is null || keys.Count == 0) return neutral;
        if (keys.Count == 1 || time <= keys[0].Time) return keys[0].Value;
        for (var i = 1; i < keys.Count; i++)
        {
            var next = keys[i];
            if (time > next.Time) continue;
            var previous = keys[i - 1];
            var span = next.Time - previous.Time;
            if (span <= 0f) return next.Value;
            var t = (time - previous.Time) / span;
            return previous.Value + (next.Value - previous.Value) * t;
        }
        return keys[^1].Value;
    }

    private static float SampleTimeInvariant(
        IReadOnlyList<ImageSpaceModifierFloatKey>? keys, float neutral)
    {
        if (keys is null || keys.Count == 0) return neutral;
        var value = keys[0].Value;
        for (var i = 1; i < keys.Count; i++)
        {
#pragma warning disable S1244 // exact same-source identity: any authored difference means the curve animates
            if (keys[i].Value != value) return neutral;
#pragma warning restore S1244
        }
        return value;
    }

    private static ImageSpaceModifierColorKey SampleColor(
        IReadOnlyList<ImageSpaceModifierColorKey> keys, float time)
    {
        if (keys.Count == 0) return default;
        if (keys.Count == 1 || time <= keys[0].Time) return keys[0];
        for (var i = 1; i < keys.Count; i++)
        {
            var next = keys[i];
            if (time > next.Time) continue;
            var previous = keys[i - 1];
            var span = next.Time - previous.Time;
            if (span <= 0f) return next;
            var t = (time - previous.Time) / span;
            return new ImageSpaceModifierColorKey(time,
                Lerp(previous.Red, next.Red, t), Lerp(previous.Green, next.Green, t),
                Lerp(previous.Blue, next.Blue, t), Lerp(previous.Alpha, next.Alpha, t));
        }
        return keys[^1];
    }

    private static bool TryGetTimeInvariantColor(
        IReadOnlyList<ImageSpaceModifierColorKey> keys,
        out ImageSpaceModifierColorKey color)
    {
        if (keys.Count == 0)
        {
            color = default;
            return false;
        }

        color = keys[0];
        for (var i = 1; i < keys.Count; i++)
        {
            var next = keys[i];
#pragma warning disable S1244 // exact same-source identity: any authored difference means the curve animates
            if (next.Red != color.Red || next.Green != color.Green
                                      || next.Blue != color.Blue || next.Alpha != color.Alpha)
                return false;
#pragma warning restore S1244
        }
        return true;
    }

    private static GpuTonemapSettings ApplyTint(
        GpuTonemapSettings settings,
        IReadOnlyList<WeatherImageSpaceContribution> contributions,
        IReadOnlyDictionary<uint, ImageSpaceModifierRecord> modifiersByFormId)
    {
        var weatherRed = 0f;
        var weatherGreen = 0f;
        var weatherBlue = 0f;
        var weatherAlpha = 0f;
        foreach (var contribution in contributions)
        {
            if (contribution.Weight <= 0f || contribution.ModifierFormId == 0
                || !modifiersByFormId.TryGetValue(contribution.ModifierFormId, out var modifier)) continue;
            ImageSpaceModifierColorKey color;
            if (contribution.TimelineTime is { } timelineTime)
            {
                color = SampleColor(modifier.TintColorTimeline, timelineTime);
            }
            else if (!TryGetTimeInvariantColor(modifier.TintColorTimeline, out color))
            {
                continue;
            }
            // ApplyWeather scales all four sampled channels by the instance weight and the weather
            // accumulator sums that raw RGBA. EndWeatherModifiers then passes the aggregate through
            // ApplyTintColorParamModifier, which premultiplies RGB by the aggregate alpha once.
            weatherRed += color.Red * contribution.Weight;
            weatherGreen += color.Green * contribution.Weight;
            weatherBlue += color.Blue * contribution.Weight;
            weatherAlpha += color.Alpha * contribution.Weight;
        }

        weatherRed *= weatherAlpha;
        weatherGreen *= weatherAlpha;
        weatherBlue *= weatherAlpha;

        var denominator = settings.TintAmount + weatherAlpha;
        var red = 0f;
        var green = 0f;
        var blue = 0f;
        if (denominator > 0f)
        {
            red = (settings.TintR * settings.TintAmount + weatherRed) / denominator;
            green = (settings.TintG * settings.TintAmount + weatherGreen) / denominator;
            blue = (settings.TintB * settings.TintAmount + weatherBlue) / denominator;
        }

        return settings with
        {
            TintR = red,
            TintG = green,
            TintB = blue,
            TintAmount = Math.Max(settings.TintAmount, weatherAlpha),
        };
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static string FormatTelemetry(
        float hour,
        List<WeatherImageSpaceContribution> contributions,
        GpuTonemapSettings settings,
        Dictionary<ImageSpaceModifierParameter, WeatherImageSpaceChannel> channels)
    {
        static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        static string Time(float? value) => value is { } time ? F(time) : "unknown";
        var selected = contributions.Count == 0
            ? "neutral"
            : string.Join(",", contributions.Select(c =>
                $"W{c.WeatherFormId:X8}:{c.Band}=I{c.ModifierFormId:X8}@{F(c.Weight)}[t={Time(c.TimelineTime)}]"));
        var sun = channels[ImageSpaceModifierParameter.HdrSunlightDimmer];
        var grass = channels[ImageSpaceModifierParameter.HdrGrassDimmer];
        var tree = channels[ImageSpaceModifierParameter.HdrTreeDimmer];
        return $"hour={F(hour)} bands=[{selected}] eye={F(settings.EyeAdaptSpeed)} " +
               $"bloom(r={F(settings.BlurRadius)},th={F(settings.BrightClamp)},s={F(settings.BrightScale)}) " +
               $"lum={F(settings.TargetLum)}..{F(settings.UpperLumClamp)} " +
               $"cin={F(settings.Saturation)}/{F(settings.Brightness)}/{F(settings.Contrast)} " +
               $"tint={F(settings.TintR)},{F(settings.TintG)},{F(settings.TintB)},{F(settings.TintAmount)} " +
               $"scene(sun={F(settings.SunlightScale)}[{F(sun.Multiply)}+{F(sun.Add)}]) " +
               $"unmapped(grass={F(grass.Multiply)}+{F(grass.Add)}," +
               $"tree={F(tree.Multiply)}+{F(tree.Add)})";
    }

    private static string FormatModernTelemetry(
        float hour,
        List<WeatherImageSpaceContribution> contributions,
        GpuTonemapSettings settings,
        IReadOnlyList<(GpuTonemapSettings Settings, float Weight, string? Lut)> sources)
    {
        static string F(float value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        var selected = contributions.Count == 0 ? "neutral" : string.Join(",", contributions.Select(c =>
            $"W{c.WeatherFormId:X8}:{c.Band}=S{c.ModifierFormId:X8}@{F(c.Weight)}"));
        var luts = string.Join(",", sources.Where(s => !string.IsNullOrWhiteSpace(s.Lut))
            .Select(s => $"{s.Lut}@{F(s.Weight)}"));
        if (luts.Length == 0) luts = "none";
        return $"modern hour={F(hour)} bands=[{selected}] family={settings.ModernFamily} " +
               $"eye={F(settings.EyeAdaptSpeed)} bloom(th={F(settings.BrightClamp)},s={F(settings.BrightScale)},neutral) " +
               $"exposure={F(settings.AutoExposureMin)}..{F(settings.AutoExposureMax)} middle={F(settings.MiddleGray)} " +
               $"tonemapE={F(settings.TonemapE)} sun={F(settings.SunlightScale)} sky={F(settings.SkyScale)} " +
               $"cin={F(settings.Saturation)}/{F(settings.Brightness)}/{F(settings.Contrast)} luts=[{luts}](retained-only)";
    }
}
