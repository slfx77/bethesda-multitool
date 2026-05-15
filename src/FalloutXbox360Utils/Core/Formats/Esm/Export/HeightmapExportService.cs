using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

#pragma warning disable CA1822, S2325 // Service instance is intentional for future dependency injection.

/// <summary>
///     Orchestrates heightmap, LAND visual, and worldspace composite export workflows.
/// </summary>
public sealed class HeightmapExportService
{
    public Task ExportStandaloneHeightmapsAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true)
    {
        return HeightmapPngExporter.ExportAsync(heightmaps, cellGrids, outputDir, useColorGradient);
    }

    public Task ExportLandRecordsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return HeightmapPngExporter.ExportLandRecordsAsync(
            landRecords,
            outputDir,
            useColorGradient,
            worldspaceNames);
    }

    public Task ExportLandVisualsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return LandVisualPreviewRenderer.ExportAsync(landRecords, outputDir, worldspaceNames);
    }

    public Task<IReadOnlyList<string>> ExportWorldspaceCompositeWorldmapsAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return WorldspaceCompositeRenderer.ExportAsync(
            heightmaps,
            cellGrids,
            landRecords,
            outputDir,
            useColorGradient,
            worldspaceNames);
    }
}

#pragma warning restore CA1822, S2325

internal static class WorldspaceCompositeRenderer
{
    public static Task<IReadOnlyList<string>> ExportAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord> cellGrids,
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        bool useColorGradient = true,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return HeightmapPngExporter.ExportWorldspaceCompositeWorldmapsAsync(
            heightmaps,
            cellGrids,
            landRecords,
            outputDir,
            useColorGradient,
            worldspaceNames);
    }
}

internal static class LandVisualPreviewRenderer
{
    public static Task ExportAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return HeightmapPngExporter.ExportLandVisualsAsync(landRecords, outputDir, worldspaceNames);
    }
}
