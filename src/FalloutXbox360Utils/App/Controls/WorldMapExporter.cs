using System.Numerics;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;

namespace FalloutXbox360Utils;

/// <summary>
///     Exports the world map as a PNG file with heightmap, grid, and markers.
/// </summary>
internal static class WorldMapExporter
{
    private const float CellWorldSize = 4096f;
    private const int HmGridSize = 33;

    /// <summary>
    ///     Computes the export grid layout from active cells.
    ///     Returns null if there are no cells with grid coordinates.
    /// </summary>
    internal static (int ImageW, int ImageH, int PixelsPerCell,
        int MinGridX, int MaxGridX, int MinGridY, int MaxGridY)? ComputeExportLayout(
        List<CellRecord> cells, int exportLongEdge)
    {
        var cellsWithGrid = cells.Where(c => c.GridX.HasValue && c.GridY.HasValue).ToList();
        if (cellsWithGrid.Count == 0) return null;

        var minGridX = cellsWithGrid.Min(c => c.GridX!.Value);
        var maxGridX = cellsWithGrid.Max(c => c.GridX!.Value);
        var minGridY = cellsWithGrid.Min(c => c.GridY!.Value);
        var maxGridY = cellsWithGrid.Max(c => c.GridY!.Value);

        var gridW = maxGridX - minGridX + 1;
        var gridH = maxGridY - minGridY + 1;
        var maxGridDim = Math.Max(gridW, gridH);
        var pixelsPerCell = Math.Max(exportLongEdge / maxGridDim, 1);

        return (gridW * pixelsPerCell, gridH * pixelsPerCell, pixelsPerCell,
            minGridX, maxGridX, minGridY, maxGridY);
    }

    internal static async Task ExportWorldspacePngAsync(
        string filePath, int imageW, int imageH, int pixelsPerCell,
        int minGridX, int maxGridX, int minGridY, int maxGridY,
        CanvasControl mapCanvas,
        CanvasBitmap? worldHeightmapBitmap,
        int worldHmPixelWidth, int worldHmPixelHeight,
        int worldHmMinX, int worldHmMaxY,
        List<PlacedReference> filteredMarkers,
        HashSet<PlacedObjectCategory> hiddenCategories,
        Dictionary<MapMarkerType, CanvasBitmap>? markerIconBitmaps,
        HeightmapColorScheme colorScheme)
    {
        using var renderTarget = new CanvasRenderTarget(mapCanvas, imageW, imageH, 96);
        var device = renderTarget.Device;

        var longEdge = Math.Max(imageW, imageH);
        var sizing = MapExportLayoutEngine.ComputeSizing(longEdge);

        var pixelsPerWorldUnit = (float)pixelsPerCell / CellWorldSize;
        var worldOriginX = minGridX * CellWorldSize;
        var worldOriginY = -(maxGridY + 1) * CellWorldSize;
        var worldMaxX = (maxGridX + 1) * CellWorldSize;
        var worldMinY = minGridY * CellWorldSize;
        var worldMaxY = (maxGridY + 1) * CellWorldSize;

        using (var ds = renderTarget.CreateDrawingSession())
        {
            ds.Clear(Color.FromArgb(255, 20, 20, 25));

            // World-space transform
            ds.Transform = Matrix3x2.CreateTranslation(-worldOriginX, -worldOriginY)
                           * Matrix3x2.CreateScale(pixelsPerWorldUnit);

            // 1. Heightmap
            if (worldHeightmapBitmap != null)
            {
                var pixelScale = CellWorldSize / HmGridSize;
                var bitmapWorldW = worldHmPixelWidth * pixelScale;
                var bitmapWorldH = worldHmPixelHeight * pixelScale;
                var bitmapX = worldHmMinX * CellWorldSize;
                var bitmapY = -(worldHmMaxY + 1) * CellWorldSize;
                ds.DrawImage(worldHeightmapBitmap,
                    new Rect(bitmapX, bitmapY, bitmapWorldW, bitmapWorldH));
            }

            // 2. Cell grid
            WorldMapDrawingHelper.DrawExportCellGrid(ds, minGridX, maxGridX, minGridY, maxGridY, pixelsPerWorldUnit);

            // 3. Map markers
            DrawExportMapMarkers(ds, device, pixelsPerWorldUnit, imageW, imageH,
                worldOriginX, worldMaxX, worldMinY, worldMaxY, sizing,
                filteredMarkers, hiddenCategories, markerIconBitmaps, colorScheme);
        }

        await using var stream = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        await renderTarget.SaveAsync(stream.AsRandomAccessStream(), CanvasBitmapFileFormat.Png);
    }

    private static void DrawExportMapMarkers(
        CanvasDrawingSession ds, CanvasDevice device,
        float pixelsPerWorldUnit, int imageW, int imageH,
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY,
        MapExportSizing sizing,
        List<PlacedReference> filteredMarkers,
        HashSet<PlacedObjectCategory> hiddenCategories,
        Dictionary<MapMarkerType, CanvasBitmap>? markerIconBitmaps,
        HeightmapColorScheme colorScheme)
    {
        if (filteredMarkers.Count == 0 ||
            hiddenCategories.Contains(PlacedObjectCategory.MapMarker))
        {
            return;
        }

        var inputs = filteredMarkers
            .Select(m => new MapMarkerInput(m.X, m.Y, m.MarkerType, m.MarkerName))
            .ToList();

        var layout = MapExportLayoutEngine.ComputeLayout(
            inputs, imageW, imageH,
            worldMinX, worldMaxX, worldMinY, worldMaxY,
            pixelsPerWorldUnit, sizing,
            (text, fontSize) =>
            {
                using var tl = new CanvasTextLayout(device, text,
                    new CanvasTextFormat { FontSize = fontSize, FontFamily = "Segoe UI" },
                    float.MaxValue, float.MaxValue);
                return ((float)tl.LayoutBounds.Width, (float)tl.LayoutBounds.Height);
            });

        var markerWorldRadius = sizing.MarkerRadius / pixelsPerWorldUnit;
        var tint = Color.FromArgb(255, colorScheme.R, colorScheme.G, colorScheme.B);

        foreach (var m in layout.Markers)
        {
            var marker = filteredMarkers[m.OriginalIndex];
            DrawExportMarkerIcon(ds, marker, markerWorldRadius, tint,
                sizing.LabelFontSize, pixelsPerWorldUnit, markerIconBitmaps);
        }

        // Switch to pixel space for leader lines + labels
        ds.Transform = Matrix3x2.Identity;

        var leaderColor = Color.FromArgb(150, 255, 255, 255);
        var leaderWidth = Math.Max(1f, sizing.MarkerRadius * 0.1f);

        foreach (var lp in layout.Labels)
        {
            if (!lp.NeedsLeader)
            {
                continue;
            }

            var labelCenter = new Vector2(
                lp.LabelX + lp.PillWidth / 2,
                lp.LabelY + lp.PillHeight / 2);
            var markerPixel = new Vector2(lp.MarkerPixelX, lp.MarkerPixelY);
            var direction = Vector2.Normalize(labelCenter - markerPixel);
            var lineStart = markerPixel + direction * (sizing.MarkerRadius + 1f);

            ds.DrawLine(lineStart, labelCenter, leaderColor, leaderWidth);
        }

        using var labelFormat = new CanvasTextFormat
        {
            FontSize = sizing.LabelFontSize,
            FontFamily = "Segoe UI"
        };

        foreach (var lp in layout.Labels)
        {
            using var pillGeometry = CanvasGeometry.CreateRoundedRectangle(
                device, lp.LabelX, lp.LabelY, lp.PillWidth, lp.PillHeight, 3f, 3f);
            ds.FillGeometry(pillGeometry, Color.FromArgb(220, 0, 0, 0));
            ds.DrawGeometry(pillGeometry, Color.FromArgb(100, 255, 255, 255), 0.5f);

            ds.DrawText(lp.Text, lp.LabelX + lp.PadH, lp.LabelY + lp.PadV,
                Colors.White, labelFormat);
        }
    }

    private static void DrawExportMarkerIcon(
        CanvasDrawingSession ds, PlacedReference marker,
        float worldRadius, Color tint, float labelFontSize, float pixelsPerWorldUnit,
        Dictionary<MapMarkerType, CanvasBitmap>? markerIconBitmaps)
    {
        var pos = new Vector2(marker.X, -marker.Y);
        var destRect = new Rect(
            pos.X - worldRadius, pos.Y - worldRadius,
            worldRadius * 2, worldRadius * 2);

        if (marker.MarkerType.HasValue &&
            markerIconBitmaps?.TryGetValue(marker.MarkerType.Value, out var icon) == true)
        {
            WorldMapDrawingHelper.DrawTintedIcon(ds, icon, destRect, tint);
        }
        else
        {
            var color = WorldMapColors.GetMarkerColor(marker.MarkerType);
            ds.FillCircle(pos, worldRadius, WorldMapColors.WithAlpha(color, 200));
            ds.DrawCircle(pos, worldRadius, Colors.White, 1f / pixelsPerWorldUnit);
            var glyph = WorldMapColors.GetMarkerGlyph(marker.MarkerType);
            using var glyphFormat = new CanvasTextFormat
            {
                FontSize = labelFontSize / pixelsPerWorldUnit,
                FontFamily = "Segoe MDL2 Assets",
                HorizontalAlignment = CanvasHorizontalAlignment.Center,
                VerticalAlignment = CanvasVerticalAlignment.Center
            };
            ds.DrawText(glyph, destRect, Colors.White, glyphFormat);
        }
    }
}
