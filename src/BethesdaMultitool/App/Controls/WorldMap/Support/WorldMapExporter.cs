using System.Diagnostics;
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
using BethesdaMultitool.Core.Utils;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

internal enum WorldMapPngWriterPhase
{
    RenderTargetCreate,
    ComposeDrawSubmission,
    Win2DSaveAsync,
    CompanionStagingMove,
    AtomicPublishMove
}

/// <summary>
///     One opt-in wall-clock phase from the Win2D PNG writer. These are API-boundary timings, not GPU
///     timestamps: bitmap/draw work may be deferred into <c>SaveAsync</c>, whose sample also includes
///     temporary-stream disposal and flush.
/// </summary>
internal readonly record struct WorldMapPngWriterPhaseTiming(
    WorldMapPngWriterPhase Phase,
    double WallMilliseconds,
    bool Succeeded,
    long? ByteCount = null);

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
        string filePath, int imageW, int imageH, int pixelsPerCell, float cellWorldSize,
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
        float overlayWorldMinY = 0f, float overlayWorldMaxY = 0f,
        string? companionPathToInvalidate = null,
        Action<WorldMapPngWriterPhaseTiming>? phaseObserver = null,
        CancellationToken cancellationToken = default)
    {
        using var renderTarget = CreateRenderTarget(
            mapCanvas,
            imageW,
            imageH,
            phaseObserver);
        var device = renderTarget.Device;

        var longEdge = Math.Max(imageW, imageH);
        var sizing = MapExportLayoutEngine.ComputeSizing(longEdge);

        // Real world-units per cell (8192 Morrowind, 4096 Fallout). Drives the grid→world transform so
        // the exported PNG places terrain/markers/overlay at the same scale as the live map.
        var pixelsPerWorldUnit = (float)pixelsPerCell / cellWorldSize;
        var gridWorldBounds = WorldMapExportPlan.GetWorldBoundsChecked(
            minGridX, maxGridX, minGridY, maxGridY, cellWorldSize);
        var worldOriginX = gridWorldBounds.MinX;
        var worldOriginY = -gridWorldBounds.MaxY;
        var worldMaxX = gridWorldBounds.MaxX;
        var worldMinY = gridWorldBounds.MinY;
        var worldMaxY = gridWorldBounds.MaxY;

        var composeStarted = StartTiming(phaseObserver);
        try
        {
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
                    var cellsWide = checked((int)((long)maxGridX - minGridX + 1));
                    var cellsTall = checked((int)((long)maxGridY - minGridY + 1));
                    WorldMapDrawingHelper.DrawExportCellGrid(
                        ds, cellsWide, cellsTall, gridWorldBounds, pixelsPerWorldUnit, cellWorldSize);
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

            ObservePhase(
                phaseObserver,
                WorldMapPngWriterPhase.ComposeDrawSubmission,
                composeStarted,
                succeeded: true);
        }
        catch
        {
            ObservePhase(
                phaseObserver,
                WorldMapPngWriterPhase.ComposeDrawSubmission,
                composeStarted,
                succeeded: false);
            throw;
        }

        Action<AtomicFileWritePhaseTiming>? atomicPhaseObserver = phaseObserver is null
            ? null
            : timing => ObservePhase(
                phaseObserver,
                timing.Phase == AtomicFileWritePhase.CompanionStagingMove
                    ? WorldMapPngWriterPhase.CompanionStagingMove
                    : WorldMapPngWriterPhase.AtomicPublishMove,
                timing.WallMilliseconds,
                timing.Succeeded);

        await AtomicFileWriter.WriteAsync(
            filePath,
            async (temporaryPath, _) =>
            {
                var saveStarted = 0L;
                var saveAttempted = false;
                try
                {
                    await using (var stream = new FileStream(
                                     temporaryPath,
                                     FileMode.CreateNew,
                                     FileAccess.Write,
                                     FileShare.None))
                    {
                        using (var randomAccessStream = stream.AsRandomAccessStream())
                        {
                            saveStarted = StartTiming(phaseObserver);
                            saveAttempted = true;
                            await renderTarget.SaveAsync(
                                randomAccessStream,
                                CanvasBitmapFileFormat.Png);
                        }
                    }

                    if (phaseObserver is not null)
                    {
                        // Stop immediately after both stream scopes finalize; the best-effort byte
                        // probe below is diagnostic overhead and deliberately excluded from Save wall.
                        var saveWallMilliseconds = Stopwatch.GetElapsedTime(saveStarted).TotalMilliseconds;
                        var encodedBytes = TryGetFileLength(temporaryPath);
                        ObservePhase(
                            phaseObserver,
                            WorldMapPngWriterPhase.Win2DSaveAsync,
                            saveWallMilliseconds,
                            succeeded: true,
                            byteCount: encodedBytes);
                    }
                }
                catch
                {
                    if (saveAttempted)
                    {
                        ObservePhase(
                            phaseObserver,
                            WorldMapPngWriterPhase.Win2DSaveAsync,
                            saveStarted,
                            succeeded: false);
                    }

                    throw;
                }
            },
            companionPathToInvalidate: companionPathToInvalidate,
            cancellationToken: cancellationToken,
            phaseObserver: atomicPhaseObserver);
    }

    private static long? TryGetFileLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            // Byte counts are trace metadata; a failed length probe cannot invalidate a saved PNG.
            return null;
        }
    }

    private static long StartTiming(Action<WorldMapPngWriterPhaseTiming>? phaseObserver) =>
        phaseObserver is null ? 0 : Stopwatch.GetTimestamp();

    private static CanvasRenderTarget CreateRenderTarget(
        CanvasControl mapCanvas,
        int imageW,
        int imageH,
        Action<WorldMapPngWriterPhaseTiming>? phaseObserver)
    {
        var started = StartTiming(phaseObserver);
        try
        {
            var renderTarget = new CanvasRenderTarget(mapCanvas, imageW, imageH, 96);
            ObservePhase(
                phaseObserver,
                WorldMapPngWriterPhase.RenderTargetCreate,
                started,
                succeeded: true);
            return renderTarget;
        }
        catch
        {
            ObservePhase(
                phaseObserver,
                WorldMapPngWriterPhase.RenderTargetCreate,
                started,
                succeeded: false);
            throw;
        }
    }

    private static void ObservePhase(
        Action<WorldMapPngWriterPhaseTiming>? phaseObserver,
        WorldMapPngWriterPhase phase,
        long started,
        bool succeeded,
        long? byteCount = null)
    {
        if (phaseObserver is null)
        {
            return;
        }

        ObservePhase(
            phaseObserver,
            phase,
            Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            succeeded,
            byteCount);
    }

    private static void ObservePhase(
        Action<WorldMapPngWriterPhaseTiming>? phaseObserver,
        WorldMapPngWriterPhase phase,
        double wallMilliseconds,
        bool succeeded,
        long? byteCount = null)
    {
        if (phaseObserver is null)
        {
            return;
        }

        try
        {
            phaseObserver(new WorldMapPngWriterPhaseTiming(
                phase,
                wallMilliseconds,
                succeeded,
                byteCount));
        }
        catch
        {
            // Export tracing is diagnostic-only and must never turn a successful save into a failure.
        }
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
