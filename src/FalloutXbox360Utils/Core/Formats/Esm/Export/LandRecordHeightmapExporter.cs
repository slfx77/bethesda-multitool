using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class LandRecordHeightmapExporter
{
    internal static async Task ExportLandRecordsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        Directory.CreateDirectory(outputDir);

        var landsWithHeightmaps = landRecords
            .Where(l => l.Heightmap != null)
            .ToList();

        var grayscaleScalesByWorldspace = useColorGradient
            ? null
            : landsWithHeightmaps
                .GroupBy(land => land.WorldspaceFormId ?? 0u)
                .Select(group => (
                    WorldspaceFormId: group.Key,
                    Scale: HeightmapExportScaleCalculator.CalculateVhgtGrayscaleScale(group.Select(land => land.Heightmap!.CalculateHeights()))))
                .Where(row => row.Scale.HasValue)
                .ToDictionary(row => row.WorldspaceFormId, row => row.Scale!.Value);

        if (grayscaleScalesByWorldspace != null)
        {
            foreach (var (worldspaceFormId, scale) in grayscaleScalesByWorldspace)
            {
                var dir = Path.Combine(
                    outputDir,
                    "worldspaces",
                    HeightmapExportPathBuilder.BuildWorldspaceDirName(worldspaceFormId, worldspaceNames));
                HeightmapExportScaleCalculator.WriteHeightScaleMetadata(
                    Path.Combine(dir, "height_grayscale_scale.csv"),
                    HeightmapExportPathBuilder.BuildWorldspaceDirName(worldspaceFormId, worldspaceNames),
                    scale);
            }
        }

        var tasks = landsWithHeightmaps
            .Select((land, index) => ExportLandRecordAsync(
                land,
                index,
                outputDir,
                useColorGradient,
                worldspaceNames,
                grayscaleScalesByWorldspace != null &&
                grayscaleScalesByWorldspace.TryGetValue(land.WorldspaceFormId ?? 0u, out var scale)
                    ? scale
                    : null));

        await Task.WhenAll(tasks);
    }

    /// <summary>
    ///     Export a single LAND record heightmap to PNG.
    /// </summary>
    internal static async Task ExportLandRecordAsync(
        ExtractedLandRecord land,
        int index,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null,
        HeightmapGrayscaleScale? grayscaleScale = null)
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
            grayscaleScale ??= HeightmapExportScaleCalculator.CalculateVhgtGrayscaleScale([heights]);
            pixels = grayscaleScale.HasValue
                ? HeightmapColorRenderer.GenerateFixedScaleGrayscalePixels(heights, grayscaleScale.Value)
                : HeightmapColorRenderer.GenerateGrayscalePixels(heights);
        }

        var gridSuffix = land.BestCellX.HasValue && land.BestCellY.HasValue
            ? $"_cell{land.BestCellX}_{land.BestCellY}"
            : "";
        var worldspaceSuffix = land.WorldspaceFormId is uint ws
            ? HeightmapExportPathBuilder.BuildWorldspaceFileSuffix(ws, worldspaceNames)
            : "";
        var filename = $"land_{land.Header.FormId:X8}{worldspaceSuffix}{gridSuffix}.png";
        var targetDir = Path.Combine(
            outputDir,
            "worldspaces",
            HeightmapExportPathBuilder.BuildWorldspaceDirName(land.WorldspaceFormId ?? 0u, worldspaceNames));
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
