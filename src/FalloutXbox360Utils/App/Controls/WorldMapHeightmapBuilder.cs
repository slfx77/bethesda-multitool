using FalloutXbox360Utils.Core.Formats.Esm.Models;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;

namespace FalloutXbox360Utils;

/// <summary>
///     Builds world-level heightmap CanvasBitmaps from cached or computed data.
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
        HeightmapColorScheme colorScheme, bool showWater)
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

        var result = HeightmapRenderer.ComputeHeightmapData(activeCells, currentDefaultWaterHeight);
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
