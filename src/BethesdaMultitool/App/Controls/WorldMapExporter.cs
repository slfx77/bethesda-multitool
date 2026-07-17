using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Export.Map;
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
using BethesdaMultitool.Core.Games;

namespace BethesdaMultitool;

/// <summary>
///     Exports the world map as a PNG file with heightmap, grid, and markers.
/// </summary>
internal static class WorldMapExporter
{
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
        IReadOnlyDictionary<int, CanvasBitmap>? markerIconBitmaps,
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

        // Real world-units per cell (8192 Morrowind, 4096 Fallout). Drives the grid→world transform so
        // the exported PNG places terrain/markers/overlay at the same scale as the live map.
        var cellWorldSize = data?.CellWorldSize ?? WorldGridConstants.CellSize;
        var pixelsPerWorldUnit = (float)pixelsPerCell / cellWorldSize;
        var worldOriginX = minGridX * cellWorldSize;
        var worldOriginY = -(maxGridY + 1) * cellWorldSize;
        var worldMaxX = (maxGridX + 1) * cellWorldSize;
        var worldMinY = minGridY * cellWorldSize;
        var worldMaxY = (maxGridY + 1) * cellWorldSize;

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
                var outset = Math.Min(1f / pixelsPerWorldUnit, cellWorldSize * 0.01f);
                foreach (var ((gx, gy), (_, bmp)) in bestPerCell)
                {
                    var originX = gx * cellWorldSize;
                    var originY = -(gy + 1) * cellWorldSize;
                    ds.DrawImage(bmp, new Rect(originX - outset, originY - outset,
                        cellWorldSize + 2 * outset, cellWorldSize + 2 * outset));
                }
            }
            else if (worldHeightmapBitmap != null)
            {
                var pixelScale = cellWorldSize / HmGridSize;
                var bitmapWorldW = worldHmPixelWidth * pixelScale;
                var bitmapWorldH = worldHmPixelHeight * pixelScale;
                var bitmapX = worldHmMinX * cellWorldSize;
                var bitmapY = -(worldHmMaxY + 1) * cellWorldSize;
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
                WorldMapDrawingHelper.DrawExportCellGrid(ds, minGridX, maxGridX, minGridY, maxGridY, pixelsPerWorldUnit, cellWorldSize);
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
                filteredMarkers, hiddenCategories, markerIconBitmaps, colorScheme,
                data?.Game ?? BethesdaGame.Unknown);
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
        IReadOnlyDictionary<int, CanvasBitmap>? markerIconBitmaps,
        HeightmapColorScheme colorScheme,
        BethesdaGame game)
    {
        if (filteredMarkers.Count == 0 ||
            hiddenCategories.Contains(PlacedObjectCategory.MapMarker))
        {
            return;
        }

        var inputs = filteredMarkers
            .Select(m => new MapMarkerInput(m.X, m.Y, m.MarkerType, m.MarkerName))
            .ToList();

        // An export's pixels-per-world-unit is the direct equivalent of the live viewport zoom. Apply
        // the same profile policy to both the layout's reserved marker bounds and the eventual draw.
        var profile = GameProfiles.For(game);
        var metrics = MapMarkerMetrics.Resolve(
            profile, pixelsPerWorldUnit, sizing.MarkerRadius * 2f);
        var markerSizing = sizing with { MarkerRadius = metrics.VisualDiameterPixels * 0.5f };

        var layout = MapExportLayoutEngine.ComputeLayout(
            inputs, imageW, imageH,
            worldMinX, worldMaxX, worldMinY, worldMaxY,
            pixelsPerWorldUnit, markerSizing,
            (text, fontSize) =>
            {
                using var tl = new CanvasTextLayout(device, text,
                    new CanvasTextFormat { FontSize = fontSize, FontFamily = "Segoe UI" },
                    float.MaxValue, float.MaxValue);
                return ((float)tl.LayoutBounds.Width, (float)tl.LayoutBounds.Height);
            });

        var markerWorldRadius = markerSizing.MarkerRadius / pixelsPerWorldUnit;
        var iconWorldHeight = metrics.IconHeightPixels / pixelsPerWorldUnit;
        var glyphFontSize = sizing.LabelFontSize * metrics.ScreenScale;
        var tint = Color.FromArgb(255, colorScheme.R, colorScheme.G, colorScheme.B);

        foreach (var m in layout.Markers)
        {
            var marker = filteredMarkers[m.OriginalIndex];
            DrawExportMarkerIcon(ds, marker, markerWorldRadius, iconWorldHeight, tint,
                glyphFontSize, pixelsPerWorldUnit, markerIconBitmaps, profile, game);
        }

        // Switch to pixel space for leader lines + labels
        ds.Transform = Matrix3x2.Identity;

        var leaderColor = Color.FromArgb(150, 255, 255, 255);
        var leaderWidth = Math.Max(1f, markerSizing.MarkerRadius * 0.1f);

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
            var lineStart = markerPixel + direction * (markerSizing.MarkerRadius + 1f);

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
        float worldRadius, float iconWorldHeight, Color tint,
        float glyphFontSize, float pixelsPerWorldUnit,
        IReadOnlyDictionary<int, CanvasBitmap>? markerIconBitmaps,
        GameProfile profile,
        BethesdaGame game)
    {
        var pos = new Vector2(marker.X, -marker.Y);
        var destRect = new Rect(
            pos.X - worldRadius, pos.Y - worldRadius,
            worldRadius * 2, worldRadius * 2);

        // The raw TNAM value is game-specific; resolve it through the per-game catalog (mirrors the live
        // map). Mirrors the live map's height-normalized, aspect-preserved, per-game-scaled blit.
        var raw = marker.MarkerType.HasValue ? (int)marker.MarkerType.Value : 0;

        if (markerIconBitmaps?.TryGetValue(raw, out var icon) == true)
        {
            float sw = icon.SizeInPixels.Width;
            float sh = icon.SizeInPixels.Height;
            var drawH = iconWorldHeight;
            var drawW = sh > 0f ? drawH * sw / sh : drawH;
            var iconDest = new Rect(pos.X - drawW / 2, pos.Y - drawH / 2, drawW, drawH);
            // FO3/FNV silhouettes tint to the scheme; pre-styled sets (Skyrim) draw as-is.
            if (profile.MarkersAreTinted)
            {
                WorldMapDrawingHelper.DrawTintedIcon(ds, icon, iconDest, tint);
            }
            else
            {
                ds.DrawImage(icon, iconDest, new Rect(0, 0, sw, sh));
            }
        }
        else
        {
            var fb = MapMarkerCatalog.Resolve(game, raw).Fallback;
            var color = Color.FromArgb(255, fb.R, fb.G, fb.B);
            ds.FillCircle(pos, worldRadius, WorldMapColors.WithAlpha(color, 200));
            ds.DrawCircle(pos, worldRadius, Colors.White, 1f / pixelsPerWorldUnit);
            if (!string.IsNullOrEmpty(fb.Glyph))
            {
                using var glyphFormat = new CanvasTextFormat
                {
                    FontSize = glyphFontSize / pixelsPerWorldUnit,
                    FontFamily = "Segoe MDL2 Assets",
                    HorizontalAlignment = CanvasHorizontalAlignment.Center,
                    VerticalAlignment = CanvasVerticalAlignment.Center
                };
                ds.DrawText(fb.Glyph, destRect, Colors.White, glyphFormat);
            }
        }
    }
}

