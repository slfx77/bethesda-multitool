using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;

/// <summary>
///     One resolved XEMI source. LIGH records and runtime-only REGN snapshots carry a constant color;
///     plugin REGN records retain their deterministic RDWT weather so the WTHR Effect Lighting band can
///     be sampled at the current game hour.
/// </summary>
internal readonly record struct ExternalEmittanceSource
{
    private readonly Vector3 _constantColor;
    private readonly bool _isResolved;
    private readonly WeatherRecord? _weather;

    private ExternalEmittanceSource(Vector3 constantColor, WeatherRecord? weather, bool isResolved)
    {
        _constantColor = constantColor;
        _weather = weather;
        _isResolved = isResolved;
    }

    internal bool IsWeatherDriven => _weather is not null;

    internal static ExternalEmittanceSource Constant(Vector3 color)
    {
        return new ExternalEmittanceSource(color, null, true);
    }

    internal static ExternalEmittanceSource FromWeather(WeatherRecord weather)
    {
        return new ExternalEmittanceSource(Vector3.One, weather, true);
    }

    internal static ExternalEmittanceSource Unresolved()
    {
        return new ExternalEmittanceSource(Vector3.One, null, false);
    }

    internal Vector3 Resolve(
        float gameHour,
        AtmosphereState.ClimateTiming? timing,
        BethesdaGame game)
    {
        return TryResolve(gameHour, timing, game, out var color) ? color : Vector3.One;
    }

    /// <summary>
    ///     Resolves a usable runtime color while preserving the distinction between authored white
    ///     and an unavailable source. Lighting30 needs that distinction so an unresolved REGN link
    ///     does not replace its material emission fallback with white.
    /// </summary>
    internal bool TryResolve(
        float gameHour,
        AtmosphereState.ClimateTiming? timing,
        BethesdaGame game,
        out Vector3 color)
    {
        if (_weather is not { } weather)
        {
            color = _constantColor;
            return _isResolved;
        }

        // Row 9 is Effect Lighting only in the Skyrim-and-later WTHR layout. Earlier games retain
        // the numeric slot as unused, so treating it as emittance can turn legacy effects black.
        if (!SupportsWeatherEffectLighting(game))
        {
            color = Vector3.One;
            return false;
        }

        var colorIndex = (int)WeatherColorType.EffectLighting;
        if (colorIndex >= weather.Colors.Count)
        {
            color = Vector3.One;
            return false;
        }

        color = AtmosphereState.SampleWeatherColor(weather.Colors[colorIndex], gameHour, timing, game);
        return float.IsFinite(color.X) && float.IsFinite(color.Y) && float.IsFinite(color.Z);
    }

    private static bool SupportsWeatherEffectLighting(BethesdaGame game)
    {
        return game is
            BethesdaGame.Skyrim or
            BethesdaGame.Fallout4 or
            BethesdaGame.Fallout76 or
            BethesdaGame.Starfield;
    }
}

/// <summary>Resolves REFR XEMI targets and applies the recovered effect-shader color blend.</summary>
internal static class ExternalEmittanceResolver
{
    public static Dictionary<uint, ExternalEmittanceSource> BuildIndex(
        IReadOnlyList<RegionRecord> regions,
        IReadOnlyList<LightRecord> lights,
        IReadOnlyDictionary<uint, WeatherRecord> weathers)
    {
        var result = new Dictionary<uint, ExternalEmittanceSource>(regions.Count + lights.Count);
        foreach (var region in regions)
        {
            result[region.FormId] = ResolveRegionSource(region, weathers);
        }

        foreach (var light in lights)
        {
            var packed = light.Color;
            result[light.FormId] = ExternalEmittanceSource.Constant(new Vector3(
                (packed & 0xff) / 255f,
                ((packed >> 8) & 0xff) / 255f,
                ((packed >> 16) & 0xff) / 255f));
        }

        return result;
    }

    private static ExternalEmittanceSource ResolveRegionSource(
        RegionRecord region,
        IReadOnlyDictionary<uint, WeatherRecord> weathers)
    {
        if (region.IsRuntimeOnly)
        {
            return ExternalEmittanceSource.Constant(new Vector3(
                region.EmittanceColorR / 255f,
                region.EmittanceColorG / 255f,
                region.EmittanceColorB / 255f));
        }

        var candidateCount = 0;
        var candidateFormId = 0u;
        foreach (var candidate in region.WeatherTypes)
        {
            if (candidate.Chance != 100u || candidate.GlobalFormId != 0u)
            {
                continue;
            }

            candidateCount++;
            candidateFormId = candidate.WeatherFormId;
        }

        // Plugin REGN RCLR is the editor/map color, not render-time external emittance. Fail neutral
        // when the region's current weather cannot be selected deterministically from its RDWT list.
        return candidateCount == 1 && weathers.TryGetValue(candidateFormId, out var weather)
            ? ExternalEmittanceSource.FromWeather(weather)
            : ExternalEmittanceSource.Unresolved();
    }

    /// <summary>
    ///     Retail BSEffect unlit path: <c>rgb *= lerp(1, externalColor, LightingInfluence)</c>.
    ///     Classic external-emittance properties have no packed influence and pass 1.
    /// </summary>
    public static Vector3 Modulation(Vector3 externalColor, float influence)
    {
        return Vector3.Lerp(Vector3.One, externalColor, Math.Clamp(influence, 0f, 1f));
    }
}
