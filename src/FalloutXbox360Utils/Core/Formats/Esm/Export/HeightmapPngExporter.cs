using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

/// <summary>
///     Compatibility facade for heightmap, LAND visual, and worldspace composite PNG exports.
/// </summary>
public static class HeightmapPngExporter
{
    public static Task ExportAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true)
    {
        return StandaloneHeightmapFileExporter.ExportAsync(heightmaps, cellGrids, outputDir, useColorGradient);
    }

    public static Task ExportSingleHeightmapAsync(
        DetectedVhgtHeightmap heightmap,
        int index,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true,
        HeightmapGrayscaleScale? grayscaleScale = null)
    {
        return StandaloneHeightmapFileExporter.ExportSingleHeightmapAsync(
            heightmap,
            index,
            cellGrids,
            outputDir,
            useColorGradient,
            grayscaleScale);
    }

    public static Task ExportLandRecordsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return LandRecordHeightmapExporter.ExportLandRecordsAsync(
            landRecords,
            outputDir,
            useColorGradient,
            worldspaceNames);
    }

    public static Task ExportLandVisualsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return LandVisualPreviewRenderer.ExportAsync(landRecords, outputDir, worldspaceNames);
    }

    public static Task ExportLandRecordAsync(
        ExtractedLandRecord land,
        int index,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null,
        HeightmapGrayscaleScale? grayscaleScale = null)
    {
        return LandRecordHeightmapExporter.ExportLandRecordAsync(
            land,
            index,
            outputDir,
            useColorGradient,
            worldspaceNames,
            grayscaleScale);
    }

    public static Task ExportCompositeWorldmapAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        string outputPath,
        bool useColorGradient = true)
    {
        return WorldspaceCompositeMapRenderer.ExportCompositeWorldmapAsync(
            heightmaps,
            cellGrids,
            outputPath,
            useColorGradient);
    }

    public static Task ExportCompositeWorldmapAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputPath,
        bool useColorGradient = true)
    {
        return WorldspaceCompositeMapRenderer.ExportCompositeWorldmapAsync(
            heightmaps,
            cellGrids,
            landRecords,
            outputPath,
            useColorGradient);
    }

    public static Task ExportCompositeWorldmapWithGridAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputPath,
        bool useColorGradient = true)
    {
        return WorldspaceCompositeMapRenderer.ExportCompositeWorldmapWithGridAsync(
            heightmaps,
            cellGrids,
            landRecords,
            outputPath,
            useColorGradient);
    }

    public static Task<IReadOnlyList<string>> ExportWorldspaceCompositeWorldmapsAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return WorldspaceCompositeMapRenderer.ExportWorldspaceCompositeWorldmapsAsync(
            heightmaps,
            cellGrids,
            landRecords,
            outputDir,
            useColorGradient,
            worldspaceNames);
    }
}
