using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;

/// <summary>
///     Compatibility facade for heightmap, LAND visual, and worldspace composite PNG exports.
/// </summary>
public static class HeightmapPngExporter
{
    /// <summary>Exports all detected standalone VHGT heightmaps to PNG files.</summary>
    public static Task ExportAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true)
    {
        return StandaloneHeightmapFileExporter.ExportAsync(heightmaps, cellGrids, outputDir, useColorGradient);
    }

    /// <summary>Exports a single detected VHGT heightmap to a PNG file.</summary>
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

    /// <summary>Exports each LAND record's heightmap to a PNG file.</summary>
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

    /// <summary>Exports LAND visual data (vertex colors / texture layers) as preview images.</summary>
    public static Task ExportLandVisualsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return LandVisualPreviewRenderer.ExportAsync(landRecords, outputDir, worldspaceNames);
    }

    /// <summary>Exports a single LAND record's heightmap to a PNG file.</summary>
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

    /// <summary>Stitches the given heightmaps and cell grids into one composite worldmap PNG.</summary>
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

    /// <summary>Stitches heightmaps, cell grids, and LAND records into one composite worldmap PNG.</summary>
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

    /// <summary>
    ///     Like
    ///     <see
    ///         cref="ExportCompositeWorldmapAsync(List{DetectedVhgtHeightmap}, List{CellGridSubrecord}, List{ExtractedLandRecord}, string, bool)" />
    ///     , but overlays a cell-grid coordinate grid.
    /// </summary>
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

    /// <summary>Produces one composite worldmap PNG per worldspace and returns the written file paths.</summary>
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
