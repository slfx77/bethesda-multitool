using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Export;

/// <summary>
///     Conservative CPU broadphase over the content inventory supplied to the tiled 3D export. This
///     mirrors the widest cell working set used by the renderers rather than the tile's tighter clip
///     volume: a square around the tile cylinder, widened by one cell for renderer exit hysteresis.
///     It does not claim that the renderer's history-dependent placed-water inventory is complete.
/// </summary>
internal static class ExportTileContentCoverage
{
    /// <summary>
    ///     Returns <see langword="true" /> when the tile may produce pixels. Cell centers represent
    ///     complete cell footprints, so the center test includes half a cell beyond the renderer's
    ///     one-cell-widened working square. Already-accumulated placed NIF water is tested by its own
    ///     world bounds because it is consumed directly by the water renderer and no longer depends
    ///     on its reference's home cell being visited.
    ///     <para>
    ///         Invalid inputs fail open: an uncertain tile is rendered rather than incorrectly
    ///         omitted from the stitched image or tile manifest.
    ///     </para>
    /// </summary>
    public static bool MayContainContent(
        VisibilityCylinder tileCylinder,
        float cellSize,
        IReadOnlyList<(float X, float Y)> cellCenters,
        IReadOnlyList<NifWaterGeometry> placedNifWater)
    {
        if (cellCenters is null || placedNifWater is null ||
            !IsFinite(tileCylinder.Position) ||
            !float.IsFinite(tileCylinder.Radius) || tileCylinder.Radius < 0f ||
            tileCylinder.Radius >= float.MaxValue ||
            !float.IsFinite(cellSize) || cellSize <= 0f)
        {
            return true;
        }

        // ReferenceRenderer12 and the terrain/water cache gathers retain one extra cell at their
        // exit edge. Expand once more by half a cell because the input stores centers, not AABBs.
        var workingHalfExtent = (double)tileCylinder.Radius + cellSize;
        var cellCenterReach = workingHalfExtent + ((double)cellSize * 0.5d);
        if (!double.IsFinite(workingHalfExtent) || !double.IsFinite(cellCenterReach))
        {
            return true;
        }

        for (var i = 0; i < cellCenters.Count; i++)
        {
            var cell = cellCenters[i];
            if (!float.IsFinite(cell.X) || !float.IsFinite(cell.Y))
            {
                return true;
            }

            var dx = Math.Abs((double)cell.X - tileCylinder.Position.X);
            var dy = Math.Abs((double)cell.Y - tileCylinder.Position.Y);
            if (dx <= cellCenterReach && dy <= cellCenterReach)
            {
                return true;
            }
        }

        var minX = (double)tileCylinder.Position.X - workingHalfExtent;
        var maxX = (double)tileCylinder.Position.X + workingHalfExtent;
        var minY = (double)tileCylinder.Position.Y - workingHalfExtent;
        var maxY = (double)tileCylinder.Position.Y + workingHalfExtent;
        for (var i = 0; i < placedNifWater.Count; i++)
        {
            var geometry = placedNifWater[i];
            if (geometry is null || !HasValidBounds(geometry.BoundsMin, geometry.BoundsMax))
            {
                return true;
            }

            if (geometry.BoundsMax.X >= minX && geometry.BoundsMin.X <= maxX &&
                geometry.BoundsMax.Y >= minY && geometry.BoundsMin.Y <= maxY)
            {
                return true;
            }
        }

        return false;
    }

    private static bool HasValidBounds(Vector3 min, Vector3 max) =>
        IsFinite(min) && IsFinite(max) &&
        min.X <= max.X && min.Y <= max.Y && min.Z <= max.Z;

    private static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}
