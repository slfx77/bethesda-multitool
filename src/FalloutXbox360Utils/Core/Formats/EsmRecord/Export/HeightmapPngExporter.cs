using FalloutXbox360Utils.Core.Formats.EsmRecord.Models;
using FalloutXbox360Utils.Core.Formats.EsmRecord.Subrecords;
using ImageMagick;

namespace FalloutXbox360Utils.Core.Formats.EsmRecord.Export;

/// <summary>
///     Exports detected VHGT heightmaps as color PNG images.
///     Color gradient adapted from EsmAnalyzer ExportCommands.Worldmap.HeightToColor().
///     Uses a 9-zone HSL color gradient for terrain visualization.
/// </summary>
public static class HeightmapPngExporter
{
    /// <summary>
    ///     Export all detected heightmaps to PNG files.
    /// </summary>
    /// <param name="heightmaps">List of detected VHGT heightmaps.</param>
    /// <param name="cellGrids">List of detected XCLC cell grids for naming.</param>
    /// <param name="outputDir">Output directory for PNG files.</param>
    /// <param name="useColorGradient">If true, uses terrain color gradient. If false, uses grayscale.</param>
    public static async Task ExportAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true)
    {
        Directory.CreateDirectory(outputDir);

        var tasks = heightmaps.Select((heightmap, index) =>
            ExportSingleHeightmapAsync(heightmap, index, cellGrids, outputDir, useColorGradient));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    ///     Export a single heightmap to PNG.
    /// </summary>
    public static async Task ExportSingleHeightmapAsync(
        DetectedVhgtHeightmap heightmap,
        int index,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true)
    {
        var heights = heightmap.CalculateHeights();
        var nearestGrid = FindNearestCellGrid(heightmap.Offset, cellGrids);

        byte[] pixels;
        if (useColorGradient)
        {
            pixels = GenerateColorPixels(heights);
        }
        else
        {
            pixels = GenerateGrayscalePixels(heights);
        }

        var gridSuffix = nearestGrid != null
            ? $"_grid{nearestGrid.GridX}_{nearestGrid.GridY}"
            : "";
        var endianSuffix = heightmap.IsBigEndian ? "_be" : "_le";
        var filename = $"heightmap_{index:D4}{gridSuffix}{endianSuffix}.png";
        var path = Path.Combine(outputDir, filename);

        await Task.Run(() =>
        {
            if (useColorGradient)
            {
                SaveRgb(pixels, 33, 33, path);
            }
            else
            {
                SaveGrayscale(pixels, 33, 33, path);
            }
        });
    }

    /// <summary>
    ///     Export all extracted LAND records with heightmaps to PNG files.
    /// </summary>
    public static async Task ExportLandRecordsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true)
    {
        Directory.CreateDirectory(outputDir);

        var tasks = landRecords
            .Where(l => l.Heightmap != null)
            .Select((land, index) => ExportLandRecordAsync(land, index, outputDir, useColorGradient));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    ///     Export a single LAND record heightmap to PNG.
    /// </summary>
    public static async Task ExportLandRecordAsync(
        ExtractedLandRecord land,
        int index,
        string outputDir,
        bool useColorGradient = true)
    {
        if (land.Heightmap == null)
        {
            return;
        }

        var heights = land.Heightmap.CalculateHeights();

        byte[] pixels;
        if (useColorGradient)
        {
            pixels = GenerateColorPixels(heights);
        }
        else
        {
            pixels = GenerateGrayscalePixels(heights);
        }

        var gridSuffix = land.BestCellX.HasValue && land.BestCellY.HasValue
            ? $"_cell{land.BestCellX}_{land.BestCellY}"
            : "";
        var filename = $"land_{land.Header.FormId:X8}{gridSuffix}.png";
        var path = Path.Combine(outputDir, filename);

        await Task.Run(() =>
        {
            if (useColorGradient)
            {
                SaveRgb(pixels, 33, 33, path);
            }
            else
            {
                SaveGrayscale(pixels, 33, 33, path);
            }
        });
    }

    /// <summary>
    ///     Generate a composite worldmap image from all detected heightmaps.
    ///     Positions heightmaps based on their cell grid coordinates.
    /// </summary>
    public static async Task ExportCompositeWorldmapAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        string outputPath,
        bool useColorGradient = true)
    {
        // Correlate heightmaps to cell grids using XCLC proximity
        var correlatedHeightmaps = new Dictionary<(int x, int y), DetectedVhgtHeightmap>();

        foreach (var heightmap in heightmaps)
        {
            var nearestGrid = FindNearestCellGrid(heightmap.Offset, cellGrids);
            if (nearestGrid != null)
            {
                var key = (nearestGrid.GridX, nearestGrid.GridY);
                if (!correlatedHeightmaps.ContainsKey(key))
                {
                    correlatedHeightmaps[key] = heightmap;
                }
            }
        }

        if (correlatedHeightmaps.Count == 0)
        {
            return;
        }

        await RenderCompositeAsync(correlatedHeightmaps, outputPath, useColorGradient);
    }

    /// <summary>
    ///     Shared rendering logic for composite worldmaps.
    /// </summary>
    private static async Task RenderCompositeAsync(
        Dictionary<(int x, int y), DetectedVhgtHeightmap> correlatedHeightmaps,
        string outputPath,
        bool useColorGradient)
    {
        // Calculate bounds
        var minX = correlatedHeightmaps.Keys.Min(k => k.x);
        var maxX = correlatedHeightmaps.Keys.Max(k => k.x);
        var minY = correlatedHeightmaps.Keys.Min(k => k.y);
        var maxY = correlatedHeightmaps.Keys.Max(k => k.y);

        var gridWidth = maxX - minX + 1;
        var gridHeight = maxY - minY + 1;

        // Create composite image (33x33 pixels per cell)
        var imgWidth = gridWidth * 33;
        var imgHeight = gridHeight * 33;

        byte[] compositePixels;
        if (useColorGradient)
        {
            compositePixels = new byte[imgWidth * imgHeight * 3];
            // Initialize to background color (dark gray)
            for (var i = 0; i < compositePixels.Length; i += 3)
            {
                compositePixels[i] = 32;
                compositePixels[i + 1] = 32;
                compositePixels[i + 2] = 32;
            }
        }
        else
        {
            compositePixels = new byte[imgWidth * imgHeight];
            Array.Fill(compositePixels, (byte)32);
        }

        // Calculate global height range using absolute min/max.
        // No percentile clamping — preserves true height relationships for comparison
        // against full-map exports from EsmAnalyzer.
        var globalMin = float.MaxValue;
        var globalMax = float.MinValue;

        foreach (var kvp in correlatedHeightmaps)
        {
            var heights = kvp.Value.CalculateHeights();
            for (var y = 0; y < 33; y++)
            {
                for (var x = 0; x < 33; x++)
                {
                    var h = heights[y, x];
                    if (h < globalMin)
                    {
                        globalMin = h;
                    }

                    if (h > globalMax)
                    {
                        globalMax = h;
                    }
                }
            }
        }

        if (globalMin >= globalMax)
        {
            return;
        }

        var globalRange = globalMax - globalMin;
        if (globalRange < 0.001f)
        {
            globalRange = 1f;
        }

        // Render each heightmap into the composite
        foreach (var kvp in correlatedHeightmaps)
        {
            var (cellX, cellY) = kvp.Key;
            var heightmap = kvp.Value;
            var heights = heightmap.CalculateHeights();

            // Calculate position in composite image
            // Y is inverted (cell Y increases northward, but image Y increases downward)
            var imgCellX = cellX - minX;
            var imgCellY = maxY - cellY;

            for (var py = 0; py < 33; py++)
            {
                for (var px = 0; px < 33; px++)
                {
                    // VHGT row 0 = south edge, row 32 = north edge.
                    // Image py=0 is the top (north), so flip the Y index.
                    var height = heights[32 - py, px];
                    var normalized = (height - globalMin) / globalRange;

                    var imgX = imgCellX * 33 + px;
                    var imgY = imgCellY * 33 + py;

                    if (useColorGradient)
                    {
                        var (r, g, b) = HeightToColor(normalized);
                        var idx = (imgY * imgWidth + imgX) * 3;
                        compositePixels[idx] = r;
                        compositePixels[idx + 1] = g;
                        compositePixels[idx + 2] = b;
                    }
                    else
                    {
                        var gray = (byte)(normalized * 255);
                        var idx = imgY * imgWidth + imgX;
                        compositePixels[idx] = gray;
                    }
                }
            }
        }

        await Task.Run(() =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? ".");
            if (useColorGradient)
            {
                SaveRgb(compositePixels, imgWidth, imgHeight, outputPath);
            }
            else
            {
                SaveGrayscale(compositePixels, imgWidth, imgHeight, outputPath);
            }
        });
    }

    /// <summary>
    ///     Generate a composite worldmap using LAND records as primary positioning source.
    ///     LAND records contain proper CellX/CellY from record parsing, providing
    ///     more reliable positioning than XCLC proximity matching.
    ///     Runtime cell coordinates (RuntimeCellX/RuntimeCellY) are preferred over ESM-derived.
    /// </summary>
    public static async Task ExportCompositeWorldmapAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputPath,
        bool useColorGradient = true)
    {
        var correlatedHeightmaps = new Dictionary<(int x, int y), DetectedVhgtHeightmap>();

        // Primary source: use LAND records that have both cell coordinates and heightmap data.
        // Build DetectedVhgtHeightmap directly from the LAND record's own decompressed VHGT,
        // since standalone VHGT detections may be in different memory regions.
        foreach (var land in landRecords)
        {
            if (land.BestCellX.HasValue && land.BestCellY.HasValue && land.Heightmap != null)
            {
                var key = (land.BestCellX.Value, land.BestCellY.Value);
                if (!correlatedHeightmaps.ContainsKey(key))
                {
                    correlatedHeightmaps[key] = new DetectedVhgtHeightmap
                    {
                        Offset = land.Heightmap.Offset,
                        IsBigEndian = land.Header.IsBigEndian,
                        HeightOffset = land.Heightmap.HeightOffset,
                        HeightDeltas = land.Heightmap.HeightDeltas
                    };
                }
            }
        }

        // Fallback: use XCLC proximity for standalone VHGT heightmaps not covered by LAND records
        foreach (var heightmap in heightmaps)
        {
            var nearestGrid = FindNearestCellGrid(heightmap.Offset, cellGrids);
            if (nearestGrid != null)
            {
                var key = (nearestGrid.GridX, nearestGrid.GridY);
                if (!correlatedHeightmaps.ContainsKey(key))
                {
                    correlatedHeightmaps[key] = heightmap;
                }
            }
        }

        if (correlatedHeightmaps.Count == 0)
        {
            return;
        }

        // Delegate to the shared rendering logic
        await RenderCompositeAsync(correlatedHeightmaps, outputPath, useColorGradient);
    }

    #region Helper Methods

    private static CellGridSubrecord? FindNearestCellGrid(long heightmapOffset, List<CellGridSubrecord>? cellGrids)
    {
        if (cellGrids == null || cellGrids.Count == 0)
        {
            return null;
        }

        // XCLC typically appears before VHGT in the same record
        // Look for XCLC within ~5000 bytes before the VHGT (widened from 2000)
        return cellGrids
            .Where(g => heightmapOffset - g.Offset is > 0 and < 5000)
            .OrderByDescending(g => g.Offset)
            .FirstOrDefault();
    }

    #endregion

    #region Pixel Generation

    private static byte[] GenerateColorPixels(float[,] heights)
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
                var normalized = (heights[y, x] - minH) / range;
                var (r, g, b) = HeightToColor(normalized);

                var idx = (y * 33 + x) * 3;
                pixels[idx] = r;
                pixels[idx + 1] = g;
                pixels[idx + 2] = b;
            }
        }

        return pixels;
    }

    private static byte[] GenerateGrayscalePixels(float[,] heights)
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
                var normalized = (heights[y, x] - minH) / range;
                pixels[y * 33 + x] = (byte)(normalized * 255);
            }
        }

        return pixels;
    }

    private static (float min, float max) GetHeightRange(float[,] heights)
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
    private static (byte r, byte g, byte b) HeightToColor(float normalizedHeight)
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
    private static (byte r, byte g, byte b) HslToRgb(float h, float s, float l)
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

        return t < 1f / 6f ? p + (q - p) * 6 * t : t < 1f / 2f ? q : t < 2f / 3f ? p + (q - p) * (2f / 3f - t) * 6 : p;
    }

    #endregion

    #region PNG Writing (adapted from EsmAnalyzer)

    /// <summary>
    ///     Saves a grayscale image (8-bit) to PNG.
    /// </summary>
    private static void SaveGrayscale(byte[] pixels, int width, int height, string path)
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
    private static void SaveRgb(byte[] pixels, int width, int height, string path)
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
