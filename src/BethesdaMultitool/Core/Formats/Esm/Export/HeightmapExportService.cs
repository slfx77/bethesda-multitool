using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Export;

#pragma warning disable CA1822, S2325 // Service instance is intentional for future dependency injection.

/// <summary>
///     Orchestrates heightmap, LAND visual, and worldspace composite export workflows.
/// </summary>
public sealed class HeightmapExportService
{
    /// <summary>Exports detected standalone VHGT heightmaps to PNG files.</summary>
    public Task ExportStandaloneHeightmapsAsync(
        List<DetectedVhgtHeightmap> heightmaps,
        List<CellGridSubrecord>? cellGrids,
        string outputDir,
        bool useColorGradient = true)
    {
        return HeightmapPngExporter.ExportAsync(heightmaps, cellGrids, outputDir, useColorGradient);
    }

    /// <summary>Exports each LAND record's heightmap to a PNG file.</summary>
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

    /// <summary>Exports LAND visual data (vertex colors / texture layers) as preview images.</summary>
    public Task ExportLandVisualsAsync(
        List<ExtractedLandRecord> landRecords,
        string outputDir,
        IReadOnlyDictionary<uint, string>? worldspaceNames = null)
    {
        return LandVisualPreviewRenderer.ExportAsync(landRecords, outputDir, worldspaceNames);
    }

    /// <summary>Stitches per-cell heightmaps and LAND records into one composite worldmap PNG per worldspace.</summary>
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
