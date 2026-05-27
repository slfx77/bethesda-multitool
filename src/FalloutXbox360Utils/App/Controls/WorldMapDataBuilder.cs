using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;

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
            if (!cell.GridX.HasValue || !cell.GridY.HasValue)
            {
                continue;
            }

            var key = (cell.GridX.Value, cell.GridY.Value);
            if (!lookup.TryGetValue(key, out var existing) || PreferGridLookupCell(cell, existing))
            {
                lookup[key] = cell;
            }
        }

        return lookup;
    }

    private static bool PreferGridLookupCell(CellRecord candidate, CellRecord existing)
    {
        if (candidate.PlacedObjects.Count != existing.PlacedObjects.Count)
        {
            return candidate.PlacedObjects.Count > existing.PlacedObjects.Count;
        }

        if (candidate.IsVirtual != existing.IsVirtual)
        {
            return !candidate.IsVirtual;
        }

        if (candidate.IsUnresolvedBucket != existing.IsUnresolvedBucket)
        {
            return !candidate.IsUnresolvedBucket;
        }

        var candidateHasTerrain = HasTerrain(candidate);
        var existingHasTerrain = HasTerrain(existing);
        if (candidateHasTerrain != existingHasTerrain)
        {
            return candidateHasTerrain;
        }

        return candidate.FormId < existing.FormId;
    }

    private static bool HasTerrain(CellRecord cell) =>
        cell.Heightmap is not null ||
        cell.LandVisualData?.HasAny == true ||
        cell.RuntimeTerrainMesh is not null;
}
