using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.Foundation;
using Windows.UI;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

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
        PlacedReference? hoveredObject,
        bool showRenderedObjects = false,
        CanvasBitmap? renderedObjectsOverlay = null,
        float overlayWorldMinX = 0f, float overlayWorldMaxX = 0f,
        float overlayWorldMinY = 0f, float overlayWorldMaxY = 0f,
        CanvasBitmap? cellWaterBitmap = null,
        bool showWater = true)
    {
        ds.Transform = WorldMapViewportHelper.GetViewTransform(zoom, panOffset);

        var overlayActive = showRenderedObjects && renderedObjectsOverlay is not null;

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

        // 2a. Water layer (flat) — over the water-free cell bitmap; suppressed when the overlay is active
        //     (it supplies height-correct water). Opacity matches the cell bitmap's 200 alpha so water and
        //     land read equally translucent, reproducing the old baked look. Same gate as overview step 2c.
        if (showWater && !overlayActive && cellWaterBitmap != null
            && selectedCell.GridX.HasValue && selectedCell.GridY.HasValue)
        {
            var wOriginX = selectedCell.GridX.Value * CellWorldSize;
            var wOriginY = -(selectedCell.GridY.Value + 1) * CellWorldSize;
            var src = cellWaterBitmap.SizeInPixels;
            ds.DrawImage(cellWaterBitmap,
                new Rect(wOriginX, wOriginY, CellWorldSize, CellWorldSize),
                new Rect(0, 0, src.Width, src.Height),
                200f / 255f);
        }

        // 2b. Rendered-models overlay (exterior cells only — the caller passes null for interiors).
        if (overlayActive)
        {
            WorldMapOverviewRenderer.DrawRenderedObjectsOverlay(ds, renderedObjectsOverlay!,
                overlayWorldMinX, overlayWorldMaxX, overlayWorldMinY, overlayWorldMaxY);
        }

        // 3. Placed objects — skipped when the rendered-models overlay replaces them.
        var (tlWorld, brWorld) = WorldMapViewportHelper.GetVisibleWorldBounds(
            canvasWidth, canvasHeight, zoom, panOffset);
        if (!overlayActive)
        {
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
        HeightmapColorScheme colorScheme, bool showWater,
        WorldMapLayer layer = WorldMapLayer.Heightmap,
        WorldViewData? data = null,
        WorldRenderCache? cache = null)
    {
        if (layer != WorldMapLayer.Heightmap)
        {
            var layerPixels = layer switch
            {
                WorldMapLayer.VertexColors =>
                    WorldMapLayerRenderer.RenderVertexColorsForCell(cell, currentDefaultWaterHeight, showWater, cache),
                WorldMapLayer.TerrainRegions =>
                    WorldMapLayerRenderer.RenderTerrainRegionsForCell(cell, currentDefaultWaterHeight, showWater, cache),
                WorldMapLayer.TerrainTextures =>
                    WorldMapLayerRenderer.RenderTerrainTexturesForCell(cell,
                        data is null ? null : LandscapeTexturePalette.GetOrCreate(data),
                        currentDefaultWaterHeight, showWater, cache,
                        WorldMapLayerRenderer.MaxTexturePixelsPerCell),
                WorldMapLayer.Slope =>
                    WorldMapLayerRenderer.RenderSlopeForCell(cell, currentDefaultWaterHeight, showWater, cache),
                _ => null
            };
            if (layerPixels == null) return null;
            // Match the heightmap path's alpha so the cell grid border remains visible.
            for (var i = 3; i < layerPixels.Length; i += 4) layerPixels[i] = 200;
            var dim = (int)Math.Sqrt(layerPixels.Length / 4d);
            return CanvasBitmap.CreateFromBytes(
                canvas, layerPixels, dim, dim,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
        }

        var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
        if (!terrain.HasTerrain)
        {
            return null;
        }

        var minH = float.MaxValue;
        var maxH = float.MinValue;
        for (var y = 0; y < HmGridSize; y++)
        {
            for (var x = 0; x < HmGridSize; x++)
            {
                var h = terrain.HeightAt(x, y);
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
        }

        var range = maxH - minH;
        if (range < 0.001f)
        {
            range = 1f;
        }

        // Determine effective water height. Explicit "no water" sentinel on the cell
        // suppresses water entirely; null (no XCLW) falls back to worldspace DNAM.
        var waterH = WorldRenderCache.ResolveEffectiveWaterHeight(cell, currentDefaultWaterHeight);

        var grayscale = new byte[HmGridSize * HmGridSize];
        var waterMask = new byte[HmGridSize * HmGridSize];

        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var height = terrain.HeightAt(px, HmGridSize - 1 - py);
                var normalized = (height - minH) / range;
                var idx = py * HmGridSize + px;
                grayscale[idx] = (byte)(Math.Clamp(normalized, 0f, 1f) * 255);
            }
        }

        if (terrain.GetLowResWaterMask(waterH) is { } cachedWaterMask)
        {
            Array.Copy(cachedWaterMask, waterMask, waterMask.Length);
        }

        var pixels = HeightmapRenderer.ApplyTintAndWater(grayscale, waterMask, HmGridSize, HmGridSize,
            colorScheme, showWater, alpha: 200);

        return CanvasBitmap.CreateFromBytes(
            canvas, pixels, HmGridSize, HmGridSize,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
    }

    /// <summary>
    ///     Builds the single-cell standalone water bitmap (premult RGBA, transparent where dry) that
    ///     <see cref="BuildCellHeightmapBitmap" /> (rendered water-free) is paired with. Reuses the same
    ///     <see cref="WorldMapLayerRenderer.BuildCellWaterTile" /> as the overview per-cell path, at the
    ///     cell's render resolution so it aligns with the cell bitmap. Solid-blue (no palette), matching
    ///     the cell-detail's prior baked water. Returns null for a dry cell.
    /// </summary>
    internal static CanvasBitmap? BuildCellWaterBitmap(
        CanvasControl canvas, CellRecord cell, float? currentDefaultWaterHeight,
        WorldMapLayer layer = WorldMapLayer.Heightmap, WorldRenderCache? cache = null)
    {
        var ppc = layer == WorldMapLayer.TerrainTextures
            ? WorldMapLayerRenderer.MaxTexturePixelsPerCell
            : HmGridSize;
        var tile = WorldMapLayerRenderer.BuildCellWaterTile(
            cell, currentDefaultWaterHeight, ppc, cache, cellByGrid: null, waterPalette: null);
        if (tile is null) return null;
        return CanvasBitmap.CreateFromBytes(
            canvas, tile, ppc, ppc,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
    }
}
