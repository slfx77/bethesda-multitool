using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Resolves the water type authored by the cell containing a placed-NIF water surface.
///     Exterior lookup uses the loaded worldspace's cell edge length; a lone interior cell has
///     no real grid coordinate, so its own water type is selected directly.
/// </summary>
internal static class PlacedNifWaterCellResolver
{
    internal static uint Resolve(
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cells,
        Vector2 center,
        float cellSize)
    {
        if (cells is null || cells.Count == 0 ||
            !float.IsFinite(center.X) || !float.IsFinite(center.Y))
        {
            return 0;
        }

        if (cells.Count == 1)
        {
            var onlyCell = cells.Values.First();
            if (onlyCell.IsInterior)
            {
                return PositiveWaterFormId(onlyCell);
            }
        }

        var effectiveCellSize = float.IsFinite(cellSize) && cellSize > 0f
            ? cellSize
            : global::BethesdaMultitool.WorldGridConstants.CellSize;
        var gridX = MathF.Floor(center.X / effectiveCellSize);
        var gridY = MathF.Floor(center.Y / effectiveCellSize);
        if (!float.IsFinite(gridX) || !float.IsFinite(gridY) ||
            gridX < int.MinValue || gridX > int.MaxValue ||
            gridY < int.MinValue || gridY > int.MaxValue)
        {
            return 0;
        }

        return cells.TryGetValue(((int)gridX, (int)gridY), out var cell)
            ? PositiveWaterFormId(cell)
            : 0;
    }

    private static uint PositiveWaterFormId(CellRecord cell) =>
        cell.WaterFormId is > 0 ? cell.WaterFormId.Value : 0;
}
