using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Esm.Subrecords;

namespace BethesdaMultitool.Core.Formats.Esm.Export;

internal static class WorldspaceCompositeRenderer
{
    /// <summary>Exports one composite worldmap PNG per worldspace and returns the written file paths.</summary>
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
