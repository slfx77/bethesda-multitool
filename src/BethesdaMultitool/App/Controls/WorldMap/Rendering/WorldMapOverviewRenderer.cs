using System.Numerics;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Export.Map;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Scene;
using BethesdaMultitool.Core.Games;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.WorldData;

namespace BethesdaMultitool;

/// <summary>
///     Renders the world overview mode: heightmap, cell grid, placed objects,
///     map markers, actor dots, save overlay, and selection/hover highlights.
/// </summary>
internal static class WorldMapOverviewRenderer
{
    /// <summary>
    ///     Maximum half-extent in world units for rendering a placed object's bounding box.
    ///     Half a cell (2048) is generous for even the largest buildings; anything beyond
    ///     this is likely corrupted OBND data or extreme scale and would obscure the map.
    /// </summary>
    private const float MaxHalfExtent = 2048f;

    [ThreadStatic] private static List<PlacedReference>? t_refScratch;

    internal static void DrawWorldOverview(
        CanvasDrawingSession ds,
        WorldViewData data,
        List<CellRecord> activeCells,
        List<PlacedReference> filteredMarkers,
        Dictionary<(int x, int y), CellRecord>? cellGridLookup,
        WorldSpatialIndex? spatialIndex,
        CanvasBitmap? worldHeightmapBitmap,
        int worldHmPixelWidth, int worldHmPixelHeight,
        int worldHmMinX, int worldHmMaxY,
        int worldHmPixelsPerCell,
        IReadOnlyDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? textureCellBitmaps,
        float zoom, Vector2 panOffset,
        float canvasWidth, float canvasHeight,
        HashSet<PlacedObjectCategory> hiddenCategories,
        bool hideDisabledActors,
        PlacedReference? selectedObject,
        PlacedReference? hoveredObject,
        MarkerRenderContext markers,
        HeightmapColorScheme colorScheme,
        bool showCellGrid = true,
        bool showRenderedObjects = false,
        CanvasBitmap? renderedObjectsOverlay = null,
        float overlayWorldMinX = 0f, float overlayWorldMaxX = 0f,
        float overlayWorldMinY = 0f, float overlayWorldMaxY = 0f,
        IReadOnlyDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap>? waterCellBitmaps = null,
        bool showWater = true,
        CanvasBitmap? worldWaterBitmap = null,
        int worldWaterMinX = 0, int worldWaterMaxY = 0,
        int worldWaterPixelWidth = 0, int worldWaterPixelHeight = 0,
        int worldWaterPixelsPerCell = 33,
        IReadOnlyDictionary<(int tileGx, int tileGy), CanvasBitmap>? coarseTileBitmaps = null,
        int coarseTileCellSpan = 0,
        int coarseTilePixelsPerCell = 0)
    {
        var transform = WorldMapViewportHelper.GetViewTransform(zoom, panOffset);
        ds.Transform = transform;
        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);

        // Real world-units per cell for this worldspace (8192 Morrowind, 4096 Fallout). Drives every
        // grid→world / bitmap-placement conversion below so the terrain layer lines up with placed
        // objects and the 3D top-down overlay (which both use raw world coords). Defaults to 4096.
        var cellWorldSize = data.CellWorldSize;

        var overlayActive = showRenderedObjects && renderedObjectsOverlay is not null;

        // 1. Layer background — one of: coarse multi-cell tiles (oversized worldspace, e.g. FO76
        //    APPALACHIA), a per-cell composite (TerrainTextures, to dodge the GPU max-texture-size
        //    limit), or a single giant bitmap (most layers at normal sizes). Only one is set at a time
        //    per EnsureHeightmapBitmap.
        if (coarseTileBitmaps is not null && coarseTileCellSpan > 0)
        {
            WorldMapTerrainTileRenderer.DrawCoarseTileBitmaps(
                ds, coarseTileBitmaps, coarseTileCellSpan, coarseTilePixelsPerCell, zoom, cellWorldSize);
        }
        else if (textureCellBitmaps is not null)
        {
            var counts = WorldMapTerrainTileRenderer.DrawTextureCellBitmaps(
                ds, textureCellBitmaps, zoom, cellWorldSize, tlWorld, brWorld);
            if (Map2DProfilerTrace.IsEnabled)
            {
                Map2DProfilerTrace.Event("terrain-tile-draw",
                    $"cached={counts.CandidateEntries} visibleEntries={counts.VisibleEntries} " +
                    $"drawnCells={counts.DrawnCells} culled={counts.CulledEntries}");
            }
        }
        else if (worldHeightmapBitmap != null)
        {
            // Scale by the bitmap's actual px/cell: heightmap-family aggregates are 33 px/cell, but the
            // TerrainTextures aggregate LOD uses an adaptive (smaller) px/cell sized to the worldspace.
            var ppc = worldHmPixelsPerCell > 0 ? worldHmPixelsPerCell : 33f;
            var pixelScale = cellWorldSize / ppc;
            var bitmapWorldW = worldHmPixelWidth * pixelScale;
            var bitmapWorldH = worldHmPixelHeight * pixelScale;
            var bitmapX = worldHmMinX * cellWorldSize;
            var bitmapY = -(worldHmMaxY + 1) * cellWorldSize;

            // A plain CanvasBitmap has no mip chain, so a fixed-tap cubic still moires the aggregate once
            // it is shrunk several-fold (the zoomed-out case). Anisotropic minification resamples it
            // through D2D's internally-generated mip pyramid + anisotropic footprint — the same thing a
            // GPU sampler does — which is what removes the shimmer. One DrawImage/frame, so quality-first.
            // Source rect = the full bitmap.
            var aggInterp = WorldMapTerrainTileRenderer.ChooseInterpolation(
                ppc, cellWorldSize * Math.Max(zoom, 1e-6f), preferQuality: true);
            ds.DrawImage(worldHeightmapBitmap,
                new Rect(bitmapX, bitmapY, bitmapWorldW, bitmapWorldH),
                new Rect(0, 0, worldHmPixelWidth, worldHmPixelHeight),
                1f,
                aggInterp);
        }

        // 2. Cell grid (optional overlay)
        if (showCellGrid)
        {
            DrawCellGrid(ds, activeCells, cellGridLookup,
                worldHeightmapBitmap is not null || textureCellBitmaps is not null
                                                 || (coarseTileBitmaps is not null && coarseTileCellSpan > 0),
                zoom, panOffset, canvasWidth, canvasHeight, cellWorldSize);
        }

        // 2b. Rendered-models overlay — a top-down 3D render of the placed objects (terrain-occluded
        //     via the real depth buffer). Drawn over the terrain layer + grid; replaces the static
        //     dots/boxes below. Actor dots (4b) still draw on top.
        if (overlayActive)
        {
            DrawRenderedObjectsOverlay(ds, renderedObjectsOverlay!,
                overlayWorldMinX, overlayWorldMaxX, overlayWorldMinY, overlayWorldMaxY);
        }

        // 2c. Water layer — a standalone translucent layer over the terrain. SKIPPED when the
        //     rendered-models overlay is active: that overlay already renders water THROUGH the 3D depth
        //     buffer (height-correct — docks above water show, submerged geometry is covered), which a
        //     flat 2D layer can't reproduce. Without the overlay there are no model heights to respect,
        //     so flat-water-over-terrain is correct. Exactly one source: per-cell tiles for the zoomed-in
        //     TerrainTextures path, else the shared world water bitmap for the aggregate + secondary layers.
        if (showWater && !overlayActive)
        {
            if (waterCellBitmaps is not null)
            {
                var counts = WorldMapTerrainTileRenderer.DrawWaterCellBitmaps(
                    ds, waterCellBitmaps, zoom, cellWorldSize, tlWorld, brWorld);
                if (Map2DProfilerTrace.IsEnabled)
                {
                    Map2DProfilerTrace.Event("water-tile-draw",
                        $"cached={counts.CandidateEntries} visibleEntries={counts.VisibleEntries} " +
                        $"drawnCells={counts.DrawnCells} culled={counts.CulledEntries}");
                }
            }
            else if (worldWaterBitmap is not null)
            {
                // Same world-rect math as the aggregate-terrain branch (33 px/cell, scaled to world space).
                var ppc = worldWaterPixelsPerCell > 0 ? worldWaterPixelsPerCell : 33f;
                var pixelScale = cellWorldSize / ppc;
                var src = worldWaterBitmap.SizeInPixels;
                var waterInterp = WorldMapTerrainTileRenderer.ChooseInterpolation(
                    ppc, cellWorldSize * Math.Max(zoom, 1e-6f), preferQuality: true);
                ds.DrawImage(worldWaterBitmap,
                    new Rect(worldWaterMinX * cellWorldSize, -(worldWaterMaxY + 1) * cellWorldSize,
                        worldWaterPixelWidth * pixelScale, worldWaterPixelHeight * pixelScale),
                    new Rect(0, 0, src.Width, src.Height),
                    1f,
                    waterInterp);
            }
        }

        // 3. Placed objects (LOD-based) — skipped when the rendered-models overlay replaces them.
        if (!overlayActive && zoom > 0.05f && activeCells.Count > 0)
        {
            if (spatialIndex is not null)
            {
                var refs = GetRefScratch();
                spatialIndex.QueryRefsInViewport(tlWorld, brWorld, refs, MaxHalfExtent);
                foreach (var obj in refs)
                {
                    DrawPlacedObjectInOverview(ds, obj, data, hiddenCategories,
                        hideDisabledActors, tlWorld, brWorld, zoom);
                }
            }
            else
            {
                foreach (var cell in activeCells)
                {
                    if (!cell.HasPersistentObjects &&
                        !WorldMapViewportHelper.IsCellVisible(cell, tlWorld, brWorld))
                    {
                        continue;
                    }

                    foreach (var obj in cell.PlacedObjects)
                    {
                        DrawPlacedObjectInOverview(ds, obj, data, hiddenCategories,
                            hideDisabledActors, tlWorld, brWorld, zoom);
                    }
                }
            }
        }

        // 4. Map markers (always visible)
        DrawMapMarkers(ds, filteredMarkers, markers, hiddenCategories,
            zoom, panOffset, canvasWidth, canvasHeight, colorScheme);

        // 4b. NPC/Creature dots (always visible)
        DrawActorDots(ds, data, activeCells, spatialIndex, hiddenCategories, hideDisabledActors,
            zoom, panOffset, canvasWidth, canvasHeight);

        // 4c. Save overlay markers (save file positions)
        DrawSaveOverlay(ds, data, spatialIndex, zoom, panOffset, canvasWidth, canvasHeight);

        // 5. Selected object highlight
        if (selectedObject != null)
        {
            DrawSelectedObjectHighlight(ds, selectedObject, data, markers, zoom);
            DrawSpawnOverlay(ds, selectedObject, data, zoom);
        }

        // 6. Hovered object highlight (overview)
        if (hoveredObject != null)
        {
            DrawPlacedObjectHighlight(ds, hoveredObject, data, markers, zoom);
        }
    }

    /// <summary>
    ///     Composites a top-down rendered-models overlay bitmap over the terrain layer. The overlay
    ///     covers the WORLD rectangle [<paramref name="worldMinX" />,<paramref name="worldMaxX" />] ×
    ///     [<paramref name="worldMinY" />,<paramref name="worldMaxY" />] (world north-Y). Canvas Y is
    ///     <c>-worldNorthY</c>, so the north edge (worldMaxY) maps to the top (min canvas Y), matching
    ///     the heightmap-bitmap placement. Shared by the overview and (exterior) cell-detail paths.
    /// </summary>
    internal static void DrawRenderedObjectsOverlay(
        CanvasDrawingSession ds, CanvasBitmap overlay,
        float worldMinX, float worldMaxX, float worldMinY, float worldMaxY)
    {
        if (worldMaxX <= worldMinX || worldMaxY <= worldMinY) return;
        var rect = new Rect(worldMinX, -worldMaxY, worldMaxX - worldMinX, worldMaxY - worldMinY);
        ds.DrawImage(overlay, rect);
    }

    internal static void DrawCellGrid(
        CanvasDrawingSession ds,
        List<CellRecord> activeCells,
        Dictionary<(int x, int y), CellRecord>? cellGridLookup,
        bool hasLayerBackground,
        float zoom, Vector2 panOffset,
        float canvasWidth, float canvasHeight,
        float cellWorldSize)
    {
        if (activeCells.Count == 0)
        {
            return;
        }

        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);

        var startCellX = (int)Math.Floor(Math.Min(tlWorld.X, brWorld.X) / cellWorldSize) - 1;
        var endCellX = (int)Math.Ceiling(Math.Max(tlWorld.X, brWorld.X) / cellWorldSize) + 1;
        var startCellY = (int)Math.Floor(Math.Min(tlWorld.Y, brWorld.Y) / cellWorldSize) - 1;
        var endCellY = (int)Math.Ceiling(Math.Max(tlWorld.Y, brWorld.Y) / cellWorldSize) + 1;

        // Clamp to reasonable range
        startCellX = Math.Max(startCellX, -200);
        endCellX = Math.Min(endCellX, 200);
        startCellY = Math.Max(startCellY, -200);
        endCellY = Math.Min(endCellY, 200);

        // When the worldspace has no heightmap data, fill existing cells with black
        if (!hasLayerBackground && cellGridLookup is { Count: > 0 })
        {
            var cellFill = Color.FromArgb(255, 8, 8, 10);
            foreach (var ((cx, cy), _) in cellGridLookup)
            {
                if (cx < startCellX || cx > endCellX)
                {
                    continue;
                }

                var worldLeft = cx * cellWorldSize;
                var worldTop = -(cy + 1) * cellWorldSize;
                ds.FillRectangle(worldLeft, worldTop, cellWorldSize, cellWorldSize, cellFill);
            }
        }

        var gridColor = Color.FromArgb(40, 255, 255, 255);
        var lineWidth = 1f / zoom;

        // Aggregate all H + V grid lines into a single CanvasGeometry so the Win2D command
        // list carries one DrawGeometry instead of up to ~800 individual DrawLine calls per
        // frame. Each line is an open figure on the path. Geometry + path builder are both
        // IDisposable; `using` is correct.
        var startWorldY = startCellY * cellWorldSize;
        var endWorldY = endCellY * cellWorldSize;
        var startWorldX = startCellX * cellWorldSize;
        var endWorldX = endCellX * cellWorldSize;
        using var pathBuilder = new CanvasPathBuilder(ds);
        for (var cx = startCellX; cx <= endCellX; cx++)
        {
            var worldX = cx * cellWorldSize;
            pathBuilder.BeginFigure(worldX, startWorldY);
            pathBuilder.AddLine(worldX, endWorldY);
            pathBuilder.EndFigure(CanvasFigureLoop.Open);
        }

        for (var cy = startCellY; cy <= endCellY; cy++)
        {
            var worldY = cy * cellWorldSize;
            pathBuilder.BeginFigure(startWorldX, worldY);
            pathBuilder.AddLine(endWorldX, worldY);
            pathBuilder.EndFigure(CanvasFigureLoop.Open);
        }

        using var gridGeometry = CanvasGeometry.CreatePath(pathBuilder);
        ds.DrawGeometry(gridGeometry, gridColor, lineWidth);

        // Cell coordinate labels at sufficient zoom
        if (zoom > 0.05f)
        {
            var labelColor = Color.FromArgb(100, 255, 255, 255);
            using var textFormat = new CanvasTextFormat
            {
                FontSize = 10f / zoom,
                FontFamily = "Consolas"
            };

            foreach (var cell in activeCells)
            {
                if (!cell.GridX.HasValue || !cell.GridY.HasValue)
                {
                    continue;
                }

                var cx = cell.GridX.Value;
                var cy = cell.GridY.Value;
                var labelX = cx * cellWorldSize + 50;
                var labelY = -(cy + 1) * cellWorldSize + 50;

                if (!WorldMapViewportHelper.IsPointInView(labelX, labelY, tlWorld, brWorld, cellWorldSize))
                {
                    continue;
                }

                ds.DrawText($"{cx},{cy}", labelX, labelY, labelColor, textFormat);
            }
        }
    }

    internal static void DrawMapMarkers(
        CanvasDrawingSession ds,
        List<PlacedReference> filteredMarkers,
        MarkerRenderContext markers,
        HashSet<PlacedObjectCategory> hiddenCategories,
        float zoom, Vector2 panOffset,
        float canvasWidth, float canvasHeight,
        HeightmapColorScheme colorScheme)
    {
        if (filteredMarkers.Count == 0 ||
            hiddenCategories.Contains(PlacedObjectCategory.MapMarker))
        {
            return;
        }

        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);
        var profile = GameProfiles.For(markers.Game);
        var metrics = MapMarkerMetrics.Resolve(profile, zoom);
        var markerSize = metrics.VisualDiameterPixels / zoom;

        using var labelFormat = new CanvasTextFormat
        {
            FontSize = 10f / zoom,
            FontFamily = "Segoe UI",
            WordWrapping = CanvasWordWrapping.NoWrap
        };

        using var glyphFormat = new CanvasTextFormat
        {
            FontSize = 12f * metrics.ScreenScale / zoom,
            FontFamily = "Segoe MDL2 Assets",
            HorizontalAlignment = CanvasHorizontalAlignment.Center,
            VerticalAlignment = CanvasVerticalAlignment.Center
        };

        var tint = Color.FromArgb(255, colorScheme.R, colorScheme.G, colorScheme.B);

        // Per-marker label DrawText (text layout + glyph run) is the dominant remaining marker cost at
        // a zoomed-out overview where hundreds are in view; the icons themselves are cheap cached blits.
        // Cap how many labels we lay out per draw — in a dense cluster the labels overlap into
        // unreadability anyway, so omitting the overflow is a free perf win. Icons still all draw.
        const int maxLabelsPerDraw = 250;
        var labelsDrawn = 0;
        var cullMargin = MathF.Max(metrics.HitRadiusPixels, metrics.IconHeightPixels * 2f) / zoom;

        foreach (var marker in filteredMarkers)
        {
            var pos = new Vector2(marker.X, -marker.Y);

            if (!WorldMapViewportHelper.IsPointInView(pos.X, pos.Y, tlWorld, brWorld, cullMargin))
            {
                continue;
            }

            var destRect = new Rect(
                pos.X - markerSize / 2, pos.Y - markerSize / 2,
                markerSize, markerSize);

            // The raw TNAM value means different things per game, so resolve type/name/glyph/color
            // through the per-game catalog rather than the FO3/FNV enum.
            var raw = marker.MarkerType.HasValue ? (int)marker.MarkerType.Value : 0;

            // Right-edge half-width of whatever we draw, so the label clears the marker art regardless
            // of per-game icon dimensions (Skyrim's tall holds vs FNV's square icons vs a fallback dot).
            float markerHalfWidth;
            if (markers.Icons is not null && markers.Icons.TryGetValue(raw, out var icon))
            {
                // Embedded icons are pre-tinted to the color scheme; atlas crops are pre-styled. Either
                // way this is a plain blit — NOT a per-marker ColorMatrixEffect (which dominated frame time
                // at zoomed-out overview where every worldspace marker is in view). Height-normalized to
                // markerSize × per-game scale and aspect-preserved, so non-square icons (Skyrim's tall
                // city/hold markers) aren't squashed into the square cell and don't render cramped.
                float sw = icon.SizeInPixels.Width;
                float sh = icon.SizeInPixels.Height;
                var drawH = metrics.IconHeightPixels / zoom;
                var drawW = sh > 0f ? drawH * sw / sh : drawH;
                var iconDest = new Rect(pos.X - drawW / 2, pos.Y - drawH / 2, drawW, drawH);
                ds.DrawImage(icon, iconDest, new Rect(0, 0, sw, sh));
                markerHalfWidth = drawW / 2;
            }
            else
            {
                var fb = MapMarkerCatalog.Resolve(markers.Game, raw).Fallback;
                var color = Color.FromArgb(255, fb.R, fb.G, fb.B);
                var radius = markerSize / 2;
                ds.FillCircle(pos, radius, WorldMapColors.WithAlpha(color, 200));
                ds.DrawCircle(pos, radius, Colors.White, 1f / zoom);
                // No glyph for atlas games pre-RE (the distinct dot color carries the type); games with a
                // named table supply a glyph.
                if (!string.IsNullOrEmpty(fb.Glyph))
                {
                    ds.DrawText(fb.Glyph, destRect, Colors.White, glyphFormat);
                }

                markerHalfWidth = radius;
            }

            if (zoom > 0.05f && labelsDrawn < maxLabelsPerDraw && !string.IsNullOrEmpty(marker.MarkerName))
            {
                DrawMarkerLabel(ds, marker.MarkerName, pos, markerHalfWidth, labelFormat, tint, zoom);
                labelsDrawn++;
            }
        }
    }

    /// <summary>
    ///     Draws a marker's name label to the right of its art with a dark pill background. The pill
    ///     keeps the text legible over the colored object dots / bright terrain it would otherwise blend
    ///     into (the old background-less <c>DrawText</c> was unreadable when zoomed in), and the block is
    ///     vertically centered on the marker center (<paramref name="pos" />.Y) rather than offset by a
    ///     fixed amount — icon heights vary per game, so anchoring to the center keeps the label level
    ///     with the icon for every game. Mirrors the static export label style (<c>WorldMapExporter</c>).
    /// </summary>
    private static void DrawMarkerLabel(
        CanvasDrawingSession ds, string text, Vector2 pos, float markerHalfWidth,
        CanvasTextFormat labelFormat, Color textColor, float zoom)
    {
        // Measure once (NoWrap → natural single-line extent); reused for both the pill and the draw.
        using var layout = new CanvasTextLayout(ds, text, labelFormat, 0f, 0f);
        var b = layout.LayoutBounds;

        var padH = 4f / zoom;
        var padV = 2f / zoom;
        // The pill's left edge lands at textLeft + b.Left − padH ≈ pos.X + markerHalfWidth + (gap − padH), so the
        // gap must EXCEED padH or the pill sits flush against the icon (it read as "label too close to the icon").
        // gap = padH + clearance keeps a real gap between the marker art and the label pill at every zoom.
        var gap = padH + 4f / zoom;

        var textLeft = pos.X + markerHalfWidth + gap;
        var textTop = pos.Y - (float)(b.Top + b.Height / 2);

        var pill = new Rect(
            textLeft + b.Left - padH,
            textTop + b.Top - padV,
            b.Width + padH * 2,
            b.Height + padV * 2);

        var corner = 3f / zoom;
        ds.FillRoundedRectangle(pill, corner, corner, Color.FromArgb(200, 0, 0, 0));
        ds.DrawRoundedRectangle(pill, corner, corner, Color.FromArgb(90, 255, 255, 255), 0.5f / zoom);
        ds.DrawTextLayout(layout, new Vector2(textLeft, textTop), textColor);
    }

    internal static void DrawActorDots(
        CanvasDrawingSession ds,
        WorldViewData _data,
        List<CellRecord> activeCells,
        WorldSpatialIndex? spatialIndex,
        HashSet<PlacedObjectCategory> hiddenCategories,
        bool hideDisabledActors,
        float zoom, Vector2 panOffset,
        float canvasWidth, float canvasHeight)
    {
        if (zoom <= 0.02f)
        {
            return;
        }

        var npcHidden = hiddenCategories.Contains(PlacedObjectCategory.Npc);
        var creatureHidden = hiddenCategories.Contains(PlacedObjectCategory.Creature);
        if (npcHidden && creatureHidden)
        {
            return;
        }

        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);
        var dotRadius = 5f / zoom;
        var outlineWidth = 1f / zoom;
        var npcColor = WorldMapColors.GetCategoryColor(PlacedObjectCategory.Npc);
        var creatureColor = WorldMapColors.GetCategoryColor(PlacedObjectCategory.Creature);

        if (spatialIndex is not null)
        {
            var refs = GetRefScratch();
            spatialIndex.QueryActorsInViewport(tlWorld, brWorld, refs, dotRadius * 2);
            foreach (var obj in refs)
            {
                WorldMapActorDotRenderer.DrawActorDotIfVisible(ds, obj, npcHidden, creatureHidden,
                    hideDisabledActors, tlWorld, brWorld, dotRadius, outlineWidth, npcColor, creatureColor);
            }

            return;
        }

        foreach (var cell in activeCells)
        {
            if (cell.GridX.HasValue && cell.GridY.HasValue && !cell.HasPersistentObjects
                && !WorldMapViewportHelper.IsCellVisible(cell, tlWorld, brWorld))
            {
                continue;
            }

            foreach (var obj in cell.PlacedObjects)
            {
                if (obj.IsMapMarker)
                {
                    continue;
                }

                if (hideDisabledActors && obj.IsInitiallyDisabled)
                {
                    continue;
                }

                WorldMapActorDotRenderer.DrawActorDotIfVisible(ds, obj, npcHidden, creatureHidden,
                    hideDisabledActors, tlWorld, brWorld, dotRadius, outlineWidth, npcColor, creatureColor);
            }
        }
    }

    internal static void DrawSaveOverlay(
        CanvasDrawingSession ds,
        WorldViewData data,
        WorldSpatialIndex? spatialIndex,
        float zoom, Vector2 panOffset,
        float canvasWidth, float canvasHeight)
    {
        if (data.SaveOverlayMarkers == null || data.SaveOverlayMarkers.Count == 0)
        {
            return;
        }

        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);
        var dotRadius = 4f / zoom;
        var outlineWidth = 1f / zoom;

        var achrColor = Color.FromArgb(255, 0, 200, 200);
        var acreColor = Color.FromArgb(255, 255, 140, 0);
        var refrColor = Color.FromArgb(255, 120, 120, 120);

        var saveRefs = data.SaveOverlayMarkers;
        if (spatialIndex is not null)
        {
            var refs = GetRefScratch();
            spatialIndex.QuerySaveRefsInViewport(tlWorld, brWorld, refs, dotRadius * 2);
            foreach (var obj in refs)
            {
                WorldMapActorDotRenderer.DrawSaveOverlayRef(ds, obj, tlWorld, brWorld, dotRadius,
                    outlineWidth, achrColor, acreColor, refrColor);
            }
        }
        else if (saveRefs is not null)
        {
            foreach (var obj in saveRefs)
            {
                WorldMapActorDotRenderer.DrawSaveOverlayRef(ds, obj, tlWorld, brWorld, dotRadius,
                    outlineWidth, achrColor, acreColor, refrColor);
            }
        }

        // Player marker (prominent)
        if (data.PlayerPosition is var (px, py, _))
        {
            var playerPos = new Vector2(px, -py);
            if (WorldMapViewportHelper.IsPointInView(playerPos.X, playerPos.Y, tlWorld, brWorld, 20f / zoom))
            {
                var playerRadius = 8f / zoom;
                var playerOutline = 2f / zoom;
                ds.FillCircle(playerPos, playerRadius, Color.FromArgb(220, 255, 215, 0));
                ds.DrawCircle(playerPos, playerRadius, Colors.White, playerOutline);
                ds.DrawCircle(playerPos, playerRadius * 1.5f, Color.FromArgb(100, 255, 215, 0), playerOutline);
            }
        }
    }

    internal static void DrawPlacedObjectBox(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data,
        float zoom, bool outlineOnly = false)
    {
        var category = obj.IsMapMarker
            ? PlacedObjectCategory.MapMarker
            : obj.RecordType switch
            {
                "ACHR" => PlacedObjectCategory.Npc,
                "ACRE" => PlacedObjectCategory.Creature,
                _ => data.CategoryIndex.GetValueOrDefault(obj.BaseFormId, PlacedObjectCategory.Unknown)
            };
        var color = WorldMapColors.GetCategoryColor(category);
        var pos = new Vector2(obj.X, -obj.Y);
        var lineWidth = 1f / zoom;

        if (data.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = (bounds.X2 - bounds.X1) * 0.5f * obj.Scale;
            var halfH = (bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale;

            // Clamp extreme bounds to prevent a single object from dominating the map
            var wasClamped = halfW > MaxHalfExtent || halfH > MaxHalfExtent;
            halfW = Math.Min(halfW, MaxHalfExtent);
            halfH = Math.Min(halfH, MaxHalfExtent);

            if (halfW < 1f && halfH < 1f)
            {
                ds.FillCircle(pos, 6f / zoom, WorldMapColors.WithAlpha(color, 120));
                ds.DrawCircle(pos, 6f / zoom, color, lineWidth);
                return;
            }

            // Use reddish outline for clamped bounds to signal truncation
            var outlineColor = wasClamped ? Color.FromArgb(180, 255, 100, 100) : color;

            if (outlineOnly)
            {
                // Footprint yaw from the shared PlacedReferenceTransform (same source the 3D viewer
                // and WorldMapDrawingHelper use), so the outline path can't drift from the fill path.
                var rotation = Matrix3x2.CreateRotation(
                    PlacedReferenceTransform.MapCanvasYawRadians(obj.RotZ), pos);
                Span<Vector2> corners = stackalloc Vector2[4];
                corners[0] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y - halfH), rotation);
                corners[1] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y - halfH), rotation);
                corners[2] = Vector2.Transform(new Vector2(pos.X + halfW, pos.Y + halfH), rotation);
                corners[3] = Vector2.Transform(new Vector2(pos.X - halfW, pos.Y + halfH), rotation);
                ds.DrawLine(corners[0], corners[1], outlineColor, lineWidth);
                ds.DrawLine(corners[1], corners[2], outlineColor, lineWidth);
                ds.DrawLine(corners[2], corners[3], outlineColor, lineWidth);
                ds.DrawLine(corners[3], corners[0], outlineColor, lineWidth);
            }
            else
            {
                using var geometry = WorldMapDrawingHelper.CreateRotatedRectGeometry(ds, pos, halfW, halfH, obj.RotZ);
                ds.FillGeometry(geometry, WorldMapColors.WithAlpha(outlineColor, 60));
                ds.DrawGeometry(geometry, outlineColor, lineWidth);
            }
        }
        else
        {
            var radius = 12f / zoom;
            ds.FillCircle(pos, radius, WorldMapColors.WithAlpha(color, 80));
            ds.DrawCircle(pos, radius, color, lineWidth);
        }

        // Click-point circle at center
        var clickRadius = 6f / zoom;
        ds.FillCircle(pos, clickRadius, color);
        ds.DrawCircle(pos, clickRadius, Colors.White, 1f / zoom);
    }

    internal static void DrawPlacedObjectDot(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data, float zoom)
    {
        var category = obj.RecordType switch
        {
            "ACHR" => PlacedObjectCategory.Npc,
            "ACRE" => PlacedObjectCategory.Creature,
            _ => data.CategoryIndex.GetValueOrDefault(obj.BaseFormId, PlacedObjectCategory.Unknown)
        };
        var color = WorldMapColors.GetCategoryColor(category);
        var pos = new Vector2(obj.X, -obj.Y);
        var radius = 4f / zoom;
        ds.FillCircle(pos, radius, color);
    }

    internal static void DrawPlacedObjectHighlight(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data,
        MarkerRenderContext markers, float zoom)
    {
        DrawObjectOutline(ds, obj, data, markers, zoom, Colors.Yellow, 3f, 12f);
    }

    internal static void DrawPlacedObjectHighlight(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data, float zoom) =>
        DrawPlacedObjectHighlight(ds, obj, data, new MarkerRenderContext(data.Game, null), zoom);

    internal static void DrawSelectedObjectHighlight(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data,
        MarkerRenderContext markers, float zoom)
    {
        DrawObjectOutline(ds, obj, data, markers, zoom,
            Color.FromArgb(255, 0, 200, 255), 4f, 14f);
    }

    internal static void DrawSelectedObjectHighlight(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data, float zoom) =>
        DrawSelectedObjectHighlight(ds, obj, data, new MarkerRenderContext(data.Game, null), zoom);

    internal static void DrawObjectOutline(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data,
        MarkerRenderContext markers, float zoom,
        Color color, float strokeWidth, float fallbackRadius)
    {
        var pos = new Vector2(obj.X, -obj.Y);

        if (obj.IsMapMarker)
        {
            var metrics = MapMarkerMetrics.Resolve(GameProfiles.For(markers.Game), zoom);
            var raw = obj.MarkerType.HasValue ? (int)obj.MarkerType.Value : 0;
            var visualHeight = metrics.VisualDiameterPixels;
            var visualWidth = visualHeight;
            if (markers.Icons?.TryGetValue(raw, out var icon) == true && icon.SizeInPixels.Height > 0)
            {
                visualHeight = metrics.IconHeightPixels;
                visualWidth = visualHeight * (float)icon.SizeInPixels.Width / (float)icon.SizeInPixels.Height;
            }

            var halfWidth = MathF.Max(metrics.HitRadiusPixels, visualWidth * 0.5f) / zoom;
            var halfHeight = MathF.Max(metrics.HitRadiusPixels, visualHeight * 0.5f) / zoom;
            ds.DrawRectangle(
                new Rect(pos.X - halfWidth, pos.Y - halfHeight, halfWidth * 2f, halfHeight * 2f),
                color, strokeWidth / zoom);
            return;
        }

        if (data.BoundsIndex.TryGetValue(obj.BaseFormId, out var bounds))
        {
            var halfW = Math.Min((bounds.X2 - bounds.X1) * 0.5f * obj.Scale, MaxHalfExtent);
            var halfH = Math.Min((bounds.Y2 - bounds.Y1) * 0.5f * obj.Scale, MaxHalfExtent);

            if (halfW >= 1f || halfH >= 1f)
            {
                using var geometry = WorldMapDrawingHelper.CreateRotatedRectGeometry(ds, pos, halfW, halfH, obj.RotZ);
                ds.DrawGeometry(geometry, color, strokeWidth / zoom);
                return;
            }
        }

        ds.DrawCircle(pos, fallbackRadius / zoom, color, strokeWidth / zoom);
    }

    internal static void DrawSpawnOverlay(
        CanvasDrawingSession ds, PlacedReference selectedObj, WorldViewData data, float zoom)
    {
        if (data.SpawnIndex == null)
        {
            return;
        }

        var spawnIndex = data.SpawnIndex;
        var isAchr = selectedObj.RecordType == "ACHR";
        var isAcre = selectedObj.RecordType == "ACRE";
        if (!isAchr && !isAcre)
        {
            return;
        }

        var overlayColor = isAchr
            ? Color.FromArgb(50, 0, 200, 0)
            : Color.FromArgb(50, 220, 50, 50);
        var overlayBorder = isAchr
            ? Color.FromArgb(120, 0, 200, 0)
            : Color.FromArgb(120, 220, 50, 50);

        var cellWorldSize = data.CellWorldSize;
        var actorFormIds = new List<uint>();
        if (spawnIndex.LeveledListEntries.TryGetValue(selectedObj.BaseFormId, out var resolved))
        {
            actorFormIds.AddRange(resolved.Distinct());
        }
        else
        {
            actorFormIds.Add(selectedObj.BaseFormId);
        }

        foreach (var actorFid in actorFormIds)
        {
            if (!spawnIndex.ActorToPackageCells.TryGetValue(actorFid, out var cells))
            {
                continue;
            }

            foreach (var cellFid in cells.Distinct())
            {
                if (data.CellByFormId.TryGetValue(cellFid, out var cell) &&
                    cell.GridX.HasValue && cell.GridY.HasValue)
                {
                    var originX = cell.GridX.Value * cellWorldSize;
                    var originY = -(cell.GridY.Value + 1) * cellWorldSize;
                    ds.FillRectangle(
                        new Rect(originX, originY, cellWorldSize, cellWorldSize),
                        overlayColor);
                    ds.DrawRectangle(
                        new Rect(originX, originY, cellWorldSize, cellWorldSize),
                        overlayBorder, 2f / zoom);
                }
            }
        }

        foreach (var actorFid in actorFormIds)
        {
            if (!spawnIndex.ActorToPackageRefs.TryGetValue(actorFid, out var refs))
            {
                continue;
            }

            foreach (var refLoc in refs)
            {
                if (data.PlacedRefs.TryGetPosition(refLoc.RefFormId, out var refPos))
                {
                    var center = new Vector2(refPos.X, -refPos.Y);
                    var radius = refLoc.Radius > 0 ? (float)refLoc.Radius : 500f;
                    ds.FillCircle(center, radius, overlayColor);
                    ds.DrawCircle(center, radius, overlayBorder, 2f / zoom);
                }
            }
        }
    }

    internal static PlacedObjectCategory GetObjectCategory(PlacedReference obj, WorldViewData? data)
    {
        if (obj.IsMapMarker)
        {
            return PlacedObjectCategory.MapMarker;
        }

        return obj.RecordType switch
        {
            "ACHR" => PlacedObjectCategory.Npc,
            "ACRE" => PlacedObjectCategory.Creature,
            _ => data?.CategoryIndex.GetValueOrDefault(obj.BaseFormId, PlacedObjectCategory.Unknown)
                 ?? PlacedObjectCategory.Unknown
        };
    }

    private static List<PlacedReference> GetRefScratch()
    {
        var list = t_refScratch;
        if (list is null)
        {
            list = new List<PlacedReference>(512);
            t_refScratch = list;
        }

        list.Clear();
        return list;
    }

    private static void DrawPlacedObjectInOverview(
        CanvasDrawingSession ds,
        PlacedReference obj,
        WorldViewData data,
        HashSet<PlacedObjectCategory> hiddenCategories,
        bool hideDisabledActors,
        Vector2 tlWorld,
        Vector2 brWorld,
        float zoom)
    {
        if (hiddenCategories.Contains(GetObjectCategory(obj, data)))
        {
            return;
        }

        if (hideDisabledActors && obj.IsInitiallyDisabled)
        {
            return;
        }

        if (!WorldMapViewportHelper.IsPointInView(obj.X, -obj.Y, tlWorld, brWorld,
                WorldMapViewportHelper.GetObjectViewMargin(obj, data)))
        {
            return;
        }

        if (zoom > 0.07f)
        {
            DrawPlacedObjectBox(ds, obj, data, zoom, outlineOnly: true);
        }
        else
        {
            DrawPlacedObjectDot(ds, obj, data, zoom);
        }
    }
}
