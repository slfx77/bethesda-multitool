using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>Resolves authored WTHR cloud motion without inventing motion for absent or still layers.</summary>
internal static class WeatherCloudMotion
{
    /// <summary>
    ///     Skyrim's normalized QNAM/RNAM axes are multiplied by
    ///     <c>fWeatherCloudSpeedMax * Clouds::Update time scale = 0.1 * 0.1</c>.
    ///     Legacy TES4/FO3/FNV scalar speeds use the authored weather wind instead.
    /// </summary>
    internal const float UvPerSecondScale = 0.010f;
    internal const float LegacyCloudSpeedMax = 0.1f;

    /// <summary>
    ///     Returns per-second UV motion for one source layer. The joined semantic layer is
    ///     authoritative even when both authored values are zero. Parallel arrays are only a legacy
    ///     compatibility projection. Missing data is still, rather than a license to synthesize drift.
    /// </summary>
    internal static Vector2 Resolve(
        WeatherRecord? weather,
        WeatherCloudLayer? semanticLayer,
        int sourceLayerIndex,
        BethesdaGame game = BethesdaGame.Unknown)
    {
        var scale = ResolveScale(weather, game);
        if (semanticLayer is not null)
        {
            return new Vector2(semanticLayer.SpeedU, semanticLayer.SpeedV) * scale;
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
        return new Vector2(x, y) * scale;
    }

    private static float ResolveScale(WeatherRecord? weather, BethesdaGame game)
    {
        if (game is not (BethesdaGame.Oblivion or BethesdaGame.Fallout3 or BethesdaGame.FalloutNewVegas))
        {
            return UvPerSecondScale;
        }

        // FNV MemDebug Clouds::Update is exact: ONAM byte/255 × fWeatherCloudSpeedMax(.1)
        // × Sky.fWindSpeed × seconds passed. TES4/FO3 use the same legacy scalar contract.
        var wind = (weather?.Data?.WindSpeed ?? 0) / 255f;
        return LegacyCloudSpeedMax * wind;
    }
}
