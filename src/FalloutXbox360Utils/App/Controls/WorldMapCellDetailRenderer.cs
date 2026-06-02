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
                        currentDefaultWaterHeight, showWater, cache),
                WorldMapLayer.Slope =>
                    WorldMapLayerRenderer.RenderSlopeForCell(cell, currentDefaultWaterHeight, showWater, cache),
                _ => null
            };
            if (layerPixels == null) return null;
            // Match the heightmap path's alpha so the cell grid border remains visible.
            for (var i = 3; i < layerPixels.Length; i += 4) layerPixels[i] = 200;
            // Terrain Textures renders at a higher pixel density than the other cell layers
            // (HmGridSize × TextureLayerScale per axis) so the BTXT tiling reads sharply.
            var dim = layer == WorldMapLayer.TerrainTextures
                ? WorldMapLayerRenderer.TexturePixelsPerCell
                : HmGridSize;
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
}
