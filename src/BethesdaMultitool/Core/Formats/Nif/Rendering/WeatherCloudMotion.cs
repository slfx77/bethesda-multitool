using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering;

/// <summary>Resolves authored WTHR cloud motion without inventing motion for absent or still layers.</summary>
internal static class WeatherCloudMotion
{
    /// <summary>
    ///     Skyrim's normalized QNAM/RNAM values are multiplied by
    ///     <c>fWeatherCloudSpeedMax * Clouds::Update time scale = 0.1 * 0.1</c>.
    /// </summary>
    internal const float UvPerSecondScale = 0.010f;

    /// <summary>
    ///     Returns per-second UV motion for one source layer. The joined semantic layer is
    ///     authoritative even when both authored values are zero. Parallel arrays are only a legacy
    ///     compatibility projection. Missing data is still, rather than a license to synthesize drift.
    /// </summary>
    internal static Vector2 Resolve(
        WeatherRecord? weather,
        WeatherCloudLayer? semanticLayer,
        int sourceLayerIndex)
    {
        if (semanticLayer is not null)
        {
            return new Vector2(semanticLayer.SpeedU, semanticLayer.SpeedV) * UvPerSecondScale;
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
        return new Vector2(x, y) * UvPerSecondScale;
    }
}
