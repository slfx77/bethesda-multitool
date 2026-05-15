using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;
using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

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
