using System.Numerics;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats;
using BethesdaMultitool.Core.Formats.Esm.Enums;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>
///     Renders the world overview mode: heightmap, cell grid, placed objects,
///     map markers, actor dots, save overlay, and selection/hover highlights.
/// </summary>
internal static class WorldMapOverviewRenderer
{
    private const float CellWorldSize = 4096f;
    [ThreadStatic] private static List<PlacedReference>? t_refScratch;

    /// <summary>
    ///     Reusable per-frame dedup dict for <see cref="DrawTextureCellBitmaps" />. Sized to
    ///     ~256 entries (typical fill viewport) on first frame; subsequent frames clear + refill
    ///     without allocating. The dict holds <see cref="CanvasBitmap" /> references across
    ///     frames — fine because both the source dict and this scratch live on the UI thread,
    ///     so no stale-reference race exists.
    /// </summary>
    [ThreadStatic] private static Dictionary<(int gx, int gy), (int ppc, CanvasBitmap bmp)>? t_bestPerCellScratch;

    /// <summary>
    ///     Maximum half-extent in world units for rendering a placed object's bounding box.
    ///     Half a cell (2048) is generous for even the largest buildings; anything beyond
    ///     this is likely corrupted OBND data or extreme scale and would obscure the map.
    /// </summary>
    private const float MaxHalfExtent = 2048f;

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
        Dictionary<MapMarkerType, CanvasBitmap>? markerIconBitmaps,
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

        var overlayActive = showRenderedObjects && renderedObjectsOverlay is not null;

        // 1. Layer background — one of: coarse multi-cell tiles (oversized worldspace, e.g. FO76
        //    APPALACHIA), a per-cell composite (TerrainTextures, to dodge the GPU max-texture-size
        //    limit), or a single giant bitmap (most layers at normal sizes). Only one is set at a time
        //    per EnsureHeightmapBitmap.
        if (coarseTileBitmaps is not null && coarseTileCellSpan > 0)
        {
            DrawCoarseTileBitmaps(ds, coarseTileBitmaps, coarseTileCellSpan, coarseTilePixelsPerCell, zoom);
        }
        else if (textureCellBitmaps is not null)
        {
            DrawTextureCellBitmaps(ds, textureCellBitmaps, zoom);
        }
        else if (worldHeightmapBitmap != null)
        {
            // Scale by the bitmap's actual px/cell: heightmap-family aggregates are 33 px/cell, but the
            // TerrainTextures aggregate LOD uses an adaptive (smaller) px/cell sized to the worldspace.
            var ppc = worldHmPixelsPerCell > 0 ? worldHmPixelsPerCell : 33f;
            var pixelScale = CellWorldSize / ppc;
            var bitmapWorldW = worldHmPixelWidth * pixelScale;
            var bitmapWorldH = worldHmPixelHeight * pixelScale;
            var bitmapX = worldHmMinX * CellWorldSize;
            var bitmapY = -(worldHmMaxY + 1) * CellWorldSize;

            // A plain CanvasBitmap has no mip chain, so a fixed-tap cubic still moires the aggregate once
            // it is shrunk several-fold (the zoomed-out case). Anisotropic minification resamples it
            // through D2D's internally-generated mip pyramid + anisotropic footprint — the same thing a
            // GPU sampler does — which is what removes the shimmer. One DrawImage/frame, so quality-first.
            // Source rect = the full bitmap.
            var aggInterp = ChooseInterpolation(ppc, CellWorldSize * Math.Max(zoom, 1e-6f), preferQuality: true);
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
                zoom, panOffset, canvasWidth, canvasHeight);
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
                DrawWaterCellBitmaps(ds, waterCellBitmaps, zoom);
            }
            else if (worldWaterBitmap is not null)
            {
                // Same world-rect math as the aggregate-terrain branch (33 px/cell, scaled to world space).
                var ppc = worldWaterPixelsPerCell > 0 ? worldWaterPixelsPerCell : 33f;
                var pixelScale = CellWorldSize / ppc;
                var src = worldWaterBitmap.SizeInPixels;
                var waterInterp = ChooseInterpolation(ppc, CellWorldSize * Math.Max(zoom, 1e-6f), preferQuality: true);
                ds.DrawImage(worldWaterBitmap,
                    new Rect(worldWaterMinX * CellWorldSize, -(worldWaterMaxY + 1) * CellWorldSize,
                        worldWaterPixelWidth * pixelScale, worldWaterPixelHeight * pixelScale),
                    new Rect(0, 0, src.Width, src.Height),
                    1f,
                    waterInterp);
            }
        }

        // 3. Placed objects (LOD-based) — skipped when the rendered-models overlay replaces them.
        if (!overlayActive && zoom > 0.05f && activeCells.Count > 0)
        {
            var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
                canvasWidth, canvasHeight, zoom, panOffset);

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
        DrawMapMarkers(ds, filteredMarkers, markerIconBitmaps, hiddenCategories,
            zoom, panOffset, canvasWidth, canvasHeight, colorScheme);

        // 4b. NPC/Creature dots (always visible)
        DrawActorDots(ds, data, activeCells, spatialIndex, hiddenCategories, hideDisabledActors,
            zoom, panOffset, canvasWidth, canvasHeight);

        // 4c. Save overlay markers (save file positions)
        DrawSaveOverlay(ds, data, spatialIndex, zoom, panOffset, canvasWidth, canvasHeight);

        // 5. Selected object highlight
        if (selectedObject != null)
        {
            DrawSelectedObjectHighlight(ds, selectedObject, data, zoom);
            DrawSpawnOverlay(ds, selectedObject, data, zoom);
        }

        // 6. Hovered object highlight (overview)
        if (hoveredObject != null)
        {
            DrawPlacedObjectHighlight(ds, hoveredObject, data, zoom);
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
        float canvasWidth, float canvasHeight)
    {
        if (activeCells.Count == 0)
        {
            return;
        }

        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);

        var startCellX = (int)Math.Floor(Math.Min(tlWorld.X, brWorld.X) / CellWorldSize) - 1;
        var endCellX = (int)Math.Ceiling(Math.Max(tlWorld.X, brWorld.X) / CellWorldSize) + 1;
        var startCellY = (int)Math.Floor(Math.Min(tlWorld.Y, brWorld.Y) / CellWorldSize) - 1;
        var endCellY = (int)Math.Ceiling(Math.Max(tlWorld.Y, brWorld.Y) / CellWorldSize) + 1;

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

                var worldLeft = cx * CellWorldSize;
                var worldTop = -(cy + 1) * CellWorldSize;
                ds.FillRectangle(worldLeft, worldTop, CellWorldSize, CellWorldSize, cellFill);
            }
        }

        var gridColor = Color.FromArgb(40, 255, 255, 255);
        var lineWidth = 1f / zoom;

        // Aggregate all H + V grid lines into a single CanvasGeometry so the Win2D command
        // list carries one DrawGeometry instead of up to ~800 individual DrawLine calls per
        // frame. Each line is an open figure on the path. Geometry + path builder are both
        // IDisposable; `using` is correct.
        var startWorldY = startCellY * CellWorldSize;
        var endWorldY = endCellY * CellWorldSize;
        var startWorldX = startCellX * CellWorldSize;
        var endWorldX = endCellX * CellWorldSize;
        using var pathBuilder = new CanvasPathBuilder(ds);
        for (var cx = startCellX; cx <= endCellX; cx++)
        {
            var worldX = cx * CellWorldSize;
            pathBuilder.BeginFigure(worldX, startWorldY);
            pathBuilder.AddLine(worldX, endWorldY);
            pathBuilder.EndFigure(CanvasFigureLoop.Open);
        }
        for (var cy = startCellY; cy <= endCellY; cy++)
        {
            var worldY = cy * CellWorldSize;
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
                var labelX = cx * CellWorldSize + 50;
                var labelY = -(cy + 1) * CellWorldSize + 50;

                if (!WorldMapViewportHelper.IsPointInView(labelX, labelY, tlWorld, brWorld, CellWorldSize))
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
        Dictionary<MapMarkerType, CanvasBitmap>? markerIconBitmaps,
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
        var markerSize = 16f / zoom;

        using var labelFormat = new CanvasTextFormat
        {
            FontSize = 10f / zoom,
            FontFamily = "Segoe UI"
        };

        using var glyphFormat = new CanvasTextFormat
        {
            FontSize = 12f / zoom,
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

        foreach (var marker in filteredMarkers)
        {
            var pos = new Vector2(marker.X, -marker.Y);

            if (!WorldMapViewportHelper.IsPointInView(pos.X, pos.Y, tlWorld, brWorld, markerSize * 2))
            {
                continue;
            }

            var destRect = new Rect(
                pos.X - markerSize / 2, pos.Y - markerSize / 2,
                markerSize, markerSize);

            if (marker.MarkerType.HasValue &&
                markerIconBitmaps?.TryGetValue(marker.MarkerType.Value, out var icon) == true)
            {
                // Icons are pre-tinted to the color scheme by EnsureTintedMarkerIcons, so this is a plain
                // blit — NOT a per-marker ColorMatrixEffect (which dominated frame time at zoomed-out
                // overview where every worldspace marker is in view).
                var iconSrc = new Rect(0, 0, icon.SizeInPixels.Width, icon.SizeInPixels.Height);
                ds.DrawImage(icon, destRect, iconSrc);
            }
            else
            {
                var color = WorldMapColors.GetMarkerColor(marker.MarkerType);
                var radius = markerSize / 2;
                ds.FillCircle(pos, radius, WorldMapColors.WithAlpha(color, 200));
                ds.DrawCircle(pos, radius, Colors.White, 1f / zoom);
                var glyph = WorldMapColors.GetMarkerGlyph(marker.MarkerType);
                ds.DrawText(glyph, destRect, Colors.White, glyphFormat);
            }

            if (zoom > 0.05f && labelsDrawn < maxLabelsPerDraw && !string.IsNullOrEmpty(marker.MarkerName))
            {
                var labelPos = new Vector2(pos.X + markerSize / 2 + 2f / zoom, pos.Y - markerSize / 4);
                ds.DrawText(marker.MarkerName, labelPos, tint, labelFormat);
                labelsDrawn++;
            }
        }
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
                DrawActorDotIfVisible(ds, obj, npcHidden, creatureHidden, hideDisabledActors,
                    tlWorld, brWorld, dotRadius, outlineWidth, npcColor, creatureColor);
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

                DrawActorDotIfVisible(ds, obj, npcHidden, creatureHidden, hideDisabledActors,
                    tlWorld, brWorld, dotRadius, outlineWidth, npcColor, creatureColor);
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
                DrawSaveOverlayRef(ds, obj, tlWorld, brWorld, dotRadius, outlineWidth,
                    achrColor, acreColor, refrColor);
            }
        }
        else if (saveRefs is not null)
        {
            foreach (var obj in saveRefs)
            {
                DrawSaveOverlayRef(ds, obj, tlWorld, brWorld, dotRadius, outlineWidth,
                    achrColor, acreColor, refrColor);
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
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data, float zoom)
    {
        DrawObjectOutline(ds, obj, data, zoom, Colors.Yellow, 3f, 12f);
    }

    internal static void DrawSelectedObjectHighlight(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data, float zoom)
    {
        DrawObjectOutline(ds, obj, data, zoom, Color.FromArgb(255, 0, 200, 255), 4f, 14f);
    }

    internal static void DrawObjectOutline(
        CanvasDrawingSession ds, PlacedReference obj, WorldViewData data,
        float zoom, Color color, float strokeWidth, float fallbackRadius)
    {
        var pos = new Vector2(obj.X, -obj.Y);

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
                    var originX = cell.GridX.Value * CellWorldSize;
                    var originY = -(cell.GridY.Value + 1) * CellWorldSize;
                    ds.FillRectangle(
                        new Rect(originX, originY, CellWorldSize, CellWorldSize),
                        overlayColor);
                    ds.DrawRectangle(
                        new Rect(originX, originY, CellWorldSize, CellWorldSize),
                        overlayBorder, 2f / zoom);
                }
            }
        }

        if (data.RefPositionIndex != null)
        {
            foreach (var actorFid in actorFormIds)
            {
                if (!spawnIndex.ActorToPackageRefs.TryGetValue(actorFid, out var refs))
                {
                    continue;
                }

                foreach (var refLoc in refs)
                {
                    if (data.RefPositionIndex.TryGetValue(refLoc.RefFormId, out var refPos))
                    {
                        var center = new Vector2(refPos.X, -refPos.Y);
                        var radius = refLoc.Radius > 0 ? (float)refLoc.Radius : 500f;
                        ds.FillCircle(center, radius, overlayColor);
                        ds.DrawCircle(center, radius, overlayBorder, 2f / zoom);
                    }
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

    /// <summary>
    ///     Minification factor (cached-tile pixels ÷ on-screen cell pixels) above which a tile is
    ///     resampled with the expensive <see cref="CanvasImageInterpolation.HighQualityCubic" />
    ///     anti-aliasing filter. At or below it the tile is close to screen resolution (the cache holds
    ///     a tier sized to the current zoom — see <c>ChooseTerrainTexturePixelsPerCell</c>), so cheap
    ///     bilinear is indistinguishable and ~4× faster per output pixel. Cubic is reserved for the
    ///     transient case where only a stale higher-res tier is cached (just after a zoom-out, before
    ///     the matching tier streams in) — there the heavy minification WOULD alias under bilinear.
    /// </summary>
    private const float MipCubicMinifyThreshold = 2.2f;

    /// <summary>Profiling A/B: when set, revert to the pre-mip "highest tier + always cubic" draw.</summary>
    private static readonly bool s_legacyTerrainDraw =
        EnvironmentVariables.IsEnabled(EnvironmentVariables.Map2D.LegacyTerrainDraw);

    /// <summary>
    ///     Composites the per-cell TerrainTextures bitmaps. Each entry is its own
    ///     <see cref="CanvasBitmap" /> drawn into the cell's world rect. With multi-resolution caching
    ///     (P2) the dict may hold multiple <c>pixelsPerCell</c> tiers for one <c>(gx, gy)</c>; the tier
    ///     set (33/66/132/264/528) is a factor-2 mip chain, so we pick the SMALLEST tier that still
    ///     covers the on-screen cell size (mip selection) rather than the absolute highest. That avoids
    ///     cubic-minifying a stale 528-px tile 8–16× when zoomed out — the old "always highest" rule —
    ///     which both aliased (2D-3) and was the dominant per-frame cost (2D-2). The chosen tile is then
    ///     drawn with bilinear when it's near screen resolution and cubic only when heavily minified.
    /// </summary>
    /// <summary>
    ///     Composites the coarse multi-cell tiles produced for an oversized worldspace (FO76 APPALACHIA).
    ///     Each tile covers a <paramref name="tileCellSpan" />² block of cells, so it's positioned at the
    ///     tile-grid origin (<c>tileGx·tileCellSpan·CellWorldSize</c>) and sized to span that whole block.
    ///     World north-Y maps to canvas <c>-Y</c>, so the tile's top edge is its northernmost cell row —
    ///     matching <see cref="DrawTextureCellBitmaps" />'s per-cell placement with span 1. Tiles are
    ///     opaque; a ~1px world-space outset hides the inter-tile seam exactly like the per-cell path.
    /// </summary>
    private static void DrawCoarseTileBitmaps(
        CanvasDrawingSession ds,
        IReadOnlyDictionary<(int tileGx, int tileGy), CanvasBitmap> bitmaps,
        int tileCellSpan, int pixelsPerCell, float zoom)
    {
        var tileWorldSize = tileCellSpan * CellWorldSize;
        var outset = Math.Min(1f / Math.Max(zoom, 1e-6f), CellWorldSize * 0.01f);
        // Source px/cell vs on-screen px/cell drives the minify/magnify interpolation choice (GPU-sampler
        // analogue). One DrawImage per tile (≤ a few dozen), so quality-first anisotropic minification.
        var targetScreenPpc = CellWorldSize * Math.Max(zoom, 1e-6f);

        foreach (var ((tileGx, tileGy), bmp) in bitmaps)
        {
            var originX = tileGx * tileWorldSize;
            var originY = -(tileGy + 1) * tileWorldSize;
            var src = bmp.SizeInPixels;
            var interpolation = ChooseInterpolation(pixelsPerCell, targetScreenPpc, preferQuality: true);
            ds.DrawImage(bmp,
                new Rect(originX - outset, originY - outset,
                    tileWorldSize + 2 * outset, tileWorldSize + 2 * outset),
                new Rect(0, 0, src.Width, src.Height),
                1f,
                interpolation);
        }
    }

    private static void DrawTextureCellBitmaps(
        CanvasDrawingSession ds,
        IReadOnlyDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap> bitmaps,
        float zoom)
    {
        // Outset each tile's destination rect by ~1 device pixel in world units so adjacent
        // OPAQUE tiles overlap by ~1px and the inter-tile seam disappears (the seam shows when the
        // cell-grid overlay — which used to mask it — is hidden). World-units-per-screen-pixel =
        // 1/zoom (the view transform is Scale(zoom)·Translate(pan)). The CellWorldSize*0.01f clamp
        // caps the outset at very low zoom (where the seam is already sub-pixel) so it can never
        // reach a non-adjacent cell. Lossless: tiles are opaque (alpha=255) with a +1-cell blend
        // margin, so the overlap band is opaque-over-opaque with identical edge colors — no
        // darkening, and mixed per-cell resolutions are fine.
        var outset = Math.Min(1f / Math.Max(zoom, 1e-6f), CellWorldSize * 0.01f);

        // Target on-screen pixels per cell = the mip level we want. The cache may hold several tiers
        // per cell; pick the one nearest this (smallest tier that still covers it).
        var targetScreenPpc = CellWorldSize * Math.Max(zoom, 1e-6f);

        // Per-frame scan over ≤256 entries (typical fill viewport) to pick the mip-correct tier per
        // (gx, gy). Reused thread-local scratch dict avoids a 60-Hz allocation; clear + refill keeps
        // GC out of the critical path.
        var bestPerCell = t_bestPerCellScratch ??= new Dictionary<(int gx, int gy), (int ppc, CanvasBitmap bmp)>(256);
        bestPerCell.Clear();
        foreach (var (key, bmp) in bitmaps)
        {
            var cellKey = (key.gx, key.gy);
            var candidate = (key.pixelsPerCell, bmp);
            bestPerCell[cellKey] = bestPerCell.TryGetValue(cellKey, out var current)
                ? s_legacyTerrainDraw
                    ? (key.pixelsPerCell > current.ppc ? candidate : current) // legacy: highest tier
                    : PickMipTier(current, candidate, targetScreenPpc)
                : candidate;
        }

        foreach (var ((gx, gy), (ppc, bmp)) in bestPerCell)
        {
            var originX = gx * CellWorldSize;
            var originY = -(gy + 1) * CellWorldSize;
            // Cheap bilinear when the tile is near screen resolution (mip-correct — the common steady
            // state). When only a stale higher-res tier stands in, it would be minified several-fold and
            // alias: anisotropic (mip-pyramid + anisotropic footprint) resamples it cleanly instead of
            // the old fixed-tap cubic. ChooseInterpolation centralizes the policy; per-cell tiles keep
            // the perf-validated Linear steady state (preferQuality: false).
            var interpolation = ChooseInterpolation(ppc, targetScreenPpc, preferQuality: false);
            var src = bmp.SizeInPixels;
            ds.DrawImage(bmp,
                new Rect(originX - outset, originY - outset,
                    CellWorldSize + 2 * outset, CellWorldSize + 2 * outset),
                new Rect(0, 0, src.Width, src.Height),
                1f,
                interpolation);
        }
    }

    /// <summary>
    ///     Mip selection between two cached tiers of the same cell: prefer the SMALLEST tier whose
    ///     resolution still covers the on-screen cell size (so it's minified, never blurrily upscaled);
    ///     when neither covers it (all cached tiers are below screen resolution) prefer the LARGEST
    ///     (best for the unavoidable magnification).
    /// </summary>
    private static (int ppc, CanvasBitmap bmp) PickMipTier(
        (int ppc, CanvasBitmap bmp) a, (int ppc, CanvasBitmap bmp) b, float targetScreenPpc)
    {
        var aCovers = a.ppc >= targetScreenPpc;
        var bCovers = b.ppc >= targetScreenPpc;
        if (aCovers && bCovers) return a.ppc <= b.ppc ? a : b; // both cover → smaller is closest to screen res
        if (aCovers) return a;
        if (bCovers) return b;
        return a.ppc >= b.ppc ? a : b;                          // neither covers → larger magnifies best
    }

    /// <summary>
    ///     Picks the Win2D interpolation mode for compositing a terrain/water tile (or the aggregate
    ///     bitmap) from its source resolution (<paramref name="sourcePpc" />, px/cell) versus the
    ///     on-screen cell size (<paramref name="targetScreenPpc" />). The progression mirrors a GPU
    ///     texture sampler:
    ///     <list type="bullet">
    ///       <item>Heavy minification (source ≫ screen) → <see cref="CanvasImageInterpolation.Anisotropic" />:
    ///         Direct2D resamples through an internally-generated mip pyramid with an anisotropic
    ///         footprint. This is what actually removes the zoomed-out moire/shimmer — a fixed-tap
    ///         cubic under-samples once the image is shrunk more than a few-fold.</item>
    ///       <item>Mild minification (source modestly above screen) → <see cref="CanvasImageInterpolation.Linear" />
    ///         for perf-sensitive per-cell tiles (no visible aliasing there, ~4× cheaper per output
    ///         pixel — the steady state validated by the 2D-2/2D-3 work), or <see cref="CanvasImageInterpolation.Anisotropic" />
    ///         when <paramref name="preferQuality" /> is set (the single aggregate draw, where cost is
    ///         one DrawImage/frame and quality wins).</item>
    ///       <item>Magnification (source below screen) → <see cref="CanvasImageInterpolation.HighQualityCubic" />:
    ///         cubic upscales a low-res tier/aggregate cleanly.</item>
    ///     </list>
    ///     The <c>FALLOUT_MAP2D_LEGACY_TERRAIN_DRAW</c> A/B toggle forces the pre-anisotropic
    ///     "always HighQualityCubic" behavior.
    /// </summary>
    private static CanvasImageInterpolation ChooseInterpolation(
        float sourcePpc, float targetScreenPpc, bool preferQuality)
    {
        if (s_legacyTerrainDraw)
        {
            return CanvasImageInterpolation.HighQualityCubic;
        }

        if (!preferQuality)
        {
            // Perf-sensitive per-cell tiles: HighQualityCubic only for
            // the heavy-minify transient (a stale higher-res tier standing in just after a zoom/pan), cheap
            // bilinear for the near-screen steady state. Anisotropic is deliberately NOT used here: the
            // per-cell view is the zoomed-IN regime (tiles near screen res, with their own mip-tier chain),
            // so it doesn't show the reported moire — that lives in the zoomed-OUT aggregate below. Spending
            // anisotropic across hundreds of transient tiles only inflates the churn tail for no visible win.
            return sourcePpc > targetScreenPpc * MipCubicMinifyThreshold
                ? CanvasImageInterpolation.HighQualityCubic
                : CanvasImageInterpolation.Linear;
        }

        // Single aggregate draw (one DrawImage/frame, so its extra cost is free): quality-first. This is
        // the zoomed-out bitmap where the graininess/moire was reported. Minification resamples through
        // D2D's internally-generated mip pyramid + anisotropic footprint — the GPU-sampler equivalent that
        // a fixed-tap cubic can't match once the image is shrunk several-fold. Magnification uses cubic.
        return sourcePpc > targetScreenPpc
            ? CanvasImageInterpolation.Anisotropic
            : CanvasImageInterpolation.HighQualityCubic;
    }

    /// <summary>
    ///     Composites the per-cell standalone water tiles over whatever is already drawn (terrain + the
    ///     model overlay). Mirrors <see cref="DrawTextureCellBitmaps" />'s best-resolution-per-cell pick
    ///     but draws at the EXACT cell rect with NO seam outset: the water tiles are translucent, so an
    ///     overlapping outset band would double-composite into visibly darker seams. Adjacent water tiles
    ///     meeting edge-to-edge is correct (shoreline coverage already fades to 0 at dry edges).
    /// </summary>
    private static void DrawWaterCellBitmaps(
        CanvasDrawingSession ds,
        IReadOnlyDictionary<(int gx, int gy, int pixelsPerCell), CanvasBitmap> bitmaps,
        float zoom)
    {
        var targetScreenPpc = CellWorldSize * Math.Max(zoom, 1e-6f);
        var bestPerCell = t_bestPerCellScratch ??= new Dictionary<(int gx, int gy), (int ppc, CanvasBitmap bmp)>(256);
        bestPerCell.Clear();
        foreach (var (key, bmp) in bitmaps)
        {
            var cellKey = (key.gx, key.gy);
            var candidate = (key.pixelsPerCell, bmp);
            bestPerCell[cellKey] = bestPerCell.TryGetValue(cellKey, out var current)
                ? PickMipTier(current, candidate, targetScreenPpc)
                : candidate;
        }

        foreach (var ((gx, gy), (ppc, bmp)) in bestPerCell)
        {
            var originX = gx * CellWorldSize;
            var originY = -(gy + 1) * CellWorldSize;
            var interpolation = ChooseInterpolation(ppc, targetScreenPpc, preferQuality: false);
            var src = bmp.SizeInPixels;
            ds.DrawImage(bmp,
                new Rect(originX, originY, CellWorldSize, CellWorldSize),
                new Rect(0, 0, src.Width, src.Height),
                1f,
                interpolation);
        }
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

    private static void DrawActorDotIfVisible(
        CanvasDrawingSession ds,
        PlacedReference obj,
        bool npcHidden,
        bool creatureHidden,
        bool hideDisabledActors,
        Vector2 tlWorld,
        Vector2 brWorld,
        float dotRadius,
        float outlineWidth,
        Color npcColor,
        Color creatureColor)
    {
        if (obj.IsMapMarker)
        {
            return;
        }

        if (hideDisabledActors && obj.IsInitiallyDisabled)
        {
            return;
        }

        Color color;
        if (obj.RecordType == "ACHR" && !npcHidden)
        {
            color = npcColor;
        }
        else if (obj.RecordType == "ACRE" && !creatureHidden)
        {
            color = creatureColor;
        }
        else
        {
            return;
        }

        var pos = new Vector2(obj.X, -obj.Y);
        if (!WorldMapViewportHelper.IsPointInView(pos.X, pos.Y, tlWorld, brWorld, dotRadius * 2))
        {
            return;
        }

        var fillAlpha = obj.IsInitiallyDisabled ? (byte)60 : (byte)180;
        var outlineAlpha = obj.IsInitiallyDisabled ? (byte)80 : (byte)255;
        ds.FillCircle(pos, dotRadius, WorldMapColors.WithAlpha(color, fillAlpha));
        ds.DrawCircle(pos, dotRadius, WorldMapColors.WithAlpha(Colors.White, outlineAlpha), outlineWidth);
    }

    private static void DrawSaveOverlayRef(
        CanvasDrawingSession ds,
        PlacedReference obj,
        Vector2 tlWorld,
        Vector2 brWorld,
        float dotRadius,
        float outlineWidth,
        Color achrColor,
        Color acreColor,
        Color refrColor)
    {
        var pos = new Vector2(obj.X, -obj.Y);
        if (!WorldMapViewportHelper.IsPointInView(pos.X, pos.Y, tlWorld, brWorld, dotRadius * 2))
        {
            return;
        }

        var color = obj.RecordType switch
        {
            "ACHR" => achrColor,
            "ACRE" => acreColor,
            _ => refrColor
        };

        ds.FillCircle(pos, dotRadius, WorldMapColors.WithAlpha(color, 150));
        ds.DrawCircle(pos, dotRadius, WorldMapColors.WithAlpha(Colors.White, 200), outlineWidth);
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
