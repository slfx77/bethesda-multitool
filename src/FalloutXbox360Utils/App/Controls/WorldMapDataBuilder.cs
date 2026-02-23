using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Pure data-building helpers for cell grid lookups and cell browser list items.
///     Extracted from WorldMapControl to keep computation separate from UI.
/// </summary>
internal static class WorldMapDataBuilder
{
    /// <summary>
    ///     Builds a lookup dictionary from grid coordinates to cell records.
    /// </summary>
    internal static Dictionary<(int x, int y), CellRecord> BuildCellGridLookup(List<CellRecord> cells)
    {
        var lookup = new Dictionary<(int x, int y), CellRecord>();
        foreach (var cell in cells)
        {
            if (cell.GridX.HasValue && cell.GridY.HasValue)
            {
                lookup.TryAdd((cell.GridX.Value, cell.GridY.Value), cell);
            }
        }

        return lookup;
    }
}
