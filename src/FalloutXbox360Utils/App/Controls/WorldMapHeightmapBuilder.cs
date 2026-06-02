using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds world-level layer CanvasBitmaps from cached or computed data.
///     Dispatches on <see cref="WorldMapLayer" />.
/// </summary>
internal static class WorldMapHeightmapBuilder
{
    internal static HeightmapInfo? Build(
        CanvasControl canvas,
        List<CellRecord> activeCells,
        byte[]? cachedGrayscale, byte[]? cachedWaterMask,
        int cachedHmWidth, int cachedHmHeight,
        WorldspaceRecord? selectedWorldspace,
        WorldViewData? data,
        float? currentDefaultWaterHeight,
        HeightmapColorScheme colorScheme, bool showWater,
        WorldMapLayer layer = WorldMapLayer.Heightmap,
        WorldRenderCache? cache = null)
    {
        return layer switch
        {
            WorldMapLayer.Heightmap => BuildHeightmap(
                canvas, activeCells, cachedGrayscale, cachedWaterMask,
                cachedHmWidth, cachedHmHeight, selectedWorldspace, data,
                currentDefaultWaterHeight, colorScheme, showWater, cache),
            WorldMapLayer.VertexColors => Wrap(canvas,
                WorldMapLayerRenderer.RenderVertexColors(activeCells, currentDefaultWaterHeight, showWater, cache)),
            WorldMapLayer.TerrainRegions => Wrap(canvas,
                WorldMapLayerRenderer.RenderTerrainRegions(activeCells, currentDefaultWaterHeight, showWater, cache)),
            // The TerrainTextures layer is rendered per-cell at the call site (see
            // WorldMapControl.EnsureHeightmapBitmap); this branch is only reached when no
            // Textures BSA is available and we fall back to the regions view.
            WorldMapLayer.TerrainTextures => Wrap(canvas,
                WorldMapLayerRenderer.RenderTerrainTexturesRegionsFallback(
                    activeCells, currentDefaultWaterHeight, showWater, cache)),
            WorldMapLayer.Slope => Wrap(canvas,
                WorldMapLayerRenderer.RenderSlope(activeCells, currentDefaultWaterHeight, showWater, cache)),
            _ => null
        };
    }

    private static HeightmapInfo? BuildHeightmap(
        CanvasControl canvas,
        List<CellRecord> activeCells,
        byte[]? cachedGrayscale, byte[]? cachedWaterMask,
        int cachedHmWidth, int cachedHmHeight,
        WorldspaceRecord? selectedWorldspace,
        WorldViewData? data,
        float? currentDefaultWaterHeight,
        HeightmapColorScheme colorScheme, bool showWater,
        WorldRenderCache? cache)
    {
        // Use pre-computed grayscale/waterMask from background thread when available
        if (cachedGrayscale != null && cachedHmWidth > 0 &&
            selectedWorldspace != null && data?.Worldspaces.Count > 0 &&
            ReferenceEquals(selectedWorldspace, data.Worldspaces[0]))
        {
            var pixels = HeightmapRenderer.ApplyTintAndWater(
                cachedGrayscale, cachedWaterMask!, cachedHmWidth, cachedHmHeight,
                colorScheme, showWater);
            var bitmap = CanvasBitmap.CreateFromBytes(
                canvas, pixels, cachedHmWidth, cachedHmHeight,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            return new HeightmapInfo(bitmap,
                data.HeightmapMinCellX, data.HeightmapMaxCellY,
                cachedHmWidth, cachedHmHeight);
        }

        // Fallback: compute on-the-fly for non-default worldspaces or unlinked cells
        if (activeCells.Count == 0)
        {
            return null;
        }

        var result = HeightmapRenderer.ComputeHeightmapData(activeCells, currentDefaultWaterHeight, cache);
        if (result == null)
        {
            return null;
        }

        var (grayscale, waterMask, imgW, imgH, minX, maxY) = result.Value;
        var tintedPixels = HeightmapRenderer.ApplyTintAndWater(grayscale, waterMask, imgW, imgH,
            colorScheme, showWater);
        var bmp = CanvasBitmap.CreateFromBytes(
            canvas, tintedPixels, imgW, imgH,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
        return new HeightmapInfo(bmp, minX, maxY, imgW, imgH);
    }

    private static HeightmapInfo? Wrap(CanvasControl canvas, WorldMapLayerRenderer.LayerBitmap? layer)
    {
        if (layer is not { } b) return null;
        var bitmap = CanvasBitmap.CreateFromBytes(
            canvas, b.Pixels, b.Width, b.Height,
            Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
        return new HeightmapInfo(bitmap, b.MinCellX, b.MaxCellY, b.Width, b.Height);
    }

    /// <summary>
    ///     Builds the per-cell <see cref="CanvasBitmap" /> dictionary for the TerrainTextures
    ///     layer. Each entry is a TexturePixelsPerCell × TexturePixelsPerCell GPU bitmap
    ///     keyed by (cellGridX, cellGridY). Returns null when no cells produced any pixels
    ///     (e.g. an unlinked-cells worldspace with no LAND coverage).
    /// </summary>
    internal static Dictionary<(int gx, int gy), CanvasBitmap>? BuildTerrainTextureCells(
        CanvasControl canvas,
        List<CellRecord> activeCells,
        LandscapeTexturePalette palette,
        float? currentDefaultWaterHeight,
        bool showWater,
        WorldRenderCache? cache = null)
    {
        var perCell = WorldMapLayerRenderer.RenderTerrainTexturesPerCell(
            activeCells, palette, currentDefaultWaterHeight, showWater, cache);
        if (perCell is null) return null;

        var result = new Dictionary<(int gx, int gy), CanvasBitmap>(perCell.Count);
        foreach (var (key, pixels) in perCell)
        {
            var bmp = CanvasBitmap.CreateFromBytes(
                canvas, pixels,
                WorldMapLayerRenderer.TexturePixelsPerCell,
                WorldMapLayerRenderer.TexturePixelsPerCell,
                Windows.Graphics.DirectX.DirectXPixelFormat.R8G8B8A8UIntNormalized);
            result[key] = bmp;
        }
        return result;
    }

    internal readonly struct HeightmapInfo(
        CanvasBitmap bitmap,
        int minX,
        int maxY,
        int pixelWidth,
        int pixelHeight)
    {
        internal CanvasBitmap Bitmap { get; } = bitmap;
        internal int MinX { get; } = minX;
        internal int MaxY { get; } = maxY;
        internal int PixelWidth { get; } = pixelWidth;
        internal int PixelHeight { get; } = pixelHeight;
    }
}
