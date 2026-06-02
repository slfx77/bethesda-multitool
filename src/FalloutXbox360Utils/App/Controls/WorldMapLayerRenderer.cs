using System.Numerics;
using FalloutXbox360Utils.Core.Formats.Esm.Models.Records.World;
using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils;

/// <summary>
///     Produces RGBA pixel buffers for non-heightmap world map layers: VCLR vertex colors,
///     dominant BTXT base textures per quadrant, and hillshade-from-heightmap slope.
///     Output shape matches <see cref="HeightmapRenderer" /> so the caller can wrap the
///     bytes in a CanvasBitmap and reuse the existing positioning math.
/// </summary>
internal static class WorldMapLayerRenderer
{
    private const int HmGridSize = 33;
    internal const int HeightmapPixelsPerCell = HmGridSize;

    /// <summary>Water tint for the underwater overlay, matches HeightmapRenderer.</summary>
    private const byte WaterR = 30, WaterG = 55, WaterB = 120;

    /// <summary>Cells lacking the layer's source data render in this neutral gray.</summary>
    private const byte MissingR = 40, MissingG = 40, MissingB = 45;

    /// <summary>
    ///     Last-ditch terrain colour for the Terrain Textures layer when even the engine-default
    ///     DirtWasteland01 texture can't be loaded (no Textures BSA next to the ESM). Tuned to
    ///     roughly match the averaged DirtWasteland01 diffuse so the fallback transition isn't
    ///     jarring. With a normal install the engine-default sample is used instead.
    /// </summary>
    private const byte DefaultTerrainR = 145, DefaultTerrainG = 122, DefaultTerrainB = 90;

    internal readonly record struct LayerBitmap(
        byte[] Pixels,
        int Width,
        int Height,
        int MinCellX,
        int MaxCellY);

    // ========================================================================
    // Worldspace overview renderers
    // ========================================================================

    internal static LayerBitmap? RenderVertexColors(
        List<CellRecord> cellSource, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var hm = HeightmapRenderer.ComputeHeightmapData(cellSource, defaultWaterHeight, cache);
        if (hm == null) return null;
        var (_, waterMask, width, height, minX, maxY) = hm.Value;

        var rgba = InitMissingBackground(width, height);

        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var vc = cell.LandVisualData?.VertexColors;
            if (vc is not { Length: HmGridSize * HmGridSize * 3 }) continue;

            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;
            BlitVertexColorsToCell(rgba, width, vc, imgCellX, imgCellY);
        }

        if (showWater) OverlayWater(rgba, waterMask, width, height);
        return new LayerBitmap(rgba, width, height, minX, maxY);
    }

    internal static LayerBitmap? RenderTerrainRegions(
        List<CellRecord> cellSource, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var hm = HeightmapRenderer.ComputeHeightmapData(cellSource, defaultWaterHeight, cache);
        if (hm == null) return null;
        var (_, waterMask, width, height, minX, maxY) = hm.Value;

        var rgba = InitMissingBackground(width, height);

        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var winners = cache?.GetTextureWinners(cell) ??
                          (cell.LandVisualData?.TextureLayers is { Count: > 0 } layers
                              ? TextureWinnerGrid.Build(layers)
                              : null);
            if (winners == null) continue;

            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;
            BlitTerrainRegionsToCell(rgba, width, winners, imgCellX, imgCellY);
        }

        if (showWater) OverlayWater(rgba, waterMask, width, height);
        return new LayerBitmap(rgba, width, height, minX, maxY);
    }

    /// <summary>
    ///     Texture-layer pixel density multiplier over the heightmap's HmGridSize. The terrain
    ///     textures layer is rendered at 4× the heightmap resolution so the BTXT tiling reads
    ///     sharply when the user zooms in. Memory cost scales 16× for this layer (typical
    ///     WastelandNV: 1.2 MB → 20 MB), still well within budget.
    /// </summary>
    private const int TextureLayerScale = 4;

    /// <summary>Per-cell-axis pixel count used by the terrain textures layer (132 in vanilla FNV).</summary>
    internal const int TexturePixelsPerCell = HmGridSize * TextureLayerScale;

    /// <summary>
    ///     Renders the terrain-textures layer as one RGBA bitmap per cell. Composed at draw
    ///     time by the caller. This per-cell architecture avoids the giant-bitmap path's
    ///     GPU max-texture-size cliff on large worldspaces (WastelandNV is 128 cells wide;
    ///     a single bitmap at TexturePixelsPerCell=132 exceeds the typical 16384 px GPU limit).
    ///     Returns null when no cells produced any pixels.
    /// </summary>
    internal static Dictionary<(int gx, int gy), byte[]>? RenderTerrainTexturesPerCell(
        List<CellRecord> cellSource, LandscapeTexturePalette palette,
        float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        palette.Preload(cellSource);
        var result = new Dictionary<(int gx, int gy), byte[]>();
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var bytes = RenderTerrainTextureCellOverview(cell, palette, defaultWaterHeight, showWater, cache);
            if (bytes is null) continue;
            result[(cell.GridX!.Value, cell.GridY!.Value)] = bytes;
        }
        return result.Count == 0 ? null : result;
    }

    internal static Dictionary<(int gx, int gy), byte[]>? RenderVertexColorsPerCell(
        List<CellRecord> cellSource, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var result = new Dictionary<(int gx, int gy), byte[]>();
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var bytes = RenderVertexColorsForCell(cell, defaultWaterHeight, showWater, cache)
                        ?? RenderMissingCell(cell, defaultWaterHeight, showWater, cache);
            result[(cell.GridX!.Value, cell.GridY!.Value)] = bytes;
        }

        return result.Count == 0 ? null : result;
    }

    internal static Dictionary<(int gx, int gy), byte[]>? RenderTerrainRegionsPerCell(
        List<CellRecord> cellSource, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var result = new Dictionary<(int gx, int gy), byte[]>();
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var bytes = RenderTerrainRegionsForCell(cell, defaultWaterHeight, showWater, cache)
                        ?? RenderMissingCell(cell, defaultWaterHeight, showWater, cache);
            result[(cell.GridX!.Value, cell.GridY!.Value)] = bytes;
        }

        return result.Count == 0 ? null : result;
    }

    /// <summary>
    ///     Renders the terrain-textures layer for a single cell. Output is
    ///     <see cref="TexturePixelsPerCell" /> × <see cref="TexturePixelsPerCell" /> RGBA.
    ///     Cells without LAND texture layers still get rendered as engine-default terrain
    ///     so the user can see the cell exists.
    /// </summary>
    private static byte[]? RenderTerrainTextureCellOverview(
        CellRecord cell, LandscapeTexturePalette palette,
        float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue) return null;

        var layers = cell.LandVisualData?.TextureLayers;
        var stack = layers is { Count: > 0 } ? BuildTextureBlendStack(layers) : null;

        var rgba = new byte[TexturePixelsPerCell * TexturePixelsPerCell * 4];
        BlitTerrainTexturesBlended(rgba, TexturePixelsPerCell, TexturePixelsPerCell, stack, palette,
            cell.GridX.Value, cell.GridY.Value, imgCellX: 0, imgCellY: 0);
        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, TexturePixelsPerCell, cache);
        return rgba;
    }

    /// <summary>
    ///     Regions-only fallback used when no Textures BSA is available next to the ESM. Goes
    ///     through the single-bitmap path so the caller doesn't need to special-case the
    ///     no-palette scenario.
    /// </summary>
    internal static LayerBitmap? RenderTerrainTexturesRegionsFallback(
        List<CellRecord> cellSource, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
        => RenderTerrainRegions(cellSource, defaultWaterHeight, showWater, cache);

    /// <summary>
    ///     Nearest-neighbor upscale of a single-channel byte mask. Used to lift the
    ///     <see cref="HmGridSize" />-per-cell waterMask up to the texture layer's
    ///     <see cref="TexturePixelsPerCell" /> resolution. Since waterMask is sparse and
    ///     largely per-cell anyway, nearest-neighbor is visually indistinguishable from
    ///     recomputing the mask at high res.
    /// </summary>
    private static byte[] UpscaleMaskNearest(byte[] src, int srcW, int srcH, int scale)
    {
        var dstW = srcW * scale;
        var dstH = srcH * scale;
        var dst = new byte[dstW * dstH];
        for (var dy = 0; dy < dstH; dy++)
        {
            var sy = dy / scale;
            for (var dx = 0; dx < dstW; dx++)
            {
                var sx = dx / scale;
                dst[dy * dstW + dx] = src[sy * srcW + sx];
            }
        }
        return dst;
    }

    internal static LayerBitmap? RenderSlope(
        List<CellRecord> cellSource, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var cells = new List<(CellRecord Cell, DecodedTerrainCell Terrain)>();
        foreach (var cell in cellSource)
        {
            if (!cell.GridX.HasValue || !cell.GridY.HasValue)
            {
                continue;
            }

            var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
            if (terrain.HasTerrain)
            {
                cells.Add((cell, terrain));
            }
        }
        if (cells.Count == 0) return null;

        var minX = cells.Min(c => c.Cell.GridX!.Value);
        var maxX = cells.Max(c => c.Cell.GridX!.Value);
        var minY = cells.Min(c => c.Cell.GridY!.Value);
        var maxY = cells.Max(c => c.Cell.GridY!.Value);
        var width = (maxX - minX + 1) * HmGridSize;
        var height = (maxY - minY + 1) * HmGridSize;

        var heightField = new float[width * height];
        var hasHeight = new bool[width * height];
        var waterMask = new byte[width * height];

        foreach (var (cell, terrain) in cells)
        {
            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;
            var waterH = ResolveWaterHeight(cell, defaultWaterHeight);

            for (var py = 0; py < HmGridSize; py++)
            {
                for (var px = 0; px < HmGridSize; px++)
                {
                    var h = terrain.HeightAt(px, HmGridSize - 1 - py);
                    var imgX = imgCellX * HmGridSize + px;
                    var imgY = imgCellY * HmGridSize + py;
                    var idx = imgY * width + imgX;
                    heightField[idx] = h;
                    hasHeight[idx] = true;

                    if (waterH.HasValue && waterH.Value is > -1e6f and < 1e6f && h < waterH.Value)
                    {
                        waterMask[idx] = 180;
                    }
                }
            }
        }

        HeightmapRenderer.BlurWaterMask(waterMask, width, height);

        var rgba = ComputeHillshade(heightField, hasHeight, width, height);
        if (showWater) OverlayWater(rgba, waterMask, width, height);
        return new LayerBitmap(rgba, width, height, minX, maxY);
    }

    // ========================================================================
    // Single-cell renderers (for cell detail view)
    // ========================================================================

    internal static byte[]? RenderVertexColorsForCell(
        CellRecord cell, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var vc = cell.LandVisualData?.VertexColors;
        if (vc is not { Length: HmGridSize * HmGridSize * 3 }) return null;

        var rgba = new byte[HmGridSize * HmGridSize * 4];
        BlitVertexColorsToCell(rgba, HmGridSize, vc, imgCellX: 0, imgCellY: 0);
        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
    }

    internal static byte[]? RenderTerrainRegionsForCell(
        CellRecord cell, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var winners = cache?.GetTextureWinners(cell) ??
                      (cell.LandVisualData?.TextureLayers is { Count: > 0 } layers
                          ? TextureWinnerGrid.Build(layers)
                          : null);
        if (winners == null) return null;

        var rgba = new byte[HmGridSize * HmGridSize * 4];
        BlitTerrainRegionsToCell(rgba, HmGridSize, winners, imgCellX: 0, imgCellY: 0);
        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
    }

    internal static byte[]? RenderTerrainTexturesForCell(
        CellRecord cell, LandscapeTexturePalette? palette,
        float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        if (palette is null)
        {
            return RenderTerrainRegionsForCell(cell, defaultWaterHeight, showWater, cache);
        }

        if (!cell.GridX.HasValue || !cell.GridY.HasValue) return null;

        var layers = cell.LandVisualData?.TextureLayers;
        var stack = layers is { Count: > 0 } ? BuildTextureBlendStack(layers) : null;

        palette.Preload([cell]);
        var rgba = new byte[TexturePixelsPerCell * TexturePixelsPerCell * 4];
        BlitTerrainTexturesBlended(rgba, TexturePixelsPerCell, TexturePixelsPerCell, stack, palette,
            cell.GridX.Value, cell.GridY.Value, imgCellX: 0, imgCellY: 0);
        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, TexturePixelsPerCell, cache);
        return rgba;
    }

    internal static byte[]? RenderSlopeForCell(
        CellRecord cell, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
        if (!terrain.HasTerrain) return null;

        var heightField = new float[HmGridSize * HmGridSize];
        var hasHeight = new bool[HmGridSize * HmGridSize];
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var idx = py * HmGridSize + px;
                heightField[idx] = terrain.HeightAt(px, HmGridSize - 1 - py);
                hasHeight[idx] = true;
            }
        }

        var rgba = ComputeHillshade(heightField, hasHeight, HmGridSize, HmGridSize);
        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
    }

    // ========================================================================
    // Per-pixel blitters
    // ========================================================================

    private static void BlitVertexColorsToCell(byte[] rgba, int stride, byte[] vc, int imgCellX, int imgCellY)
    {
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                // VCLR stored in LAND vertex order (north-up). Mirror HeightmapRenderer's
                // flip so vertex-color textures align with the heightmap layer.
                var srcRow = HmGridSize - 1 - py;
                var srcIdx = (srcRow * HmGridSize + px) * 3;

                var imgX = imgCellX * HmGridSize + px;
                var imgY = imgCellY * HmGridSize + py;
                var dst = (imgY * stride + imgX) * 4;

                rgba[dst] = vc[srcIdx];
                rgba[dst + 1] = vc[srcIdx + 1];
                rgba[dst + 2] = vc[srcIdx + 2];
                rgba[dst + 3] = 255;
            }
        }
    }

    private static void BlitTerrainRegionsToCell(byte[] rgba, int stride, TextureWinnerGrid winners, int imgCellX, int imgCellY)
    {
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var formId = winners.Lookup(px, py);
                var (r, g, b) = formId.HasValue
                    ? FormIdToColor(formId.Value)
                    : (MissingR, MissingG, MissingB);

                var imgX = imgCellX * HmGridSize + px;
                var imgY = imgCellY * HmGridSize + py;
                var dst = (imgY * stride + imgX) * 4;
                rgba[dst] = r;
                rgba[dst + 1] = g;
                rgba[dst + 2] = b;
                rgba[dst + 3] = 255;
            }
        }
    }

    /// <summary>
    ///     Sample-based blit for the rendered "Terrain textures" layer. For each vertex,
    ///     compute its world position and ask the palette for the winning LTEX's diffuse
    ///     color at that position. When no winner exists, or the winner's texture failed to
    ///     load, falls back to the engine-default landscape texture (DirtWasteland01 per the
    ///     Fallout.ini <c>SDefaultLandDiffuseTexture</c>). If even that can't load (no Textures
    ///     BSA), falls back to a hardcoded RGB tint.
    /// </summary>
    /// <summary>
    ///     Pixel-shader equivalent of the 3D viewer's terrain blend (terrain_textured.frag.hlsl):
    ///     for each pixel, sample the BTXT base of its quadrant, then walk the ATXT layers in
    ///     render order doing <c>color = lerp(color, layerSample, opacity)</c>. The per-vertex
    ///     opacity grid is bilinearly interpolated across the pixel grid so the transitions
    ///     between ATXT zones are smooth instead of stepped at half-opacity (the prior
    ///     "winners-with-0.5-threshold" approach).
    /// </summary>
    private static void BlitTerrainTexturesBlended(
        byte[] rgba, int stride, int pixelsPerCell, TextureBlendStack? stack,
        LandscapeTexturePalette palette,
        int cellGridX, int cellGridY, int imgCellX, int imgCellY)
    {
        var worldUnitsPerPixel = 4096f / pixelsPerCell;
        var pixelToVertex = (float)(HmGridSize - 1) / (pixelsPerCell - 1);
        var cellOriginX = cellGridX * 4096f;
        var cellOriginY = cellGridY * 4096f;

        for (var py = 0; py < pixelsPerCell; py++)
        {
            // Image py=0 is north. World Y grows northward, so flip py to get the world-Y offset.
            var worldY = cellOriginY + (pixelsPerCell - 1 - py) * worldUnitsPerPixel;
            var vyFloat = py * pixelToVertex;  // 0..(HmGridSize-1)

            for (var px = 0; px < pixelsPerCell; px++)
            {
                var worldX = cellOriginX + px * worldUnitsPerPixel;
                var vxFloat = px * pixelToVertex;

                (byte R, byte G, byte B)? color = null;
                if (stack is not null)
                {
                    var (quad, qxFloat, qyFloat) = ResolveQuadrantFractional(vxFloat, vyFloat);

                    // BTXT base for this quadrant.
                    var baseFormId = stack.BaseFormIds[quad];
                    if (baseFormId.HasValue)
                    {
                        color = palette.Sample(baseFormId.Value, worldX, worldY);
                    }

                    // ATXT layers for this quadrant, in render order, lerp on top of base.
                    foreach (var alpha in stack.AlphaLayers)
                    {
                        if (alpha.Quadrant != quad) continue;
                        var opacity = BilinearOpacity(alpha.OpacityGrid, qxFloat, qyFloat);
                        if (opacity <= 0f) continue;
                        var alphaSample = palette.Sample(alpha.TextureFormId, worldX, worldY);
                        if (alphaSample is null) continue;
                        color = color is null
                            ? alphaSample
                            : LerpRgb(color.Value, alphaSample.Value, opacity);
                    }
                }

                color ??= palette.SampleEngineDefault(worldX, worldY);
                var (r, g, b) = color ?? (DefaultTerrainR, DefaultTerrainG, DefaultTerrainB);

                var imgX = imgCellX * pixelsPerCell + px;
                var imgY = imgCellY * pixelsPerCell + py;
                var dst = (imgY * stride + imgX) * 4;
                rgba[dst] = r;
                rgba[dst + 1] = g;
                rgba[dst + 2] = b;
                rgba[dst + 3] = 255;
            }
        }
    }

    private const int QuadSize = 17;
    private const int QuadVertCount = QuadSize * QuadSize;

    /// <summary>BTXT base + ATXT layer stack for one cell, ready for per-pixel blending.</summary>
    private sealed class TextureBlendStack
    {
        /// <summary>Base diffuse FormID per quadrant (0=SW, 1=SE, 2=NW, 3=NE). Null when absent.</summary>
        internal uint?[] BaseFormIds { get; } = new uint?[4];

        /// <summary>ATXT layers in render order (quadrant ASC, layer-index ASC).</summary>
        internal List<AlphaLayer> AlphaLayers { get; } = new();
    }

    /// <summary>One ATXT alpha layer: a diffuse FormID + dense 17×17 per-vertex opacity grid.</summary>
    private sealed class AlphaLayer
    {
        internal int Quadrant { get; init; }
        internal uint TextureFormId { get; init; }

        /// <summary>Row-major, [qy * QuadSize + qx], qy=0 is the SW edge of the quadrant.</summary>
        internal float[] OpacityGrid { get; init; } = new float[QuadVertCount];
    }

    /// <summary>
    ///     Build a <see cref="TextureBlendStack" /> from a cell's LAND texture layers. BTXT
    ///     becomes per-quadrant base; ATXT layers are flattened into ordered layers each with
    ///     their own dense opacity grid (sparse BlendEntries → dense float[17*17]). Returns
    ///     null when no layer contributes any texture data.
    /// </summary>
    private static TextureBlendStack? BuildTextureBlendStack(List<LandTextureLayer> layers)
    {
        var stack = new TextureBlendStack();
        var any = false;

        foreach (var layer in layers)
        {
            if (layer.Quadrant >= 4) continue;
            if (layer.Kind == LandTextureLayerKind.Base)
            {
                stack.BaseFormIds[layer.Quadrant] = layer.TextureFormId;
                any = true;
            }
        }

        var alphaLayers = layers
            .Where(l => l.Kind == LandTextureLayerKind.Alpha && l.Quadrant < 4)
            .OrderBy(l => l.Quadrant)
            .ThenBy(l => l.Layer);

        foreach (var layer in alphaLayers)
        {
            var grid = new float[QuadVertCount];
            foreach (var entry in layer.BlendEntries)
            {
                if (entry.Position < QuadVertCount)
                {
                    grid[entry.Position] = entry.Opacity;
                }
            }
            stack.AlphaLayers.Add(new AlphaLayer
            {
                Quadrant = layer.Quadrant,
                TextureFormId = layer.TextureFormId,
                OpacityGrid = grid
            });
            any = true;
        }

        return any ? stack : null;
    }

    /// <summary>
    ///     Map a cell-image vertex-space float coord (0..HmGridSize-1) to its quadrant and the
    ///     fractional quadrant-local coord. Quadrant convention: 0=SW, 1=SE, 2=NW, 3=NE; the
    ///     quadrant-local (0,0) is the SW corner (per VTXT Position layout). Boundary pixels
    ///     (vx=16 or vy=16) clamp into the north/east quadrants.
    /// </summary>
    private static (int Quadrant, float QxFloat, float QyFloat) ResolveQuadrantFractional(
        float vxFloat, float vyFloat)
    {
        var isNorth = vyFloat <= 16f;
        var isEast = vxFloat >= 16f;
        var quad = (isNorth, isEast) switch
        {
            (true, false) => 2,
            (true, true) => 3,
            (false, false) => 0,
            (false, true) => 1
        };
        var qxFloat = isEast ? vxFloat - 16f : vxFloat;
        var qyFloat = isNorth ? 16f - vyFloat : 32f - vyFloat;
        return (quad,
            Math.Clamp(qxFloat, 0f, QuadSize - 1),
            Math.Clamp(qyFloat, 0f, QuadSize - 1));
    }

    /// <summary>
    ///     Bilinearly sample the 17×17 opacity grid at fractional quad-local (qx, qy). Returns
    ///     a value in [0, 1].
    /// </summary>
    private static float BilinearOpacity(float[] grid, float qx, float qy)
    {
        var x0 = (int)qx;
        var y0 = (int)qy;
        var x1 = Math.Min(x0 + 1, QuadSize - 1);
        var y1 = Math.Min(y0 + 1, QuadSize - 1);
        var fx = qx - x0;
        var fy = qy - y0;
        var o00 = grid[y0 * QuadSize + x0];
        var o10 = grid[y0 * QuadSize + x1];
        var o01 = grid[y1 * QuadSize + x0];
        var o11 = grid[y1 * QuadSize + x1];
        var top = o00 + (o10 - o00) * fx;
        var bot = o01 + (o11 - o01) * fx;
        return top + (bot - top) * fy;
    }

    private static (byte R, byte G, byte B) LerpRgb(
        (byte R, byte G, byte B) a, (byte R, byte G, byte B) b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return (
            (byte)(a.R + (b.R - a.R) * t),
            (byte)(a.G + (b.G - a.G) * t),
            (byte)(a.B + (b.B - a.B) * t));
    }

    // ========================================================================
    // Hillshade
    // ========================================================================

    private static byte[] ComputeHillshade(float[] heightField, bool[] hasHeight, int width, int height)
    {
        var rgba = new byte[width * height * 4];
        // NW sun, slightly elevated. Lambertian shade w/ ambient floor.
        var lightDir = Vector3.Normalize(new Vector3(-1f, 1f, 1.5f));
        // Tunes how punchy slope reads in a 33-vert-per-cell world. ~0.02 keeps
        // gentle dunes visible without hard cliffs blowing to pure white.
        const float zScale = 0.02f;
        const float ambient = 0.15f;

        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var idx = y * width + x;
                if (!hasHeight[idx])
                {
                    rgba[idx * 4 + 3] = 255;
                    continue;
                }

                var x0 = Math.Max(0, x - 1);
                var x1 = Math.Min(width - 1, x + 1);
                var y0 = Math.Max(0, y - 1);
                var y1 = Math.Min(height - 1, y + 1);

                var hCenter = heightField[idx];
                var hRight = hasHeight[y * width + x1] ? heightField[y * width + x1] : hCenter;
                var hLeft = hasHeight[y * width + x0] ? heightField[y * width + x0] : hCenter;
                var hUp = hasHeight[y0 * width + x] ? heightField[y0 * width + x] : hCenter;
                var hDown = hasHeight[y1 * width + x] ? heightField[y1 * width + x] : hCenter;

                var dx = hRight - hLeft;
                var dy = hDown - hUp;

                var normal = Vector3.Normalize(new Vector3(-dx * zScale, -dy * zScale, 1f));
                var shade = Math.Max(ambient, Vector3.Dot(normal, lightDir));
                var gray = (byte)Math.Clamp(shade * 255f, 0f, 255f);

                rgba[idx * 4] = gray;
                rgba[idx * 4 + 1] = gray;
                rgba[idx * 4 + 2] = gray;
                rgba[idx * 4 + 3] = 255;
            }
        }

        return rgba;
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private static byte[] InitMissingBackground(int width, int height)
    {
        var rgba = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var dst = i * 4;
            rgba[dst] = MissingR;
            rgba[dst + 1] = MissingG;
            rgba[dst + 2] = MissingB;
            rgba[dst + 3] = 255;
        }
        return rgba;
    }

    private static byte[] RenderMissingCell(
        CellRecord cell,
        float? defaultWaterHeight,
        bool showWater,
        WorldRenderCache? cache)
    {
        var rgba = InitMissingBackground(HmGridSize, HmGridSize);
        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
    }

    private static IEnumerable<CellRecord> EnumerateCellsWithGrid(List<CellRecord> cellSource)
    {
        foreach (var c in cellSource)
        {
            if (c.GridX.HasValue && c.GridY.HasValue)
            {
                yield return c;
            }
        }
    }

    private static float? ResolveWaterHeight(CellRecord cell, float? defaultWaterHeight)
        => WorldRenderCache.ResolveEffectiveWaterHeight(cell, defaultWaterHeight);

    private static void ApplyCellWaterOverlay(byte[] rgba, CellRecord cell, float? defaultWaterHeight, bool showWater,
        int pixelsPerCell = HmGridSize,
        WorldRenderCache? cache = null)
    {
        if (!showWater) return;

        // Build the water mask at HmGridSize (cell heightmap native resolution), then nearest-
        // neighbor upscale to pixelsPerCell so it composites with high-res texture bitmaps.
        var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
        var lowResMask = terrain.GetLowResWaterMask(ResolveWaterHeight(cell, defaultWaterHeight));
        if (lowResMask is null) return;

        if (pixelsPerCell == HmGridSize)
        {
            OverlayWater(rgba, lowResMask, HmGridSize, HmGridSize);
        }
        else
        {
            var scale = pixelsPerCell / HmGridSize;
            var hiResMask = UpscaleMaskNearest(lowResMask, HmGridSize, HmGridSize, scale);
            OverlayWater(rgba, hiResMask, pixelsPerCell, pixelsPerCell);
        }
    }

    private static void OverlayWater(byte[] rgba, byte[] waterMask, int width, int height)
    {
        var pixelCount = width * height;
        for (var i = 0; i < pixelCount; i++)
        {
            if (waterMask[i] == 0) continue;
            var factor = waterMask[i] / 255f;
            var dst = i * 4;
            rgba[dst] = (byte)(rgba[dst] + (WaterR - rgba[dst]) * factor);
            rgba[dst + 1] = (byte)(rgba[dst + 1] + (WaterG - rgba[dst + 1]) * factor);
            rgba[dst + 2] = (byte)(rgba[dst + 2] + (WaterB - rgba[dst + 2]) * factor);
        }
    }

    /// <summary>
    ///     Map a FormID to a stable, visually distinct RGB color. Golden-angle hue separation
    ///     keeps neighboring FormIDs from collapsing to similar colors.
    /// </summary>
    private static (byte R, byte G, byte B) FormIdToColor(uint formId)
    {
        // 137.508° golden angle in hue space, modulo 360
        var hue = (formId * 137u + (formId >> 8) * 23u) % 360u;
        const float saturation = 0.65f;
        const float value = 0.85f;
        return HsvToRgb(hue, saturation, value);
    }

    private static (byte R, byte G, byte B) HsvToRgb(uint h, float s, float v)
    {
        var c = v * s;
        var hp = h / 60f;
        var x = c * (1f - MathF.Abs(hp % 2f - 1f));
        var (r1, g1, b1) = (int)hp switch
        {
            0 => (c, x, 0f),
            1 => (x, c, 0f),
            2 => (0f, c, x),
            3 => (0f, x, c),
            4 => (x, 0f, c),
            _ => (c, 0f, x)
        };
        var m = v - c;
        return (
            (byte)Math.Clamp((r1 + m) * 255f, 0f, 255f),
            (byte)Math.Clamp((g1 + m) * 255f, 0f, 255f),
            (byte)Math.Clamp((b1 + m) * 255f, 0f, 255f));
    }
}
