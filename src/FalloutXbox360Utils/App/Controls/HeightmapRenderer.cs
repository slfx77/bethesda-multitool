using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Pure computation methods for heightmap rendering, extracted from WorldMapControl.
///     These are stateless and can be called from background threads or non-GUI contexts.
/// </summary>
internal static class HeightmapRenderer
{
    /// <summary>LAND heightmap grid dimension (33x33 vertices per cell).</summary>
    internal const int HmGridSize = 33;

    /// <summary>
    ///     Computes grayscale heightmap and water mask from a list of cells.
    ///     Stage 1 of the two-stage pipeline. Can be called from a background thread.
    /// </summary>
    internal static (byte[] Grayscale, byte[] WaterMask, int Width, int Height, int MinCellX, int MaxCellY)?
        ComputeHeightmapData(List<CellRecord> cellSource, float? defaultWaterHeight = null)
    {
        var cells = cellSource
            .Where(c => c.Heightmap != null && c.GridX.HasValue && c.GridY.HasValue)
            .ToList();

        if (cells.Count == 0)
        {
            return null;
        }

        var minX = cells.Min(c => c.GridX!.Value);
        var maxX = cells.Max(c => c.GridX!.Value);
        var minY = cells.Min(c => c.GridY!.Value);
        var maxY = cells.Max(c => c.GridY!.Value);
        var gridW = maxX - minX + 1;
        var gridH = maxY - minY + 1;
        var imgW = gridW * HmGridSize;
        var imgH = gridH * HmGridSize;

        // Compute global height range
        var globalMin = float.MaxValue;
        var globalMax = float.MinValue;
        var heightCache = new Dictionary<CellRecord, float[,]>();

        foreach (var cell in cells)
        {
            var heights = cell.Heightmap!.CalculateHeights();
            heightCache[cell] = heights;
            for (var y = 0; y < HmGridSize; y++)
            {
                for (var x = 0; x < HmGridSize; x++)
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

        var globalRange = globalMax - globalMin;
        if (globalRange < 0.001f)
        {
            globalRange = 1f;
        }

        // Compute grayscale and water mask
        var grayscale = new byte[imgW * imgH];
        var waterMask = new byte[imgW * imgH];

        foreach (var cell in cells)
        {
            var heights = heightCache[cell];
            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;

            // Determine effective water height: cell-specific or worldspace default
            var waterH = cell.WaterHeight;
            if (!waterH.HasValue || waterH.Value is not (> -1e6f and < 1e6f))
            {
                waterH = defaultWaterHeight;
            }

            for (var py = 0; py < HmGridSize; py++)
            {
                for (var px = 0; px < HmGridSize; px++)
                {
                    var height = heights[HmGridSize - 1 - py, px];
                    var normalized = (height - globalMin) / globalRange;
                    var gray = (byte)(Math.Clamp(normalized, 0f, 1f) * 255);

                    var imgX = imgCellX * HmGridSize + px;
                    var imgY = imgCellY * HmGridSize + py;
                    var idx = imgY * imgW + imgX;
                    grayscale[idx] = gray;

                    // Solid water: binary below/above
                    if (waterH.HasValue && waterH.Value is > -1e6f and < 1e6f &&
                        height < waterH.Value)
                    {
                        waterMask[idx] = 180;
                    }
                }
            }
        }

        BlurWaterMask(waterMask, imgW, imgH);

        return (grayscale, waterMask, imgW, imgH, minX, maxY);
    }

    /// <summary>
    ///     Applies color tint and water overlay to grayscale heightmap data.
    ///     Stage 2 of the two-stage pipeline. Fast enough for UI thread (no height recalculation).
    /// </summary>
    internal static byte[] ApplyTintAndWater(
        byte[] grayscale, byte[] waterMask, int width, int height,
        HeightmapColorScheme scheme, bool showWater, byte alpha = 255)
    {
        var pixelCount = width * height;
        var rgba = new byte[pixelCount * 4];

        // Pre-compute tint multipliers (0..1 range)
        var tR = scheme.R / 255f;
        var tG = scheme.G / 255f;
        var tB = scheme.B / 255f;

        // Water color (untinted)
        const byte waterR = 30, waterG = 55, waterB = 120;

        for (var i = 0; i < pixelCount; i++)
        {
            var gray = grayscale[i];

            // Apply tint: grayscale * tint color
            var r = (byte)(gray * tR);
            var g = (byte)(gray * tG);
            var b = (byte)(gray * tB);

            // Apply water overlay (untinted, proportional blend from blurred mask)
            if (showWater && waterMask[i] > 0)
            {
                var waterFactor = waterMask[i] / 255f;
                r = (byte)(r + (waterR - r) * waterFactor);
                g = (byte)(g + (waterG - g) * waterFactor);
                b = (byte)(b + (waterB - b) * waterFactor);
            }

            var idx = i * 4;
            rgba[idx] = r;
            rgba[idx + 1] = g;
            rgba[idx + 2] = b;
            rgba[idx + 3] = alpha;
        }

        return rgba;
    }

    /// <summary>
    ///     Applies a 3x3 box blur to the water mask to smooth hard binary edges.
    /// </summary>
    internal static void BlurWaterMask(byte[] mask, int width, int height)
    {
        var blurred = new byte[mask.Length];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sum = 0;
                var count = 0;
                var y0 = Math.Max(0, y - 1);
                var y1 = Math.Min(height - 1, y + 1);
                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(width - 1, x + 1);

                for (var ny = y0; ny <= y1; ny++)
                {
                    for (var nx = x0; nx <= x1; nx++)
                    {
                        sum += mask[ny * width + nx];
                        count++;
                    }
                }

                blurred[y * width + x] = (byte)(sum / count);
            }
        }

        Array.Copy(blurred, mask, mask.Length);
    }
}
