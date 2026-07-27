using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>Resolves authored WTHR cloud motion, including engine-defined legacy defaults.</summary>
internal static class WeatherCloudMotion
{
    /// <summary>
    ///     Skyrim's normalized QNAM/RNAM axes are multiplied by
    ///     <c>fWeatherCloudSpeedMax * Clouds::Update time scale = 0.1 * 0.1</c>.
    ///     Legacy TES4/FO3/FNV scalar speeds use the authored weather wind instead.
    /// </summary>
    internal const float UvPerSecondScale = 0.010f;
    internal const float LegacyCloudSpeedMax = 0.1f;
    internal const float FnvEmptyOnamSpeed = 0x33 / 255f;

    /// <summary>
    ///     Returns per-second UV motion for one source layer. The joined semantic layer is
    ///     authoritative even when both authored values are zero. Parallel arrays are only a legacy
    ///     compatibility projection, except for FNV's byte-array lookup contract recovered from
    ///     <c>TESWeather::GetCloudSpeed</c>.
    /// </summary>
    internal static Vector2 Resolve(
        WeatherRecord? weather,
        WeatherCloudLayer? semanticLayer,
        int sourceLayerIndex,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        var authoredSpeed = ResolveAuthoredSpeed(weather, semanticLayer, sourceLayerIndex, game);
        var scale = ResolveScale(weather, game);
        return authoredSpeed * scale;
    }

    /// <summary>
    ///     Resolves the authored/defaulted speed before the legacy wind multiplier is applied.
    ///     FNV's <c>Clouds::Update</c> blends this value between weathers first, then multiplies the
    ///     separately blended <c>Sky::fWindSpeed</c>.
    /// </summary>
    internal static Vector2 ResolveBeforeLegacyWind(
        WeatherRecord? weather,
        WeatherCloudLayer? semanticLayer,
        int sourceLayerIndex,
        BethesdaGame game) =>
        ResolveAuthoredSpeed(weather, semanticLayer, sourceLayerIndex, game) * LegacyCloudSpeedMax;

    internal static float ResolveLegacyWind(WeatherRecord? weather) =>
        (weather?.Data?.WindSpeed ?? 0) / 255f;

    private static Vector2 ResolveAuthoredSpeed(
        WeatherRecord? weather,
        WeatherCloudLayer? semanticLayer,
        int sourceLayerIndex,
        BethesdaGame game)
    {
        if (game == BethesdaGame.FalloutNewVegas && weather is not null && sourceLayerIndex >= 0)
        {
            // Retail PC TESWeather::GetCloudSpeed starts with byte 0x33. A non-empty ONAM uses
            // the requested slot, or slot zero when the source index is outside the array. Parsed
            // records retain ONAM in CloudSpeedsX; the semantic non-zero branch only supports
            // hand-built callers that predate that lossless projection.
            float speedU;
            if (weather.CloudSpeedsX.Count > 0)
            {
                var speedIndex = sourceLayerIndex < weather.CloudSpeedsX.Count ? sourceLayerIndex : 0;
                speedU = weather.CloudSpeedsX[speedIndex];
            }
            else
            {
                speedU = semanticLayer is { SpeedU: not 0f }
                    ? semanticLayer.SpeedU
                    : FnvEmptyOnamSpeed;
            }

            var speedV = semanticLayer?.SpeedV ?? 0f;
            if (semanticLayer is null && sourceLayerIndex < weather.CloudSpeedsY.Count)
            {
                speedV = weather.CloudSpeedsY[sourceLayerIndex];
            }

            return new Vector2(speedU, speedV);
        }

        if (semanticLayer is not null)
        {
            return new Vector2(semanticLayer.SpeedU, semanticLayer.SpeedV);
        }

        if (weather is null || sourceLayerIndex < 0)
        {
            return Vector2.Zero;
        }

        var x = sourceLayerIndex < weather.CloudSpeedsX.Count
            ? weather.CloudSpeedsX[sourceLayerIndex]
            : 0f;
        var y = sourceLayerIndex < weather.CloudSpeedsY.Count
            ? weather.CloudSpeedsY[sourceLayerIndex]
            : 0f;
        return new Vector2(x, y);
    }

    private static float ResolveScale(WeatherRecord? weather, BethesdaGame game)
    {
        if (!GameProfiles.For(game).UsesLegacyCloudSpeedEncoding)
        {
            return UvPerSecondScale;
        }

        // FNV MemDebug Clouds::Update is exact: ONAM byte/255 × fWeatherCloudSpeedMax(.1)
        // × Sky.fWindSpeed × seconds passed. TES4/FO3 use the same legacy scalar contract.
        return LegacyCloudSpeedMax * ResolveLegacyWind(weather);
    }
}
