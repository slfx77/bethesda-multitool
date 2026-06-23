using System.Numerics;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>
///     Exports the world map as a PNG file with heightmap, grid, and markers.
/// </summary>
internal static class WorldMapExporter
{
    private const float CellWorldSize = 4096f;
    private const int HmGridSize = 33;

    /// <summary>
    ///     Max single-image dimension (per axis) for one PNG. D3D11 feature-level 11 guarantees a
    ///     16384 px max texture dimension; a single <see cref="CanvasRenderTarget" /> can't exceed it.
    ///     The default (non-tiled) export clamps to this; "Tiled high-res" splits the output into a
    ///     grid of tiles each within this bound. (WorldLayerBuildService uses a more conservative
    ///     8192 for its single-bitmap path.)
    /// </summary>
    internal const int ExportMaxTileDimension = 16384;

    internal static async Task ExportWorldspacePngAsync(
        string filePath, int imageW, int imageH, int pixelsPerCell,
        int minGridX, int maxGridX, int minGridY, int maxGridY,
        CanvasControl mapCanvas,
        CanvasBitmap? worldHeightmapBitmap,
        int worldHmPixelWidth, int worldHmPixelHeight,
        int worldHmMinX, int worldHmMaxY,
        IReadOnlyDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? textureCellBitmaps,
        List<PlacedReference> filteredMarkers,
        HashSet<PlacedObjectCategory> hiddenCategories,
        Dictionary<MapMarkerType, CanvasBitmap>? markerIconBitmaps,
        HeightmapColorScheme colorScheme,
        WorldViewData? data = null,
        List<CellRecord>? activeCells = null,
        bool drawNavMesh = false,
        bool drawGrid = true,
        CanvasBitmap? renderedMeshesOverlay = null,
        float overlayWorldMinX = 0f, float overlayWorldMaxX = 0f,
        float overlayWorldMinY = 0f, float overlayWorldMaxY = 0f)
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

            // 1. Layer background — single bitmap for most layers, per-cell for
            //    TerrainTextures (to dodge the GPU max-texture-size limit on large worldspaces).
            //    Only one path is active per export.
            if (textureCellBitmaps is not null)
            {
                // Same best-available-resolution picker as WorldMapOverviewRenderer.DrawTextureCellBitmaps.
                // Export caches are typically single-resolution but this keeps the contract consistent.
                var bestPerCell = new Dictionary<(int gx, int gy), (int ppc, CanvasBitmap bmp)>();
                foreach (var (key, bmp) in textureCellBitmaps)
                {
                    var cellKey = (key.gx, key.gy);
                    if (!bestPerCell.TryGetValue(cellKey, out var current) || key.pixelsPerCell > current.ppc)
                    {
                        bestPerCell[cellKey] = (key.pixelsPerCell, bmp);
                    }
                }
                // Outset by ~1 export pixel in world units so adjacent opaque tiles overlap and no
                // seam shows when the grid is off — mirrors WorldMapOverviewRenderer.DrawTextureCellBitmaps.
                var outset = Math.Min(1f / pixelsPerWorldUnit, CellWorldSize * 0.01f);
                foreach (var ((gx, gy), (_, bmp)) in bestPerCell)
                {
                    var originX = gx * CellWorldSize;
                    var originY = -(gy + 1) * CellWorldSize;
                    ds.DrawImage(bmp, new Rect(originX - outset, originY - outset,
                        CellWorldSize + 2 * outset, CellWorldSize + 2 * outset));
                }
            }
            else if (worldHeightmapBitmap != null)
            {
                var pixelScale = CellWorldSize / HmGridSize;
                var bitmapWorldW = worldHmPixelWidth * pixelScale;
                var bitmapWorldH = worldHmPixelHeight * pixelScale;
                var bitmapX = worldHmMinX * CellWorldSize;
                var bitmapY = -(worldHmMaxY + 1) * CellWorldSize;
                ds.DrawImage(worldHeightmapBitmap,
                    new Rect(bitmapX, bitmapY, bitmapWorldW, bitmapWorldH));
            }

            // 1b. Rendered-meshes overlay (top-down 3D render) over the terrain, below grid + markers.
            //     Transparent where there are no meshes (terrain shows through) and bakes height-correct
            //     water, so the terrain background was rendered water-free. Image Y = -worldNorthY, so the
            //     north edge (overlayWorldMaxY) maps to the top of the destination rect.
            if (renderedMeshesOverlay is not null &&
                overlayWorldMaxX > overlayWorldMinX && overlayWorldMaxY > overlayWorldMinY)
            {
                ds.DrawImage(renderedMeshesOverlay, new Rect(
                    overlayWorldMinX, -overlayWorldMaxY,
                    overlayWorldMaxX - overlayWorldMinX, overlayWorldMaxY - overlayWorldMinY));
            }

            // 2. Cell grid (optional)
            if (drawGrid)
            {
                WorldMapDrawingHelper.DrawExportCellGrid(ds, minGridX, maxGridX, minGridY, maxGridY, pixelsPerWorldUnit);
            }

            // 3. Nav mesh overlay (below markers so labels stay on top).
            if (drawNavMesh && data != null && activeCells != null)
            {
                WorldMapNavMeshOverlayRenderer.DrawWorldOverview(
                    ds,
                    data,
                    activeCells,
                    spatialIndex: null,
                    new Vector2(worldOriginX, worldOriginY),
                    new Vector2(worldMaxX, -worldMinY),
                    pixelsPerWorldUnit);
            }

            // 4. Map markers
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
