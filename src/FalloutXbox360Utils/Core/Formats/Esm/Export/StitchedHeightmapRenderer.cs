using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Renders per-worldspace stitched heightmap PNGs from a flat list of
///     <see cref="ExtractedLandRecord" />, mirroring the
///     <c>tools/EsmAnalyzer/Commands/ExportWorldmapCommand</c> output but
///     consumable from any code path that produces <c>ExtractedLandRecord</c>s
///     (e.g. DMP scans). Per worldspace: groups cells by grid position, computes
///     a global min/range across all cells, then paints each 33×33 cell into a
///     single combined image. Empty grid positions stay default-fill.
/// </summary>
internal static class StitchedHeightmapRenderer
{
    private const int CellGridSize = 33;
    private const byte EmptyR = 128;
    private const byte EmptyG = 128;
    private const byte EmptyB = 128;

    /// <summary>
    ///     Render one stitched PNG per worldspace. Returns the per-worldspace
    ///     output paths so callers can log them.
    /// </summary>
    /// <param name="lands">LAND records — only those with non-null Heightmap and resolvable cell coords are stitched.</param>
    /// <param name="outputDir">Output directory. PNG files written as <c>stitched_ws{HEX}_{name}.png</c>.</param>
    /// <param name="scale">Pixel scale (1 = native 33px/cell).</param>
    /// <param name="worldspaceNames">Optional FormID → name map for filename suffixes.</param>
    public static List<string> RenderPerWorldspace(
        IEnumerable<ExtractedLandRecord> lands,
        string outputDir,
        int scale = 1,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        Directory.CreateDirectory(outputDir);
        var paths = new List<string>();

        var groups = lands
            .Where(l => l.Heightmap != null
                        && l.WorldspaceFormId is > 0
                        && l.BestCellX.HasValue
                        && l.BestCellY.HasValue)
            .GroupBy(l => l.WorldspaceFormId!.Value);

        foreach (var group in groups)
        {
            var worldspaceFormId = group.Key;
            var cellHeights = new Dictionary<(int X, int Y), float[,]>();
            foreach (var land in group)
            {
                var key = (land.BestCellX!.Value, land.BestCellY!.Value);
                cellHeights[key] = land.Heightmap!.CalculateHeights();
            }

            if (cellHeights.Count == 0) continue;

            var path = RenderSingleWorldspace(
                worldspaceFormId, cellHeights, outputDir, scale, worldspaceNames);
            if (path != null) paths.Add(path);
        }

        return paths;
    }

    private static string? RenderSingleWorldspace(
        uint worldspaceFormId,
        Dictionary<(int X, int Y), float[,]> cellHeights,
        string outputDir,
        int scale,
        IReadOnlyDictionary<uint, string>? worldspaceNames)
    {
        var minX = cellHeights.Keys.Min(k => k.X);
        var maxX = cellHeights.Keys.Max(k => k.X);
        var minY = cellHeights.Keys.Min(k => k.Y);
        var maxY = cellHeights.Keys.Max(k => k.Y);
        var cellsWide = maxX - minX + 1;
        var cellsTall = maxY - minY + 1;
        var imageWidth = cellsWide * CellGridSize * scale;
        var imageHeight = cellsTall * CellGridSize * scale;

        // Global min/range across all cells in this worldspace.
        var globalMin = float.MaxValue;
        var globalMax = float.MinValue;
        foreach (var heights in cellHeights.Values)
        {
            for (var y = 0; y < CellGridSize; y++)
            for (var x = 0; x < CellGridSize; x++)
            {
                var h = heights[x, y];
                if (float.IsNaN(h) || float.IsInfinity(h)) continue;
                if (h < globalMin) globalMin = h;
                if (h > globalMax) globalMax = h;
            }
        }

        var range = globalMax - globalMin;
        if (range < 0.001f) range = 1f;

        // RGB buffer (3 bytes/pixel; mirrors HeightmapColorRenderer.SaveRgb).
        var pixels = new byte[imageWidth * imageHeight * 3];
        for (var i = 0; i < pixels.Length; i += 3)
        {
            pixels[i] = EmptyR;
            pixels[i + 1] = EmptyG;
            pixels[i + 2] = EmptyB;
        }

        foreach (var ((cellX, cellY), heights) in cellHeights)
        {
            var basePixelX = (cellX - minX) * CellGridSize * scale;
            var basePixelY = (maxY - cellY) * CellGridSize * scale; // flip Y: north up

            for (var localY = 0; localY < CellGridSize; localY++)
            {
                for (var localX = 0; localX < CellGridSize; localX++)
                {
                    var h = heights[localX, localY];
                    if (float.IsNaN(h) || float.IsInfinity(h)) continue;
                    var normalized = (h - globalMin) / range;
                    var (r, g, b) = HeightmapColorRenderer.HeightToColor(normalized);
                    var flippedLocalY = CellGridSize - 1 - localY;

                    for (var sy = 0; sy < scale; sy++)
                    {
                        for (var sx = 0; sx < scale; sx++)
                        {
                            var px = basePixelX + (localX * scale) + sx;
                            var py = basePixelY + (flippedLocalY * scale) + sy;
                            if (px < 0 || px >= imageWidth || py < 0 || py >= imageHeight) continue;
                            var idx = ((py * imageWidth) + px) * 3;
                            pixels[idx] = r;
                            pixels[idx + 1] = g;
                            pixels[idx + 2] = b;
                        }
                    }
                }
            }
        }

        var suffix = HeightmapExportPathBuilder.BuildWorldspaceFileSuffix(worldspaceFormId, worldspaceNames);
        var filename = $"stitched{suffix}.png";
        var path = Path.Combine(outputDir, filename);
        HeightmapColorRenderer.SaveRgb(pixels, imageWidth, imageHeight, path);
        return path;
    }
}
