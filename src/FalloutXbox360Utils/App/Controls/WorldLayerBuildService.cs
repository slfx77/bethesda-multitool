using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils;

internal static class WorldLayerBuildService
{
    private const int MaxSingleBitmapDimension = 8192;

    internal static Task<LayerBuildResult> BuildAsync(LayerBuildRequest request)
    {
        return Task.Run(async () =>
        {
            if (request.Layer == WorldMapLayer.TerrainTextures && request.Palette is not null)
            {
                await request.Palette.PreloadAsync(request.ActiveCells).ConfigureAwait(false);
                var cells = WorldMapLayerRenderer.RenderTerrainTexturesPerCell(
                    request.ActiveCells,
                    request.Palette,
                    request.DefaultWaterHeight,
                    request.ShowWater,
                    request.Cache);
                return new LayerBuildResult(
                    request.Version,
                    null,
                    cells,
                    WorldMapLayerRenderer.TexturePixelsPerCell,
                    cells is null ? "Terrain textures produced no renderable cells." : null);
            }

            if (ShouldUsePerCellOverview(request))
            {
                var cells = request.Layer switch
                {
                    WorldMapLayer.VertexColors => WorldMapLayerRenderer.RenderVertexColorsPerCell(
                        request.ActiveCells, request.DefaultWaterHeight, request.ShowWater, request.Cache),
                    WorldMapLayer.TerrainRegions => WorldMapLayerRenderer.RenderTerrainRegionsPerCell(
                        request.ActiveCells, request.DefaultWaterHeight, request.ShowWater, request.Cache),
                    WorldMapLayer.TerrainTextures => WorldMapLayerRenderer.RenderTerrainRegionsPerCell(
                        request.ActiveCells, request.DefaultWaterHeight, request.ShowWater, request.Cache),
                    _ => null
                };
                return new LayerBuildResult(
                    request.Version,
                    null,
                    cells,
                    WorldMapLayerRenderer.HeightmapPixelsPerCell,
                    cells is null ? $"{request.Layer.DisplayName()} produced no renderable cells." : null);
            }

            var layer = request.Layer switch
            {
                WorldMapLayer.Heightmap => BuildHeightmap(request),
                WorldMapLayer.VertexColors => WorldMapLayerRenderer.RenderVertexColors(
                    request.ActiveCells, request.DefaultWaterHeight, request.ShowWater, request.Cache),
                WorldMapLayer.TerrainRegions => WorldMapLayerRenderer.RenderTerrainRegions(
                    request.ActiveCells, request.DefaultWaterHeight, request.ShowWater, request.Cache),
                WorldMapLayer.TerrainTextures => WorldMapLayerRenderer.RenderTerrainTexturesRegionsFallback(
                    request.ActiveCells, request.DefaultWaterHeight, request.ShowWater, request.Cache),
                WorldMapLayer.Slope => WorldMapLayerRenderer.RenderSlope(
                    request.ActiveCells, request.DefaultWaterHeight, request.ShowWater, request.Cache),
                _ => null
            };

            return new LayerBuildResult(
                request.Version,
                layer,
                null,
                0,
                layer is null ? $"{request.Layer.DisplayName()} produced no renderable data." : null);
        });
    }

    private static bool ShouldUsePerCellOverview(LayerBuildRequest request)
    {
        if (request.Layer is not (WorldMapLayer.VertexColors or WorldMapLayer.TerrainRegions or WorldMapLayer.TerrainTextures))
        {
            return false;
        }

        var hasGrid = false;
        var minX = int.MaxValue;
        var maxX = int.MinValue;
        var minY = int.MaxValue;
        var maxY = int.MinValue;
        foreach (var cell in request.ActiveCells)
        {
            if (cell.GridX is not int gx || cell.GridY is not int gy)
            {
                continue;
            }

            hasGrid = true;
            minX = Math.Min(minX, gx);
            maxX = Math.Max(maxX, gx);
            minY = Math.Min(minY, gy);
            maxY = Math.Max(maxY, gy);
        }

        if (!hasGrid)
        {
            return false;
        }

        var pixelsPerCell = WorldMapLayerRenderer.HeightmapPixelsPerCell;
        var width = (maxX - minX + 1) * pixelsPerCell;
        var height = (maxY - minY + 1) * pixelsPerCell;
        return width > MaxSingleBitmapDimension || height > MaxSingleBitmapDimension;
    }

    private static WorldMapLayerRenderer.LayerBitmap? BuildHeightmap(LayerBuildRequest request)
    {
        if (request.CachedGrayscale is not null &&
            request.CachedWidth > 0 &&
            request.CachedHeight > 0 &&
            request.SelectedWorldspace is not null &&
            request.Data.Worldspaces.Count > 0 &&
            ReferenceEquals(request.SelectedWorldspace, request.Data.Worldspaces[0]))
        {
            var waterMask = request.CachedWaterMask ?? new byte[request.CachedWidth * request.CachedHeight];
            var pixels = HeightmapRenderer.ApplyTintAndWater(
                request.CachedGrayscale,
                waterMask,
                request.CachedWidth,
                request.CachedHeight,
                request.ColorScheme,
                request.ShowWater);
            return new WorldMapLayerRenderer.LayerBitmap(
                pixels,
                request.CachedWidth,
                request.CachedHeight,
                request.Data.HeightmapMinCellX,
                request.Data.HeightmapMaxCellY);
        }

        var computed = HeightmapRenderer.ComputeHeightmapData(
            request.ActiveCells,
            request.DefaultWaterHeight,
            request.Cache);
        if (computed is null)
        {
            return null;
        }

        var (grayscale, waterMaskComputed, width, height, minX, maxY) = computed.Value;
        var tinted = HeightmapRenderer.ApplyTintAndWater(
            grayscale,
            waterMaskComputed,
            width,
            height,
            request.ColorScheme,
            request.ShowWater);
        return new WorldMapLayerRenderer.LayerBitmap(tinted, width, height, minX, maxY);
    }
}

internal sealed record LayerBuildRequest(
    int Version,
    List<CellRecord> ActiveCells,
    WorldspaceRecord? SelectedWorldspace,
    WorldViewData Data,
    float? DefaultWaterHeight,
    HeightmapColorScheme ColorScheme,
    bool ShowWater,
    WorldMapLayer Layer,
    byte[]? CachedGrayscale,
    byte[]? CachedWaterMask,
    int CachedWidth,
    int CachedHeight,
    WorldRenderCache Cache,
    LandscapeTexturePalette? Palette);

internal sealed record LayerBuildResult(
    int Version,
    WorldMapLayerRenderer.LayerBitmap? Bitmap,
    Dictionary<(int gx, int gy), byte[]>? CellPixels,
    int CellPixelsPerCell,
    string? Message);
