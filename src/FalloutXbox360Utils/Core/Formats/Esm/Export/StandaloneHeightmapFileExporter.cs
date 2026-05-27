using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class StandaloneHeightmapFileExporter
{
    internal static async Task ExportAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true)
    {
        Directory.CreateDirectory(outputDir);

        var scale = useColorGradient
            ? null
            : HeightmapExportScaleCalculator.CalculateVhgtGrayscaleScale(heightmaps.Select(heightmap => heightmap.CalculateHeights()));

        var tasks = heightmaps.Select((heightmap, index) =>
            ExportSingleHeightmapAsync(heightmap, index, cellGrids, outputDir, useColorGradient, scale));

        await Task.WhenAll(tasks);

        if (!useColorGradient && scale.HasValue)
        {
            HeightmapExportScaleCalculator.WriteHeightScaleMetadata(
                Path.Combine(outputDir, "standalone_height_grayscale_scale.csv"),
                "standalone",
                scale.Value);
        }
    }

    /// <summary>
    ///     Export a single heightmap to PNG.
    /// </summary>
    internal static async Task ExportSingleHeightmapAsync(
        DetectedVhgtHeightmap heightmap,
        int index,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true,
        HeightmapGrayscaleScale? grayscaleScale = null)
    {
        var heights = heightmap.CalculateHeights();
        var nearestGrid = HeightmapExportGridMatcher.FindNearestCellGrid(heightmap.Offset, cellGrids);

        byte[] pixels;
        if (useColorGradient)
        {
            pixels = HeightmapColorRenderer.GenerateColorPixels(heights);
        }
        else
        {
            grayscaleScale ??= HeightmapExportScaleCalculator.CalculateVhgtGrayscaleScale([heights]);
            pixels = grayscaleScale.HasValue
                ? HeightmapColorRenderer.GenerateFixedScaleGrayscalePixels(heights, grayscaleScale.Value)
                : HeightmapColorRenderer.GenerateGrayscalePixels(heights);
        }

        var gridSuffix = nearestGrid != null
            ? $"_grid{nearestGrid.GridX}_{nearestGrid.GridY}"
            : "";
        var endianSuffix = heightmap.IsBigEndian ? "_be" : "_le";
        var filename = $"heightmap_{index:D4}{gridSuffix}{endianSuffix}.png";
        var targetDir = Path.Combine(outputDir, "worldspaces", "ws_unknown", "standalone");
        Directory.CreateDirectory(targetDir);
        var path = Path.Combine(targetDir, filename);

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
}
