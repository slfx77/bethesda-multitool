using System.Globalization;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class HeightmapExportScaleCalculator
{
    internal static HeightmapGrayscaleScale? CalculateVhgtGrayscaleScale(IEnumerable<float[,]> heightmaps)
    {
        var values = new List<float>();
        foreach (var heights in heightmaps)
        {
            for (var y = 0; y < HeightmapExportConstants.LandVertexCount; y++)
            {
                for (var x = 0; x < HeightmapExportConstants.LandVertexCount; x++)
                {
                    var height = heights[y, x];
                    if (!float.IsNaN(height) && !float.IsInfinity(height))
                    {
                        values.Add(height);
                    }
                }
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var min = values[0];
        var max = values[^1];
        var robustMin = Percentile(values, 0.02f);
        var robustMax = Percentile(values, 0.98f);
        var baseHeight = MathF.Floor(robustMin / HeightmapExportConstants.GrayscaleBucketUnits) * HeightmapExportConstants.GrayscaleBucketUnits;
        var requiredUnitsPerGray = Math.Max(
            HeightmapExportConstants.VhgtQuantizationUnits,
            (robustMax - baseHeight) / 255f);
        var unitsPerGray = MathF.Ceiling(requiredUnitsPerGray / HeightmapExportConstants.VhgtQuantizationUnits) * HeightmapExportConstants.VhgtQuantizationUnits;
        if (unitsPerGray < HeightmapExportConstants.VhgtQuantizationUnits)
        {
            unitsPerGray = HeightmapExportConstants.VhgtQuantizationUnits;
        }

        var maxEncoded = baseHeight + unitsPerGray * 255f;
        var clippedLow = values.Count(v => v < baseHeight);
        var clippedHigh = values.Count(v => v > maxEncoded);

        return new HeightmapGrayscaleScale(
            baseHeight,
            unitsPerGray,
            min,
            max,
            clippedLow,
            clippedHigh,
            values.Count);
    }

    internal static void WriteHeightScaleMetadata(string path, string scope, HeightmapGrayscaleScale scale)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        var invariant = CultureInfo.InvariantCulture;
        var lines = new[]
        {
            "Scope,BaseHeight,UnitsPerGray,MaxEncodedHeight,MinHeight,MaxHeight,SampleCount,ClippedLowCount,ClippedHighCount",
            string.Join(
                ',',
                EscapeCsv(scope),
                scale.BaseHeight.ToString("R", invariant),
                scale.UnitsPerGray.ToString("R", invariant),
                scale.MaxEncodedHeight.ToString("R", invariant),
                scale.MinHeight.ToString("R", invariant),
                scale.MaxHeight.ToString("R", invariant),
                scale.SampleCount.ToString(invariant),
                scale.ClippedLowCount.ToString(invariant),
                scale.ClippedHighCount.ToString(invariant))
        };
        File.WriteAllLines(path, lines);
    }

    internal static string EscapeCsv(string value)
    {
        if (!value.Contains('"') && !value.Contains(',') && !value.Contains('\n') && !value.Contains('\r'))
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static (float Min, float Max)? CalculateRobustHeightRange(IEnumerable<float[,]> heightmaps)
    {
        var values = new List<float>();
        foreach (var heights in heightmaps)
        {
            for (var y = 0; y < HeightmapExportConstants.LandVertexCount; y++)
            {
                for (var x = 0; x < HeightmapExportConstants.LandVertexCount; x++)
                {
                    var height = heights[y, x];
                    if (!float.IsNaN(height) && !float.IsInfinity(height))
                    {
                        values.Add(height);
                    }
                }
            }
        }

        if (values.Count == 0)
        {
            return null;
        }

        values.Sort();
        var min = Percentile(values, 0.02f);
        var max = Percentile(values, 0.98f);
        if (max - min < 0.001f)
        {
            min = values[0];
            max = values[^1];
        }

        if (max - min < 0.001f)
        {
            max = min + 1f;
        }

        return (min, max);
    }

    internal static float Percentile(IReadOnlyList<float> sortedValues, float percentile)
    {
        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        var index = Math.Clamp(percentile, 0f, 1f) * (sortedValues.Count - 1);
        var lower = (int)MathF.Floor(index);
        var upper = Math.Min(lower + 1, sortedValues.Count - 1);
        var fraction = index - lower;
        return sortedValues[lower] * (1f - fraction) + sortedValues[upper] * fraction;
    }
}
