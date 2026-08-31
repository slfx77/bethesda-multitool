using BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

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
        ComputeHeightmapData(
            List<CellRecord> cellSource,
            float? defaultWaterHeight = null,
            WorldRenderCache? cache = null)
    {
        var cells = new List<(CellRecord Cell, DecodedTerrainCell Terrain)>();
        foreach (var cell in cellSource)
        {
            if (!cell.GridX.HasValue || !cell.GridY.HasValue)
            {
                continue;
            }

            var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
            if (terrain.HasTerrain)
            {
                cells.Add((cell, terrain));
            }
        }

        if (cells.Count == 0)
        {
            return null;
        }

        var minX = cells.Min(c => c.Cell.GridX!.Value);
        var maxX = cells.Max(c => c.Cell.GridX!.Value);
        var minY = cells.Min(c => c.Cell.GridY!.Value);
        var maxY = cells.Max(c => c.Cell.GridY!.Value);
        var gridW = maxX - minX + 1;
        var gridH = maxY - minY + 1;
        var imgW = gridW * HmGridSize;
        var imgH = gridH * HmGridSize;

        // Guard against an oversized worldspace (e.g. FO76's APPALACHIA, or any worldspace with terrain
        // cells scattered across a huge grid extent): the two byte buffers below are imgW*imgH each, so a
        // multi-thousand-cell span allocates gigabytes and freezes/OOMs the app. Above the cap we skip
        // the heightmap (the worldspace simply renders without the terrain-derived layers) rather than
        // hang. long math avoids the int overflow a huge product would otherwise silently wrap to.
        const long maxHeightmapPixels = 200_000_000L; // ~200 MB per byte buffer
        if ((long)imgW * imgH > maxHeightmapPixels)
        {
            return null;
        }

        // Compute global height range
        var globalMin = float.MaxValue;
        var globalMax = float.MinValue;

        foreach (var (_, terrain) in cells)
        {
            for (var y = 0; y < HmGridSize; y++)
            {
                for (var x = 0; x < HmGridSize; x++)
                {
                    var h = terrain.HeightAt(x, y);
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

        foreach (var (cell, terrain) in cells)
        {
            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;

            // Determine effective water height. Explicit "no water" sentinel on the cell
            // suppresses water entirely. Null (no XCLW) falls back to worldspace DNAM.
            // Out-of-range numeric values fall back too as a safety net.
            var waterH = WorldRenderCache.ResolveEffectiveWaterHeight(cell, defaultWaterHeight);

            for (var py = 0; py < HmGridSize; py++)
            {
                for (var px = 0; px < HmGridSize; px++)
                {
                    var height = terrain.HeightAt(px, HmGridSize - 1 - py);
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
        var colorful = scheme.Mode == HeightmapRenderMode.Colorful;

        // Water color (untinted)
        const byte waterR = 30, waterG = 55, waterB = 120;

        for (var i = 0; i < pixelCount; i++)
        {
            var gray = grayscale[i];

            byte r, g, b;
            if (colorful)
            {
                // 9-zone HSL elevation gradient. grayscale is the worldspace-global normalized height
                // (0..255 = globalMin..globalMax), exactly the 0..1 input HeightToColor expects.
                (r, g, b) = HeightmapColorRenderer.HeightToColor(gray / 255f);
            }
            else
            {
                // Apply tint: grayscale * tint color
                r = (byte)(gray * tR);
                g = (byte)(gray * tG);
                b = (byte)(gray * tB);
            }

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
