using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models;

namespace FalloutXbox360Utils;

/// <summary>
///     Pure math helpers for world map viewport calculations: coordinate transforms,
///     zoom-to-fit, visible bounds, and cell/point visibility tests.
/// </summary>
internal static class WorldMapViewportHelper
{
    private const float CellWorldSize = 4096f;
    private const float MinZoom = 0.001f;
    private const float MaxZoom = 50f;

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

        var cellMinX = cell.GridX.Value * CellWorldSize;
        var cellMaxX = cellMinX + CellWorldSize;
        var cellMinY = -(cell.GridY.Value + 1) * CellWorldSize;
        var cellMaxY = cellMinY + CellWorldSize;

        var viewMinX = Math.Min(tlWorld.X, brWorld.X);
        var viewMaxX = Math.Max(tlWorld.X, brWorld.X);
        var viewMinY = Math.Min(tlWorld.Y, brWorld.Y);
        var viewMaxY = Math.Max(tlWorld.Y, brWorld.Y);

        return cellMaxX >= viewMinX && cellMinX <= viewMaxX &&
               cellMaxY >= viewMinY && cellMinY <= viewMaxY;
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
            return Math.Max(maxExtent, 500f);
        }

        return 500f;
    }

    internal static void ZoomToFitWorldspace(
        List<CellRecord> cells, float canvasWidth, float canvasHeight,
        out float zoom, out Vector2 panOffset)
    {
        zoom = 0.05f;
        panOffset = Vector2.Zero;

        if (cells.Count == 0) return;

        var cellsWithGrid = cells
            .Where(c => c.GridX.HasValue && c.GridY.HasValue)
            .ToList();

        if (cellsWithGrid.Count == 0) return;

        var minX = cellsWithGrid.Min(c => c.GridX!.Value) * CellWorldSize;
        var maxX = (cellsWithGrid.Max(c => c.GridX!.Value) + 1) * CellWorldSize;
        var minY = -(cellsWithGrid.Max(c => c.GridY!.Value) + 1) * CellWorldSize;
        var maxY = -cellsWithGrid.Min(c => c.GridY!.Value) * CellWorldSize;

        ZoomToFitBounds(minX, minY, maxX, maxY, canvasWidth, canvasHeight, out zoom, out panOffset);
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
        var worldMinX = cx * CellWorldSize - 200;
        var worldMaxX = (cx + 1) * CellWorldSize + 200;
        var worldMinY = -(cy + 1) * CellWorldSize - 200;
        var worldMaxY = -cy * CellWorldSize + 200;

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
