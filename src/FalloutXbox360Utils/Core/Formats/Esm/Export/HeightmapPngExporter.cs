using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

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
            pixels = HeightmapColorRenderer.GenerateColorPixels(heights);
        }
        else
        {
            pixels = HeightmapColorRenderer.GenerateGrayscalePixels(heights);
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
                HeightmapColorRenderer.SaveRgb(pixels, 33, 33, path);
            }
            else
            {
                HeightmapColorRenderer.SaveGrayscale(pixels, 33, 33, path);
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
            pixels = HeightmapColorRenderer.GenerateColorPixels(heights);
        }
        else
        {
            pixels = HeightmapColorRenderer.GenerateGrayscalePixels(heights);
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
                HeightmapColorRenderer.SaveRgb(pixels, 33, 33, path);
            }
            else
            {
                HeightmapColorRenderer.SaveGrayscale(pixels, 33, 33, path);
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
        var precomputedHeights = new Dictionary<(int x, int y), float[,]>();

        // Primary source: use LAND records with pre-computed heights.
        // Call CalculateHeights() on the LandHeightmap directly to preserve ExactHeights
        // from runtime terrain mesh data (avoids lossy delta re-encoding).
        foreach (var land in landRecords)
        {
            if (land.BestCellX.HasValue && land.BestCellY.HasValue && land.Heightmap != null)
            {
                var key = (land.BestCellX.Value, land.BestCellY.Value);
                if (!precomputedHeights.ContainsKey(key))
                {
                    precomputedHeights[key] = land.Heightmap.CalculateHeights();
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
                if (!precomputedHeights.ContainsKey(key))
                {
                    precomputedHeights[key] = heightmap.CalculateHeights();
                }
            }
        }

        if (precomputedHeights.Count == 0)
        {
            return;
        }

        await RenderCompositeFromHeightsAsync(precomputedHeights, outputPath, useColorGradient);
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
                        var (r, g, b) = HeightmapColorRenderer.HeightToColor(normalized);
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
                HeightmapColorRenderer.SaveRgb(compositePixels, imgWidth, imgHeight, outputPath);
            }
            else
            {
                HeightmapColorRenderer.SaveGrayscale(compositePixels, imgWidth, imgHeight, outputPath);
            }
        });
    }

    /// <summary>
    ///     Render a composite worldmap from precomputed height arrays (float[33,33] per cell).
    /// </summary>
    private static async Task RenderCompositeFromHeightsAsync(
        Dictionary<(int x, int y), float[,]> precomputedHeights,
        string outputPath,
        bool useColorGradient)
    {
        var minX = precomputedHeights.Keys.Min(k => k.x);
        var maxX = precomputedHeights.Keys.Max(k => k.x);
        var minY = precomputedHeights.Keys.Min(k => k.y);
        var maxY = precomputedHeights.Keys.Max(k => k.y);

        var gridWidth = maxX - minX + 1;
        var gridHeight = maxY - minY + 1;

        var imgWidth = gridWidth * 33;
        var imgHeight = gridHeight * 33;

        byte[] compositePixels;
        if (useColorGradient)
        {
            compositePixels = new byte[imgWidth * imgHeight * 3];
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

        var globalMin = float.MaxValue;
        var globalMax = float.MinValue;

        foreach (var heights in precomputedHeights.Values)
        {
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

        foreach (var kvp in precomputedHeights)
        {
            var (cellX, cellY) = kvp.Key;
            var heights = kvp.Value;

            var imgCellX = cellX - minX;
            var imgCellY = maxY - cellY;

            for (var py = 0; py < 33; py++)
            {
                for (var px = 0; px < 33; px++)
                {
                    var height = heights[32 - py, px];
                    var normalized = (height - globalMin) / globalRange;

                    var imgX = imgCellX * 33 + px;
                    var imgY = imgCellY * 33 + py;

                    if (useColorGradient)
                    {
                        var (r, g, b) = HeightmapColorRenderer.HeightToColor(normalized);
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
                HeightmapColorRenderer.SaveRgb(compositePixels, imgWidth, imgHeight, outputPath);
            }
            else
            {
                HeightmapColorRenderer.SaveGrayscale(compositePixels, imgWidth, imgHeight, outputPath);
            }
        });
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
}
