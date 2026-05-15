using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

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
