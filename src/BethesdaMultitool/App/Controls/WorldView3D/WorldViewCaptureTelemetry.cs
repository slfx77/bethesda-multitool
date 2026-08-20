using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering;

namespace BethesdaMultitool;

/// <summary>
///     Pure static helpers for the profiler scene capture's WTHR/cloud/ambient telemetry
///     (weather-band sampling, cloud-layer rows, RGBA math) plus the shadow-dump readback
///     analyzer. Carved out of <c>WorldView3DControl.SceneCapture.cs</c>; these read authored
///     record data only and never touch viewer state.
/// </summary>
internal static class WorldViewCaptureTelemetry
{
    private static readonly Logger Log = Logger.Instance;

    internal static int[] CaptureCloudSourceIndices(WeatherRecord? weather)
    {
        if (weather is null)
        {
            return [];
        }

        if (weather.CloudLayers.Count > 0)
        {
            return weather.CloudLayers
                .Where(layer => !string.IsNullOrWhiteSpace(layer.Texture))
                .Select(layer => layer.SourceIndex)
                .Distinct()
                .Order()
                .ToArray();
        }

        if (weather.CloudLayerSourceIndices.Count > 0)
        {
            return weather.CloudLayerSourceIndices.Distinct().Order().ToArray();
        }

        return Enumerable.Range(0, weather.CloudLayerTextures.Count).ToArray();
    }

    internal static (float[] U, float[] V) CaptureCloudSpeeds(
        WeatherRecord? weather,
        int[] sourceIndices)
    {
        var u = new float[sourceIndices.Length];
        var v = new float[sourceIndices.Length];
        if (weather is null)
        {
            return (u, v);
        }

        for (var i = 0; i < sourceIndices.Length; i++)
        {
            var sourceIndex = sourceIndices[i];
            var layer = weather.FindCloudLayerBySourceIndex(sourceIndex);
            u[i] = layer?.SpeedU ??
                   (sourceIndex < weather.CloudSpeedsX.Count ? weather.CloudSpeedsX[sourceIndex] : 0f);
            v[i] = layer?.SpeedV ??
                   (sourceIndex < weather.CloudSpeedsY.Count ? weather.CloudSpeedsY[sourceIndex] : 0f);
        }

        return (u, v);
    }

    internal static Dictionary<string, object?>[] CaptureWeatherColorBands(
        WeatherRecord? weather,
        AtmosphereState.WeatherBandBlend band)
    {
        if (weather?.Colors.Count is not > 0)
        {
            return [];
        }

        var result = new Dictionary<string, object?>[weather.Colors.Count];
        for (var i = 0; i < weather.Colors.Count; i++)
        {
            var color = weather.Colors[i];
            var from = EffectiveColorBand(color, band.From);
            var to = EffectiveColorBand(color, band.To);
            result[i] = new Dictionary<string, object?>
            {
                ["index"] = i,
                ["category"] = Enum.IsDefined<WeatherColorType>((WeatherColorType)i)
                    ? ((WeatherColorType)i).ToString()
                    : $"Unknown{i}",
                ["fromBand"] = band.From.ToString(),
                ["toBand"] = band.To.ToString(),
                ["fromAuthored"] = IsColorBandAuthored(color, band.From),
                ["toAuthored"] = IsColorBandAuthored(color, band.To),
                ["fromRgba8"] = Rgba8(from),
                ["toRgba8"] = Rgba8(to),
                ["sampledRgba"] = LerpRgba(from, to, band.ToWeight)
            };
        }

        return result;
    }

    internal static Dictionary<string, object?>[] CaptureCloudLayers(
        WeatherRecord? weather,
        int[] sourceIndices,
        AtmosphereState.WeatherBandBlend band)
    {
        if (weather is null || sourceIndices.Length == 0)
        {
            return [];
        }

        var result = new Dictionary<string, object?>[sourceIndices.Length];
        for (var i = 0; i < sourceIndices.Length; i++)
        {
            var sourceIndex = sourceIndices[i];
            var layer = weather.FindCloudLayerBySourceIndex(sourceIndex);
            var speedU = layer?.SpeedU ??
                         (sourceIndex < weather.CloudSpeedsX.Count
                             ? weather.CloudSpeedsX[sourceIndex]
                             : 0f);
            var speedV = layer?.SpeedV ??
                         (sourceIndex < weather.CloudSpeedsY.Count
                             ? weather.CloudSpeedsY[sourceIndex]
                             : 0f);
            Dictionary<string, object?>? colorBand = null;
            if (layer?.Color is { } layerColor)
            {
                var from = EffectiveColorBand(layerColor, band.From);
                var to = EffectiveColorBand(layerColor, band.To);
                colorBand = new Dictionary<string, object?>
                {
                    ["fromRgba8"] = Rgba8(from),
                    ["toRgba8"] = Rgba8(to),
                    ["sampledRgba"] = LerpRgba(from, to, band.ToWeight)
                };
            }

            Dictionary<string, object?>? opacityBand = null;
            if (layer?.Opacity is { } layerOpacity)
            {
                var from = EffectiveOpacityBand(layerOpacity.Bands, band.From);
                var to = EffectiveOpacityBand(layerOpacity.Bands, band.To);
                opacityBand = new Dictionary<string, object?>
                {
                    ["from"] = from,
                    ["to"] = to,
                    ["sampled"] = from + (to - from) * band.ToWeight
                };
            }

            result[i] = new Dictionary<string, object?>
            {
                ["sourceIndex"] = sourceIndex,
                ["texture"] = layer?.Texture,
                ["speedU"] = speedU,
                ["speedV"] = speedV,
                ["colorBand"] = colorBand,
                ["colorUnavailableReason"] = layer?.Color is null
                    ? "the authored cloud slot has no retained PNAM color row"
                    : null,
                ["opacityBand"] = opacityBand,
                ["opacityUnavailableReason"] = layer?.Opacity is null
                    ? "the authored cloud slot has no retained JNAM opacity row"
                    : null
            };
        }

        return result;
    }

    internal static Dictionary<string, object?>? CaptureAmbientCubeBand(
        WeatherRecord? weather,
        AtmosphereState.WeatherBandBlend band)
    {
        if (weather?.DirectionalAmbientCubes is not { } cubes)
        {
            return null;
        }

        var from = EffectiveAmbientCubeBand(cubes, band.From);
        var to = EffectiveAmbientCubeBand(cubes, band.To);
        return new Dictionary<string, object?>
        {
            ["fromBand"] = band.From.ToString(),
            ["toBand"] = band.To.ToString(),
            ["toWeight"] = band.ToWeight,
            ["from"] = AmbientCube(from),
            ["to"] = AmbientCube(to)
        };
    }

    private static WeatherAmbientCube EffectiveAmbientCubeBand(
        WeatherTimeBands<WeatherAmbientCube> bands,
        AtmosphereState.WeatherBandKind band) => band switch
    {
        AtmosphereState.WeatherBandKind.Night => bands.Night,
        AtmosphereState.WeatherBandKind.EarlySunrise => bands.EarlySunrise ?? bands.Sunrise,
        AtmosphereState.WeatherBandKind.Sunrise => bands.Sunrise,
        AtmosphereState.WeatherBandKind.LateSunrise => bands.LateSunrise ?? bands.Sunrise,
        AtmosphereState.WeatherBandKind.Day or AtmosphereState.WeatherBandKind.HighNoon => bands.Day,
        AtmosphereState.WeatherBandKind.EarlySunset => bands.EarlySunset ?? bands.Sunset,
        AtmosphereState.WeatherBandKind.Sunset => bands.Sunset,
        AtmosphereState.WeatherBandKind.LateSunset => bands.LateSunset ?? bands.Sunset,
        _ => bands.Day
    };

    private static Dictionary<string, object?> AmbientCube(WeatherAmbientCube cube) => new()
    {
        ["positiveX"] = Rgba8(cube.PositiveX),
        ["negativeX"] = Rgba8(cube.NegativeX),
        ["positiveY"] = Rgba8(cube.PositiveY),
        ["negativeY"] = Rgba8(cube.NegativeY),
        ["positiveZ"] = Rgba8(cube.PositiveZ),
        ["negativeZ"] = Rgba8(cube.NegativeZ),
        ["specular"] = cube.Specular is { } specular ? Rgba8(specular) : null,
        ["fresnelPower"] = cube.FresnelPower
    };

    private static WeatherRgba EffectiveColorBand(
        WeatherColor color,
        AtmosphereState.WeatherBandKind band) => band switch
    {
        AtmosphereState.WeatherBandKind.Night => color.Night,
        AtmosphereState.WeatherBandKind.EarlySunrise => color.EarlySunrise ?? color.Sunrise,
        AtmosphereState.WeatherBandKind.Sunrise => color.Sunrise,
        AtmosphereState.WeatherBandKind.LateSunrise => color.LateSunrise ?? color.Sunrise,
        AtmosphereState.WeatherBandKind.Day => color.Day,
        AtmosphereState.WeatherBandKind.HighNoon => color.Bands.HighNoon ?? color.Day,
        AtmosphereState.WeatherBandKind.EarlySunset => color.EarlySunset ?? color.Sunset,
        AtmosphereState.WeatherBandKind.Sunset => color.Sunset,
        AtmosphereState.WeatherBandKind.LateSunset => color.LateSunset ?? color.Sunset,
        _ => color.Day
    };

    private static bool IsColorBandAuthored(
        WeatherColor color,
        AtmosphereState.WeatherBandKind band) => band switch
    {
        AtmosphereState.WeatherBandKind.HighNoon => color.Bands.HighNoon.HasValue,
        AtmosphereState.WeatherBandKind.EarlySunrise => color.Bands.EarlySunrise.HasValue,
        AtmosphereState.WeatherBandKind.LateSunrise => color.Bands.LateSunrise.HasValue,
        AtmosphereState.WeatherBandKind.EarlySunset => color.Bands.EarlySunset.HasValue,
        AtmosphereState.WeatherBandKind.LateSunset => color.Bands.LateSunset.HasValue,
        _ => true
    };

    private static float EffectiveOpacityBand(
        WeatherTimeBands<float> bands,
        AtmosphereState.WeatherBandKind band) => band switch
    {
        AtmosphereState.WeatherBandKind.Night => bands.Night,
        AtmosphereState.WeatherBandKind.EarlySunrise => bands.EarlySunrise ?? bands.Sunrise,
        AtmosphereState.WeatherBandKind.Sunrise => bands.Sunrise,
        AtmosphereState.WeatherBandKind.LateSunrise => bands.LateSunrise ?? bands.Sunrise,
        AtmosphereState.WeatherBandKind.Day or AtmosphereState.WeatherBandKind.HighNoon => bands.Day,
        AtmosphereState.WeatherBandKind.EarlySunset => bands.EarlySunset ?? bands.Sunset,
        AtmosphereState.WeatherBandKind.Sunset => bands.Sunset,
        AtmosphereState.WeatherBandKind.LateSunset => bands.LateSunset ?? bands.Sunset,
        _ => bands.Day
    };

    private static byte[] Rgba8(WeatherRgba color) => [color.R, color.G, color.B, color.A];

    private static float[] LerpRgba(WeatherRgba from, WeatherRgba to, float toWeight)
    {
        const float scale = 1f / 255f;
        toWeight = Math.Clamp(toWeight, 0f, 1f);
        return
        [
            (from.R + (to.R - from.R) * toWeight) * scale,
            (from.G + (to.G - from.G) * toWeight) * scale,
            (from.B + (to.B - from.B) * toWeight) * scale,
            (from.A + (to.A - from.A) * toWeight) * scale
        ];
    }

    /// <summary>
    ///     Maps a shadow-cascade readback buffer and logs its occupancy stats (the
    ///     FALLOUT_VIEWER_SHADOW_DUMP diagnostic). Synchronous on purpose: the mapped pointer
    ///     must not live across an await, so the map/scan/unmap stays out of the async capture
    ///     method. Disposes the buffer.
    /// </summary>
    internal static unsafe void AnalyzeAndLogShadowDump(
        Vortice.Direct3D12.ID3D12Resource dumpBuffer, int resolution, uint rowPitch, int cascade)
    {
        void* p = null;
        dumpBuffer.Map(0, &p).CheckError();
        try
        {
            long nonZero = 0;
            float maxV = 0f, minNz = float.MaxValue;
            int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
            for (var y = 0; y < resolution; y++)
            {
                var row = (float*)((byte*)p + (long)y * rowPitch);
                for (var x = 0; x < resolution; x++)
                {
                    var v = row[x];
                    if (v <= 0f) continue;
                    nonZero++;
                    if (v > maxV) maxV = v;
                    if (v < minNz) minNz = v;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }

            Log.Info(
                "[ShadowDump] cascade={0} res={1} nonZero={2} ({3:0.000}%) range=[{4:0.00000},{5:0.00000}] bbox=({6},{7})-({8},{9})",
                cascade, resolution, nonZero, 100.0 * nonZero / ((long)resolution * resolution), minNz, maxV, minX,
                minY, maxX, maxY);
        }
        finally
        {
            dumpBuffer.Unmap(0, null);
            dumpBuffer.Dispose();
        }
    }
}
