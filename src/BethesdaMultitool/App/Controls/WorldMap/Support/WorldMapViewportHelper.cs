using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>
///     Pure math helpers for world map viewport calculations: coordinate transforms,
///     zoom-to-fit, visible bounds, and cell/point visibility tests.
/// </summary>
internal static class WorldMapViewportHelper
{
    private const float MinZoom = 0.001f;
    private const float MaxZoom = 50f;

    /// <summary>
    ///     World-units per cell edge for the given cell (8192 Morrowind, 4096 Fallout-family), with the
    ///     Fallout default when a cell carries no size (e.g. interiors, which are CellWorldSize == 0).
    /// </summary>
    private static float CellSizeOf(CellRecord cell) =>
        cell.CellWorldSize > 0 ? cell.CellWorldSize : WorldGridConstants.CellSize;

    internal static Matrix3x2 GetViewTransform(float zoom, Vector2 panOffset)
    {
        return Matrix3x2.CreateScale(zoom) * Matrix3x2.CreateTranslation(panOffset);
    }

    internal static Vector2 ScreenToWorld(Vector2 screen, float zoom, Vector2 panOffset)
    {
        Matrix3x2.Invert(GetViewTransform(zoom, panOffset), out var inverse);
        return Vector2.Transform(screen, inverse);
    }

    internal static (Vector2 topLeft, Vector2 bottomRight) GetVisibleWorldBounds(
        float canvasWidth, float canvasHeight, float zoom, Vector2 panOffset)
    {
        if (canvasWidth < 1) canvasWidth = 800;
        if (canvasHeight < 1) canvasHeight = 600;

        var tl = ScreenToWorld(Vector2.Zero, zoom, panOffset);
        var br = ScreenToWorld(new Vector2(canvasWidth, canvasHeight), zoom, panOffset);
        return (tl, br);
    }

    internal static bool IsCellVisible(CellRecord cell, Vector2 tlWorld, Vector2 brWorld)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            return false;
        }

        return IsGridCellVisible(
            cell.GridX.Value, cell.GridY.Value, CellSizeOf(cell), tlWorld, brWorld);
    }

    /// <summary>
    ///     Pure grid-cell visibility test shared by terrain/water tile compositing and record-backed
    ///     cell rendering. Unlike <see cref="IsCellVisible" />, this overload does not require an ESM
    ///     record and can therefore cull bitmap-cache keys before best-mip-per-cell selection.
    /// </summary>
    internal static bool IsGridCellVisible(
        int gridX, int gridY, float cellWorldSize,
        Vector2 tlWorld, Vector2 brWorld, float margin = 0f)
    {
        return NormalizeWorldBounds(tlWorld, brWorld, margin)
            .IntersectsCell(gridX, gridY, cellWorldSize);
    }

    /// <summary>
    ///     Normalizes arbitrary viewport corners and applies a non-negative world-space margin.
    ///     Tile renderers call this once per layer, then test all cache entries through the returned
    ///     value to keep the hot loop allocation-free and free of repeated min/max work.
    /// </summary>
    internal static WorldMapGridViewport NormalizeWorldBounds(
        Vector2 tlWorld, Vector2 brWorld, float margin = 0f)
    {
        return WorldMapGridViewport.FromCorners(tlWorld, brWorld, margin);
    }

    internal static bool IsPointInView(float x, float y, Vector2 tlWorld, Vector2 brWorld, float margin)
    {
        var viewMinX = Math.Min(tlWorld.X, brWorld.X) - margin;
        var viewMaxX = Math.Max(tlWorld.X, brWorld.X) + margin;
        var viewMinY = Math.Min(tlWorld.Y, brWorld.Y) - margin;
        var viewMaxY = Math.Max(tlWorld.Y, brWorld.Y) + margin;

        return x >= viewMinX && x <= viewMaxX && y >= viewMinY && y <= viewMaxY;
    }

    internal static float GetObjectViewMargin(PlacedReference obj, WorldViewData? data)
    {
        if (data?.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds) == true)
        {
            var maxExtent = Math.Max(
                Math.Max(Math.Abs(bounds.X2 - bounds.X1), Math.Abs(bounds.Y2 - bounds.Y1)),
                Math.Abs(bounds.Z2 - bounds.Z1)) * obj.Scale;
            return Math.Clamp(maxExtent, 500f, data?.CellWorldSize ?? WorldGridConstants.CellSize);
        }

        return 500f;
    }

    /// <summary>
    ///     Frames the worldspace on the cells that actually carry data, NOT on the full cell list: a
    ///     WRLD's declared extents reach past its authored region (WastelandNV), and the bbox over every
    ///     declared cell puts the view centre near the data's top edge — which the 2D→3D handoff then
    ///     inherits as an out-of-bounds camera.
    /// </summary>
    internal static void ZoomToFitWorldspace(
        List<CellRecord> cells, float canvasWidth, float canvasHeight,
        out float zoom, out Vector2 panOffset)
    {
        zoom = 0.05f;
        panOffset = Vector2.Zero;

        if (WorldMapViewportMath.TryGetOccupiedCellBounds(cells) is not { } bounds) return;

        ZoomToFitBounds(
            bounds.MinX, bounds.MinY, bounds.MaxX, bounds.MaxY,
            canvasWidth, canvasHeight, out zoom, out panOffset);
    }

    internal static void ZoomToFitCell(
        CellRecord cell, float canvasWidth, float canvasHeight,
        out float zoom, out Vector2 panOffset)
    {
        zoom = 0.05f;
        panOffset = Vector2.Zero;

        if (!cell.GridX.HasValue || !cell.GridY.HasValue)
        {
            // Interior cell - zoom based on placed object extent
            if (cell.PlacedObjects.Count > 0)
            {
                var minX = cell.PlacedObjects.Min(o => o.X) - 200;
                var maxX = cell.PlacedObjects.Max(o => o.X) + 200;
                var minY = -cell.PlacedObjects.Max(o => o.Y) - 200;
                var maxY = -cell.PlacedObjects.Min(o => o.Y) + 200;
                ZoomToFitBounds(minX, minY, maxX, maxY, canvasWidth, canvasHeight, out zoom, out panOffset);
            }

            return;
        }

        var cx = cell.GridX.Value;
        var cy = cell.GridY.Value;
        var cellWorldSize = CellSizeOf(cell);
        var worldMinX = cx * cellWorldSize - 200;
        var worldMaxX = (cx + 1) * cellWorldSize + 200;
        var worldMinY = -(cy + 1) * cellWorldSize - 200;
        var worldMaxY = -cy * cellWorldSize + 200;

        ZoomToFitBounds(worldMinX, worldMinY, worldMaxX, worldMaxY, canvasWidth, canvasHeight, out zoom, out panOffset);
    }

    internal static void ZoomToFitBounds(
        float worldMinX, float worldMinY, float worldMaxX, float worldMaxY,
        float canvasWidth, float canvasHeight,
        out float zoom, out Vector2 panOffset)
    {
        if (canvasWidth < 1 || canvasHeight < 1)
        {
            canvasWidth = 800;
            canvasHeight = 600;
        }

        var worldW = worldMaxX - worldMinX;
        var worldH = worldMaxY - worldMinY;
        if (worldW < 1) worldW = 1;
        if (worldH < 1) worldH = 1;

        zoom = Math.Min(canvasWidth / worldW, canvasHeight / worldH) * 0.9f;
        zoom = Math.Clamp(zoom, MinZoom, MaxZoom);

        var centerWorldX = (worldMinX + worldMaxX) * 0.5f;
        var centerWorldY = (worldMinY + worldMaxY) * 0.5f;
        panOffset = new Vector2(
            canvasWidth * 0.5f - centerWorldX * zoom,
            canvasHeight * 0.5f - centerWorldY * zoom);
    }
}
