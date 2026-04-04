using System.Numerics;
using FalloutXbox360Utils.Core.Formats;
using FalloutXbox360Utils.Core.Formats.Esm.Enums;
using FalloutXbox360Utils.Core.Formats.Esm.Export;
using FalloutXbox360Utils.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.UI;
using Windows.Foundation;
using Windows.UI;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils;

/// <summary>
///     Renders the world overview mode: heightmap, cell grid, placed objects,
///     map markers, actor dots, save overlay, and selection/hover highlights.
/// </summary>
internal static class WorldMapOverviewRenderer
{
    private const float CellWorldSize = 4096f;

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
        CanvasBitmap? worldHeightmapBitmap,
        int worldHmPixelWidth, int worldHmPixelHeight,
        int worldHmMinX, int worldHmMaxY,
        float zoom, Vector2 panOffset,
        float canvasWidth, float canvasHeight,
        HashSet<PlacedObjectCategory> hiddenCategories,
        bool hideDisabledActors,
        PlacedReference? selectedObject,
        PlacedReference? hoveredObject,
        Dictionary<MapMarkerType, CanvasBitmap>? markerIconBitmaps,
        HeightmapColorScheme colorScheme)
    {
        var transform = WorldMapViewportHelper.GetViewTransform(zoom, panOffset);
        ds.Transform = transform;

        // 1. Heightmap background
        if (worldHeightmapBitmap != null)
        {
            var pixelScale = CellWorldSize / 33f; // HmGridSize
            var bitmapWorldW = worldHmPixelWidth * pixelScale;
            var bitmapWorldH = worldHmPixelHeight * pixelScale;
            var bitmapX = worldHmMinX * CellWorldSize;
            var bitmapY = -(worldHmMaxY + 1) * CellWorldSize;

            ds.DrawImage(worldHeightmapBitmap,
                new Rect(bitmapX, bitmapY, bitmapWorldW, bitmapWorldH));
        }

        // 2. Cell grid
        DrawCellGrid(ds, activeCells, cellGridLookup, worldHeightmapBitmap,
            zoom, panOffset, canvasWidth, canvasHeight);

        // 3. Placed objects (LOD-based)
        if (zoom > 0.05f && activeCells.Count > 0)
        {
            var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
                canvasWidth, canvasHeight, zoom, panOffset);

            foreach (var cell in activeCells)
            {
                if (!cell.HasPersistentObjects &&
                    !WorldMapViewportHelper.IsCellVisible(cell, tlWorld, brWorld))
                {
                    continue;
                }

                foreach (var obj in cell.PlacedObjects)
                {
                    if (hiddenCategories.Contains(GetObjectCategory(obj, data)))
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
        }

        // 4. Map markers (always visible)
        DrawMapMarkers(ds, filteredMarkers, markerIconBitmaps, hiddenCategories,
            zoom, panOffset, canvasWidth, canvasHeight, colorScheme);

        // 4b. NPC/Creature dots (always visible)
        DrawActorDots(ds, data, activeCells, hiddenCategories, hideDisabledActors,
            zoom, panOffset, canvasWidth, canvasHeight);

        // 4c. Save overlay markers (save file positions)
        DrawSaveOverlay(ds, data, zoom, panOffset, canvasWidth, canvasHeight);

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

    internal static void DrawCellGrid(
        CanvasDrawingSession ds,
        List<CellRecord> activeCells,
        Dictionary<(int x, int y), CellRecord>? cellGridLookup,
        CanvasBitmap? worldHeightmapBitmap,
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
        if (worldHeightmapBitmap == null && cellGridLookup is { Count: > 0 })
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

        // Vertical lines
        for (var cx = startCellX; cx <= endCellX; cx++)
        {
            var worldX = cx * CellWorldSize;
            ds.DrawLine(worldX, startCellY * CellWorldSize, worldX, endCellY * CellWorldSize, gridColor, lineWidth);
        }

        // Horizontal lines
        for (var cy = startCellY; cy <= endCellY; cy++)
        {
            var worldY = cy * CellWorldSize;
            ds.DrawLine(startCellX * CellWorldSize, worldY, endCellX * CellWorldSize, worldY, gridColor, lineWidth);
        }

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
                WorldMapDrawingHelper.DrawTintedIcon(ds, icon, destRect, tint);
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

            if (zoom > 0.05f && !string.IsNullOrEmpty(marker.MarkerName))
            {
                var labelPos = new Vector2(pos.X + markerSize / 2 + 2f / zoom, pos.Y - markerSize / 4);
                ds.DrawText(marker.MarkerName, labelPos, tint, labelFormat);
            }
        }
    }

    internal static void DrawActorDots(
        CanvasDrawingSession ds,
        WorldViewData _data,
        List<CellRecord> activeCells,
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
                    continue;
                }

                var pos = new Vector2(obj.X, -obj.Y);
                if (!WorldMapViewportHelper.IsPointInView(pos.X, pos.Y, tlWorld, brWorld, dotRadius * 2))
                {
                    continue;
                }

                var fillAlpha = obj.IsInitiallyDisabled ? (byte)60 : (byte)180;
                var outlineAlpha = obj.IsInitiallyDisabled ? (byte)80 : (byte)255;
                ds.FillCircle(pos, dotRadius, WorldMapColors.WithAlpha(color, fillAlpha));
                ds.DrawCircle(pos, dotRadius, WorldMapColors.WithAlpha(Colors.White, outlineAlpha), outlineWidth);
            }
        }
    }

    internal static void DrawSaveOverlay(
        CanvasDrawingSession ds,
        WorldViewData data,
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

        foreach (var obj in data.SaveOverlayMarkers)
        {
            var pos = new Vector2(obj.X, -obj.Y);
            if (!WorldMapViewportHelper.IsPointInView(pos.X, pos.Y, tlWorld, brWorld, dotRadius * 2))
            {
                continue;
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

            // Try sprite rendering first (if registry is loaded and sprite exists)
            var sprite = data.SpriteRegistry?.GetSprite(obj.ModelPath);
            if (sprite != null)
            {
                DrawSpriteAtObject(ds, pos, halfW, halfH, obj.RotZ, sprite);
                return;
            }

            // Use reddish outline for clamped bounds to signal truncation
            var outlineColor = wasClamped ? Color.FromArgb(180, 255, 100, 100) : color;

            if (outlineOnly)
            {
                var rotation = Matrix3x2.CreateRotation(-obj.RotZ, pos);
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

    /// <summary>
    ///     Draw a pre-rendered sprite at an object's position, sized to match its bounding box
    ///     and rotated by the object's Z-axis rotation.
    /// </summary>
    private static void DrawSpriteAtObject(CanvasDrawingSession ds, Vector2 pos,
        float halfW, float halfH, float rotZ, WorldMapSpriteRegistry.SpriteEntry sprite)
    {
        var saved = ds.Transform;

        // Apply rotation around object center
        if (MathF.Abs(rotZ) > 0.001f)
        {
            ds.Transform = Matrix3x2.CreateRotation(-rotZ, pos) * saved;
        }

        // Draw sprite sized to match bounding box
        var destRect = new Rect(pos.X - halfW, pos.Y - halfH, halfW * 2, halfH * 2);
        ds.DrawImage(sprite.Bitmap, destRect);

        ds.Transform = saved;
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
}
