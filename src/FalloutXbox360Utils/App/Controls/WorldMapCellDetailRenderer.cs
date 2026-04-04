using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils;

/// <summary>
///     Renders cell detail mode (single cell view) and builds per-cell heightmap bitmaps.
/// </summary>
internal static class WorldMapCellDetailRenderer
{
    private const float CellWorldSize = 4096f;
    private const int HmGridSize = 33;

    internal static void DrawCellDetail(
        CanvasDrawingSession ds,
        CellRecord selectedCell,
        WorldViewData data,
        CanvasBitmap? cellHeightmapBitmap,
        float zoom, Vector2 panOffset,
        float canvasWidth, float canvasHeight,
        HashSet<PlacedObjectCategory> hiddenCategories,
        bool hideDisabledActors,
        PlacedReference? selectedObject,
        PlacedReference? hoveredObject)
    {
        ds.Transform = WorldMapViewportHelper.GetViewTransform(zoom, panOffset);

        // 1. Cell heightmap background
        if (cellHeightmapBitmap != null && selectedCell.GridX.HasValue && selectedCell.GridY.HasValue)
        {
            var cellX = selectedCell.GridX.Value;
            var cellY = selectedCell.GridY.Value;
            var originX = cellX * CellWorldSize;
            var originY = -(cellY + 1) * CellWorldSize;

            ds.DrawImage(cellHeightmapBitmap,
                new Rect(originX, originY, CellWorldSize, CellWorldSize));
        }

        // 2. Cell boundary
        if (selectedCell.GridX.HasValue && selectedCell.GridY.HasValue)
        {
            var cellX = selectedCell.GridX.Value;
            var cellY = selectedCell.GridY.Value;
            var originX = cellX * CellWorldSize;
            var originY = -(cellY + 1) * CellWorldSize;
            ds.DrawRectangle(new Rect(originX, originY, CellWorldSize, CellWorldSize),
                Color.FromArgb(80, 255, 255, 255), 2f / zoom);
        }

        // 3. Placed objects
        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);
        foreach (var obj in selectedCell.PlacedObjects)
        {
            if (hiddenCategories.Contains(WorldMapOverviewRenderer.GetObjectCategory(obj, data)))
            {
                continue;
            }

            if (hideDisabledActors && obj.IsInitiallyDisabled)
            {
                continue;
            }

            if (!WorldMapViewportHelper.IsPointInView(obj.X, -obj.Y, tlWorld, brWorld,
                    WorldMapViewportHelper.GetObjectViewMargin(obj, data)))
            {
                continue;
            }

            WorldMapOverviewRenderer.DrawPlacedObjectBox(ds, obj, data, zoom);
        }

        // 4. Selected object highlight
        if (selectedObject != null)
        {
            WorldMapOverviewRenderer.DrawSelectedObjectHighlight(ds, selectedObject, data, zoom);
            WorldMapOverviewRenderer.DrawSpawnOverlay(ds, selectedObject, data, zoom);
        }

        // 5. Hovered object highlight
        if (hoveredObject != null)
        {
            WorldMapOverviewRenderer.DrawPlacedObjectHighlight(ds, hoveredObject, data, zoom);
        }
    }

    internal static CanvasBitmap? BuildCellHeightmapBitmap(
        CanvasControl canvas, CellRecord cell,
        float? currentDefaultWaterHeight,
        HeightmapColorScheme colorScheme, bool showWater)
    {
        if (cell.Heightmap == null)
        {
            return null;
        }

        var heights = cell.Heightmap.CalculateHeights();
        var minH = float.MaxValue;
        var maxH = float.MinValue;
        for (var y = 0; y < HmGridSize; y++)
        {
            for (var x = 0; x < HmGridSize; x++)
            {
                var h = heights[y, x];
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
        }

        var range = maxH - minH;
        if (range < 0.001f)
        {
            range = 1f;
        }

        // Determine effective water height
        var waterH = cell.WaterHeight;
        if (!waterH.HasValue || waterH.Value is not (> -1e6f and < 1e6f))
        {
            waterH = currentDefaultWaterHeight;
        }

        var grayscale = new byte[HmGridSize * HmGridSize];
        var waterMask = new byte[HmGridSize * HmGridSize];

        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var height = heights[HmGridSize - 1 - py, px];
                var normalized = (height - minH) / range;
                var idx = py * HmGridSize + px;
                grayscale[idx] = (byte)(Math.Clamp(normalized, 0f, 1f) * 255);

                if (waterH.HasValue && waterH.Value is > -1e6f and < 1e6f &&
                    height < waterH.Value)
                {
                    waterMask[idx] = 180;
                }
            }
        }

        HeightmapRenderer.BlurWaterMask(waterMask, HmGridSize, HmGridSize);

        var pixels = HeightmapRenderer.ApplyTintAndWater(grayscale, waterMask, HmGridSize, HmGridSize,
            colorScheme, showWater, alpha: 200);

        return CanvasBitmap.CreateFromBytes(
            canvas, pixels, HmGridSize, HmGridSize,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
    }
}
