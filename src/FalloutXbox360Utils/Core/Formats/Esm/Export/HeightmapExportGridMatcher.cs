using FalloutXbox360Utils.Core.Formats.Esm.Subrecords;

namespace FalloutXbox360Utils.Core.Formats.Esm.Export;
internal static class HeightmapExportGridMatcher
{
    internal static CellGridSubrecord? FindNearestCellGrid(long heightmapOffset, List<CellGridSubrecord>? cellGrids)
    {
        if (cellGrids == null || cellGrids.Count == 0)
        {
            return null;
        }

        // XCLC typically appears before VHGT in the same record
        // Look for XCLC within ~5000 bytes before the VHGT (widened from 2000)
        return cellGrids
            .Where(g => heightmapOffset - g.Offset is > 0 and < 5000)
            .OrderByDescending(g => g.Offset)
            .FirstOrDefault();
    }

}
