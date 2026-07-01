using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool;

/// <summary>Builds the 2D world map's per-layer bitmap (heightmap, vertex colors, terrain, slope) off the UI thread.</summary>
internal static class WorldLayerBuildService
{
    private const int MaxSingleBitmapDimension = 8192;

    /// <summary>Renders the requested map layer to a bitmap (or coarse tile set) on a background thread.</summary>
    internal static Task<LayerBuildResult> BuildAsync(LayerBuildRequest request)
    {
        return Task.Run(async () =>
        {
            if (request.Layer == WorldMapLayer.TerrainTextures && request.Palette is not null)
            {
                // Zoomed-out LOD: one downscaled whole-worldspace texture bitmap instead of streaming
                // thousands of per-cell tiles. Falls through to per-cell when the worldspace is too
                // large to fit a single bitmap.
                if (request.PreferAggregate)
                {
                    await request.Palette.PreloadAsync(request.ActiveCells).ConfigureAwait(false);
                    var aggregate = WorldMapLayerRenderer.RenderTerrainTexturesAggregate(
                        request.ActiveCells,
                        request.Palette,
                        request.DefaultWaterHeight,
                        request.ShowWater,
                        out var aggregatePpc,
                        request.Cache,
                        request.WaterPalette,
                        shading: request.TerrainShading);
                    // Empty result (no bitmap, no cells) signals "too large for aggregate" so the
                    // caller falls back to per-cell streaming instead of paying a full synchronous
                    // per-cell render here. CellPixelsPerCell carries the aggregate's px/cell so the
                    // composite can scale it correctly (it's < 33 for large worldspaces).
                    return new LayerBuildResult(
                        request.Version, aggregate, null, aggregatePpc,
                        aggregate is null ? "Worldspace too large for a single terrain overview bitmap." : null);
                }

                var pixelsPerCell = WorldMapLayerRenderer.NormalizeTexturePixelsPerCell(request.TexturePixelsPerCell);
                await request.Palette.PreloadAsync(request.ActiveCells).ConfigureAwait(false);
                var cells = WorldMapLayerRenderer.RenderTerrainTexturesPerCell(
                    request.ActiveCells,
                    request.Palette,
                    request.DefaultWaterHeight,
                    request.ShowWater,
                    request.Cache,
                    pixelsPerCell,
                    shading: request.TerrainShading);
                return new LayerBuildResult(
                    request.Version,
                    null,
                    cells,
                    pixelsPerCell,
                    cells is null ? "Terrain textures produced no renderable cells." : null);
            }

            if (ShouldUseCoarseTileOverview(request))
            {
                // Oversized worldspace: render the terrain-derived layer as a small set of COARSE
                // multi-cell tiles (bounded count + memory) instead of one giant bitmap (the FO76
                // APPALACHIA freeze) or thousands of per-cell tiles. The coarse renderer adapts its
                // px/cell to the worldspace size and reuses the single-cell renderers internally.
                var tiles = WorldMapLayerRenderer.RenderLayerCoarseTiles(
                    request.Layer, request.ActiveCells, request.DefaultWaterHeight, request.ShowWater,
                    request.ColorScheme, request.Cache, request.HillshadeLightDir, request.HillshadeZScale);
                return new LayerBuildResult(
                    request.Version,
                    null,
                    null,
                    0,
                    tiles is null ? $"{request.Layer.DisplayName()} produced no renderable cells." : null,
                    tiles?.Tiles,
                    tiles?.TileCellSpan ?? 0,
                    tiles?.PixelsPerCell ?? 0);
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
                    request.ActiveCells, request.DefaultWaterHeight, request.ShowWater, request.Cache,
                    request.HillshadeLightDir, request.HillshadeZScale),
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

    private static bool ShouldUseCoarseTileOverview(LayerBuildRequest request)
    {
        // Above the single-bitmap size ceiling a huge worldspace (FO76 APPALACHIA) would otherwise build
        // one multi-GB bitmap and freeze. These terrain-derived layers instead render as COARSE
        // multi-cell tiles — a bounded set of mid-resolution bitmaps covering blocks of cells. (The
        // TerrainTextures *textured* path has its own aggregate + per-cell streaming above; this branch
        // only covers its no-palette regions fallback.)
        if (request.Layer is not (WorldMapLayer.Heightmap or WorldMapLayer.VertexColors
            or WorldMapLayer.TerrainRegions or WorldMapLayer.TerrainTextures or WorldMapLayer.Slope))
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

/// <summary>Inputs for a world map layer build: which cells/worldspace/layer to render plus caches and palettes.</summary>
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
    LandscapeTexturePalette? Palette,
    int TexturePixelsPerCell = WorldMapLayerRenderer.TexturePixelsPerCell,
    bool PreferAggregate = false,
    WaterColorPalette? WaterPalette = null,
    TerrainShadingOptions TerrainShading = default,
    Vector3? HillshadeLightDir = null,
    float HillshadeZScale = WorldMapHillshadeRenderer.DefaultZScale);

/// <summary>Output of a world map layer build: the composed bitmap and/or per-cell and coarse-tile pixel data.</summary>
internal sealed record LayerBuildResult(
    int Version,
    WorldMapLayerRenderer.LayerBitmap? Bitmap,
    Dictionary<(int gx, int gy), byte[]>? CellPixels,
    int CellPixelsPerCell,
    string? Message,
    // Coarse multi-cell tiles (oversized-worldspace overview). Each tile covers CoarseTileCellSpan²
    // cells at CoarseTilePixelsPerCell px/cell; positioned/sized off the cell span by the draw path.
    Dictionary<(int tileGx, int tileGy), byte[]>? CoarseTiles = null,
    int CoarseTileCellSpan = 0,
    int CoarseTilePixelsPerCell = 0);
