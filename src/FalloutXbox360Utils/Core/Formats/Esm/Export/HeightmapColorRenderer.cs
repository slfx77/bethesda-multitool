using ImageMagick;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Handles color scheme application, pixel rendering, and PNG writing for heightmap images.
///     Uses a 9-zone HSL color gradient for terrain visualization, adapted from EsmAnalyzer.
/// </summary>
internal static class HeightmapColorRenderer
{
    #region Pixel Generation

    /// <summary>
    ///     Generates color (RGB) pixel data from a 33x33 height grid, normalized per-cell.
    /// </summary>
    internal static byte[] GenerateColorPixels(float[,] heights)
    {
        var pixels = new byte[33 * 33 * 3];

        // Get height range for normalization
        var (minH, maxH) = GetHeightRange(heights);
        var range = maxH - minH;
        if (range < 0.001f)
        {
            range = 1f;
        }

        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                // Row 0 = south edge, row 32 = north edge.
                // Flip Y so north is at the top of the image.
                var normalized = (heights[32 - y, x] - minH) / range;
                var (r, g, b) = HeightToColor(normalized);

                var idx = (y * 33 + x) * 3;
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
            }
        }

        return pixels;
    }

    /// <summary>
    ///     Generates grayscale (8-bit) pixel data from a 33x33 height grid, normalized per-cell.
    /// </summary>
    internal static byte[] GenerateGrayscalePixels(float[,] heights)
    {
        var pixels = new byte[33 * 33];

        // Get height range for normalization
        var (minH, maxH) = GetHeightRange(heights);
        var range = maxH - minH;
        if (range < 0.001f)
        {
            range = 1f;
        }

        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                // Row 0 = south edge, row 32 = north edge.
                // Flip Y so north is at the top of the image.
                var normalized = (heights[32 - y, x] - minH) / range;
                pixels[y * 33 + x] = (byte)(normalized * 255);
            }
        }

        return pixels;
    }

    /// <summary>
    ///     Returns the min and max height values from a 33x33 height grid.
    /// </summary>
    internal static (float min, float max) GetHeightRange(float[,] heights)
    {
        var min = float.MaxValue;
        var max = float.MinValue;

        for (var y = 0; y < 33; y++)
        {
            for (var x = 0; x < 33; x++)
            {
                var h = heights[y, x];
                if (h < min)
                {
                    min = h;
                }

                if (h > max)
                {
                    max = h;
                }
            }
        }

        return (min, max);
    }

    #endregion

    #region Color Gradient (adapted from EsmAnalyzer)

    /// <summary>
    ///     Converts a normalized height value (0-1) to a color using data-driven transitions.
    ///     Based on height analysis: 80% of terrain is in 0.21-0.54 range, median at 0.37.
    ///     Uses HIGH SATURATION for best detail. Mountains: Brown -> Red -> Pink -> White.
    /// </summary>
    internal static (byte r, byte g, byte b) HeightToColor(float normalizedHeight)
    {
        // Clamp to 0-1 range
        normalizedHeight = Math.Clamp(normalizedHeight, 0f, 1f);

        float h, s, l;

        if (normalizedHeight < 0.10f)
        {
            // Deep areas: Dark blue
            var t = normalizedHeight / 0.10f;
            h = 220f;
            s = 0.90f;
            l = 0.25f + t * 0.10f; // 0.25 -> 0.35
        }
        else if (normalizedHeight < 0.21f)
        {
            // Low areas: Blue -> Cyan (bright)
            var t = (normalizedHeight - 0.10f) / 0.11f;
            h = 220f - t * 40f; // 220 -> 180
            s = 0.90f;
            l = 0.35f + t * 0.13f; // 0.35 -> 0.48
        }
        else if (normalizedHeight < 0.27f)
        {
            // Cyan -> Lime: DARKER lime
            var t = (normalizedHeight - 0.21f) / 0.06f;
            h = 180f - t * 67f; // 180 -> 113 (cyan -> lime-green)
            s = 0.85f;
            l = 0.48f - t * 0.24f; // 0.48 -> 0.24 (dark lime)
        }
        else if (normalizedHeight < 0.34f)
        {
            // Lime -> Yellow: BRIGHTEN
            var t = (normalizedHeight - 0.27f) / 0.07f;
            h = 113f - t * 54f; // 113 -> 59 (lime -> yellow)
            s = 0.85f;
            l = 0.24f + t * 0.18f; // 0.24 -> 0.42 (brighten to yellow)
        }
        else if (normalizedHeight < 0.45f)
        {
            // Yellow -> Orange: BRIGHTEN
            var t = (normalizedHeight - 0.34f) / 0.11f;
            h = 59f - t * 35f; // 59 -> 24
            s = 0.85f - t * 0.03f; // 0.85 -> 0.82
            l = 0.42f + t * 0.03f; // 0.42 -> 0.45 (brighter orange)
        }
        else if (normalizedHeight < 0.54f)
        {
            // Orange -> Brown-red: darken
            var t = (normalizedHeight - 0.45f) / 0.09f;
            h = 24f - t * 8f; // 24 -> 16
            s = 0.82f - t * 0.02f; // 0.82 -> 0.80
            l = 0.45f - t * 0.15f; // 0.45 -> 0.30
        }
        else if (normalizedHeight < 0.65f)
        {
            // Brown-red -> Red: DARKEN
            var t = (normalizedHeight - 0.54f) / 0.11f;
            h = 16f - t * 11f; // 16 -> 5 (toward red)
            s = 0.80f + t * 0.05f; // 0.80 -> 0.85
            l = 0.30f + t * 0.14f; // 0.30 -> 0.44 (builds up to red zone)
        }
        else if (normalizedHeight < 0.78f)
        {
            // Red -> Pink: stay more red
            var t = (normalizedHeight - 0.65f) / 0.13f;
            h = 5f - t * 1f; // 5 -> 4 (stay red, slight shift)
            s = 0.85f - t * 0.08f; // 0.85 -> 0.77
            l = 0.44f + t * 0.11f; // 0.44 -> 0.55
        }
        else if (normalizedHeight < 0.90f)
        {
            // Pink -> Light pink: go SHORT way (4 -> 324 via 360, subtract 40)
            var t = (normalizedHeight - 0.78f) / 0.12f;
            h = 4f - t * 40f; // 4 -> -36 (wraps to 324)
            if (h < 0f)
            {
                h += 360f;
            }

            s = 0.77f - t * 0.17f; // 0.77 -> 0.60
            l = 0.55f + t * 0.10f; // 0.55 -> 0.65
        }
        else
        {
            // Peaks: Light pink -> White (continuous from previous zone)
            var t = (normalizedHeight - 0.90f) / 0.10f;
            h = 324f - t * 4f; // 324 -> 320 (continue pink hue)
            s = 0.60f - t * 0.55f; // 0.60 -> 0.05 (fade to white)
            l = 0.65f + t * 0.30f; // 0.65 -> 0.95 (brighten to white)
        }

        return HslToRgb(h, s, l);
    }

    /// <summary>
    ///     Converts HSL color to RGB.
    /// </summary>
    /// <param name="h">Hue in degrees (0-360)</param>
    /// <param name="s">Saturation (0-1)</param>
    /// <param name="l">Lightness (0-1)</param>
    internal static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
    {
        if (s < 0.001f)
        {
            // Achromatic (gray)
            var gray = (byte)(l * 255);
            return (gray, gray, gray);
        }

        h /= 360f; // Normalize hue to 0-1

        var q = l < 0.5f ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;

        var r = HueToRgb(p, q, h + 1f / 3f);
        var g = HueToRgb(p, q, h);
        var b = HueToRgb(p, q, h - 1f / 3f);

        return ((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }

    private static float HueToRgb(float p, float q, float t)
    {
        if (t < 0)
        {
            t += 1;
        }

        if (t > 1)
        {
            t -= 1;
        }

        if (t < 1f / 6f)
        {
            return p + (q - p) * 6 * t;
        }

        if (t < 1f / 2f)
        {
            return q;
        }

        if (t < 2f / 3f)
        {
            return p + (q - p) * (2f / 3f - t) * 6;
        }

        return p;
    }

    #endregion

    #region PNG Writing (adapted from EsmAnalyzer)

    /// <summary>
    ///     Saves a grayscale image (8-bit) to PNG.
    /// </summary>
    internal static void SaveGrayscale(byte[] pixels, int width, int height, string path)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = MagickFormat.Gray,
            Depth = 8
        };

        using var image = new MagickImage(pixels, settings);
        image.Write(path, MagickFormat.Png);
    }

    /// <summary>
    ///     Saves an RGB image (24-bit) to PNG.
    /// </summary>
    internal static void SaveRgb(byte[] pixels, int width, int height, string path)
    {
        var settings = new MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = MagickFormat.Rgb,
            Depth = 8
        };

        using var image = new MagickImage(pixels, settings);
        image.Write(path, MagickFormat.Png);
    }

    #endregion
}
