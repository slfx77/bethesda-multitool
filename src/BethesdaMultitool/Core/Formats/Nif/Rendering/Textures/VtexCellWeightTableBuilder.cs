using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     Builds one cell's Morrowind VTEX weight table with cross-cell boundary ownership.
///     East, north, and northeast boundary vertices use the adjacent cell's first texture square;
///     absent neighbors retain the underlying table builder's own-edge clamp.
/// </summary>
internal static class VtexCellWeightTableBuilder
{
    internal static CellLayerWeightTable Build(
        int gridSize,
        (int gx, int gy) key,
        uint[] vtex,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cells)
    {
        uint[]? eastVtex = null, northVtex = null, northEastVtex = null;
        if (cells is not null)
        {
            eastVtex = NeighborGrid(cells, key.gx + 1, key.gy);
            northVtex = NeighborGrid(cells, key.gx, key.gy + 1);
            northEastVtex = NeighborGrid(cells, key.gx + 1, key.gy + 1);
        }

        return CellLayerWeightTable.BuildFromVtexGrid(
            gridSize,
            vtex,
            eastVtexFormIds: eastVtex,
            northVtexFormIds: northVtex,
            northEastVtexFormIds: northEastVtex);
    }

    private static uint[]? NeighborGrid(
        IReadOnlyDictionary<(int gx, int gy), CellRecord> cells, int gx, int gy)
    {
        return cells.TryGetValue((gx, gy), out var neighbor)
            ? neighbor.LandVisualData?.VtexTextureFormIds
            : null;
    }
}
