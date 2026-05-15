namespace FalloutXbox360Utils.Core.Formats.Esm.Export;

internal static class HeightmapExportGroups
{
    internal static Dictionary<(int x, int y), byte[]> GetRgbWorldspaceGroup(
        Dictionary<uint, Dictionary<(int x, int y), byte[]>> groupedPixels,
        uint worldspaceFormId)
    {
        if (!groupedPixels.TryGetValue(worldspaceFormId, out var group))
        {
            group = [];
            groupedPixels[worldspaceFormId] = group;
        }

        return group;
    }
}
