using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool;

/// <summary>
///     Pure viewport math for the 2D world map: centre-preserving resize and the canvas-world bounds
///     of the cells that actually carry data. Lives in the shared (non-GUI) compile set so it can be
///     unit tested on the net10.0 TFM, like <see cref="WorldMapBoundsMath" />.
/// </summary>
internal static class WorldMapViewportMath
{
    /// <summary>
    ///     A canvas-world rectangle. X grows east; Y is the NEGATED grid Y (north is min-Y), matching
    ///     <c>WorldMapViewportHelper.GetViewTransform</c> and the cell rects drawn in the overview.
    /// </summary>
    internal readonly record struct CanvasBounds(float MinX, float MinY, float MaxX, float MaxY)
    {
        internal Vector2 Center => new((MinX + MaxX) * 0.5f, (MinY + MaxY) * 0.5f);

        /// <summary>Clamps a canvas-world point into this rectangle (component-wise).</summary>
        internal Vector2 Clamp(Vector2 canvasPoint) => new(
            Math.Clamp(canvasPoint.X, MinX, MaxX),
            Math.Clamp(canvasPoint.Y, MinY, MaxY));
    }

    /// <summary>
    ///     Structural terrain test — cheap (no LAND decode), unlike
    ///     <c>DecodedTerrainCell.HasTerrain</c>, which the heightmap builder uses per cell. Both answer
    ///     the same question; this one is the form a whole-worldspace bounds pass can afford.
    /// </summary>
    internal static bool HasTerrainData(CellRecord cell) =>
        cell.Heightmap is not null ||
        cell.LandVisualData?.HasAny == true ||
        cell.RuntimeTerrainMesh is not null;

    /// <summary>
    ///     A cell carries authored data when it has terrain or at least one placed reference. A WRLD's
    ///     declared extents are NOT data: WastelandNV's cell list reaches well past its authored region,
    ///     so framing on every grid cell centres the view off the playable area.
    /// </summary>
    internal static bool HasAuthoredData(CellRecord cell) =>
        HasTerrainData(cell) || cell.PlacedObjects.Count > 0;

    /// <summary>
    ///     Canvas-world bounds of the cells that carry authored data, falling back to every grid-bearing
    ///     cell when none qualify (a worldspace loaded without terrain or references must still be
    ///     framable). Null when no cell has grid coordinates at all.
    /// </summary>
    internal static CanvasBounds? TryGetOccupiedCellBounds(IReadOnlyList<CellRecord> cells)
    {
        var cellWorldSize = ResolveCellWorldSize(cells);
        return Accumulate(cells, cellWorldSize, requireAuthoredData: true)
               ?? Accumulate(cells, cellWorldSize, requireAuthoredData: false);
    }

    /// <summary>
    ///     Re-derives the pan offset after a canvas resize so the world point that was centred stays
    ///     centred. Pan is a SCREEN-space translation (screen = world × zoom + pan), so leaving it alone
    ///     across a resize pins the top-left corner and drifts the centre by half the size delta — the
    ///     maximize case. Degenerate zoom or either size below one pixel returns the offset unchanged
    ///     (nothing meaningful was centred to preserve).
    /// </summary>
    internal static Vector2 PreserveCenterOnResize(
        Vector2 panOffset, float zoom,
        float oldWidth, float oldHeight, float newWidth, float newHeight)
    {
        if (!float.IsFinite(zoom) || zoom <= 0f) return panOffset;
        if (oldWidth < 1f || oldHeight < 1f || newWidth < 1f || newHeight < 1f) return panOffset;

        var centerWorld = new Vector2(
            (oldWidth * 0.5f - panOffset.X) / zoom,
            (oldHeight * 0.5f - panOffset.Y) / zoom);

        return new Vector2(
            newWidth * 0.5f - centerWorld.X * zoom,
            newHeight * 0.5f - centerWorld.Y * zoom);
    }

    /// <summary>
    ///     Cell edge in world units, taken from the first grid-bearing cell (8192 Morrowind, 4096
    ///     Fallout-family). Interiors carry CellWorldSize == 0, hence the Fallout default.
    /// </summary>
    private static float ResolveCellWorldSize(IReadOnlyList<CellRecord> cells)
    {
        foreach (var cell in cells)
        {
            if (cell.GridX is null || cell.GridY is null) continue;
            return cell.CellWorldSize > 0 ? cell.CellWorldSize : WorldGridConstants.CellSize;
        }

        return WorldGridConstants.CellSize;
    }

    private static CanvasBounds? Accumulate(
        IReadOnlyList<CellRecord> cells, float cellWorldSize, bool requireAuthoredData)
    {
        var found = false;
        int minGridX = 0, maxGridX = 0, minGridY = 0, maxGridY = 0;

        foreach (var cell in cells)
        {
            if (cell.GridX is not int gridX || cell.GridY is not int gridY) continue;
            if (requireAuthoredData && !HasAuthoredData(cell)) continue;

            if (!found)
            {
                minGridX = maxGridX = gridX;
                minGridY = maxGridY = gridY;
                found = true;
                continue;
            }

            if (gridX < minGridX) minGridX = gridX;
            if (gridX > maxGridX) maxGridX = gridX;
            if (gridY < minGridY) minGridY = gridY;
            if (gridY > maxGridY) maxGridY = gridY;
        }

        if (!found) return null;

        return new CanvasBounds(
            minGridX * cellWorldSize,
            -(maxGridY + 1) * cellWorldSize,
            (maxGridX + 1) * cellWorldSize,
            -minGridY * cellWorldSize);
    }
}
