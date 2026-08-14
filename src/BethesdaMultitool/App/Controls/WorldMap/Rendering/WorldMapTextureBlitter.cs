using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;

namespace BethesdaMultitool;

/// <summary>
///     Terrain-textures layer pixel rendering for the world map, extracted from
///     <see cref="WorldMapLayerRenderer" />. Builds a single cell's blended terrain-texture tile
///     (<see cref="RenderTerrainTextureCellOverview" />), including the cross-cell edge blend, the
///     bilinear per-vertex LTEX weight accumulation, and the diffuse-tile sampling that mirrors
///     <c>NiTerrainLandShader</c>.
/// </summary>
internal static class WorldMapTextureBlitter
{
    private const int HmGridSize = 33;

    /// <summary>Darkest factor the hillshade may apply when MODULATING terrain textures. Lifts the
    /// hillshade's 0.15-ambient floor so steep slopes ease the diffuse toward this instead of crushing it
    /// to near-black (the standalone Slope layer keeps the full-contrast hillshade).</summary>
    private const float HillshadeMultiplyFloor = 0.5f;

    /// <summary>
    ///     Last-ditch terrain color for the Terrain Textures layer when even the engine-default
    ///     DirtWasteland01 texture can't be loaded (no Textures BSA next to the ESM). Tuned to
    ///     roughly match the averaged DirtWasteland01 diffuse so the fallback transition isn't
    ///     jarring. With a normal install the engine-default sample is used instead.
    /// </summary>
    private const byte DefaultTerrainR = 145, DefaultTerrainG = 122, DefaultTerrainB = 90;

    /// <summary>
    ///     Renders the terrain-textures layer for a single cell. Output is
    ///     <paramref name="pixelsPerCell" /> × <paramref name="pixelsPerCell" /> RGBA.
    ///     Cells without LAND texture layers still get rendered as engine-default terrain
    ///     so the user can see the cell exists.
    /// </summary>
    internal static byte[]? RenderTerrainTextureCellOverview(
        CellRecord cell, LandscapeTexturePalette palette,
        float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache,
        int pixelsPerCell,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid = null,
        WaterColorPalette? waterPalette = null,
        TerrainShadingOptions shading = default)
    {
        if (!cell.GridX.HasValue || !cell.GridY.HasValue) return null;

        var layers = cell.LandVisualData?.TextureLayers;

        // Cross-cell blend: with a (gx,gy)→CellRecord lookup, feed each direction's neighbor
        // layers to the weight-table builder so this cell's edge vertices accumulate the
        // neighbor's adjacent edge-quadrant BTXTs/ATXTs. Otherwise (single-cell detail view)
        // we skip the cross-cell fade and just render this cell's own contributions.
        IReadOnlyList<LandTextureLayer>? eastN = null, westN = null, northN = null, southN = null;
        if (cellByGrid is not null)
        {
            var gx = cell.GridX.Value;
            var gy = cell.GridY.Value;
            eastN = NeighborLayers(cellByGrid, gx + 1, gy);
            westN = NeighborLayers(cellByGrid, gx - 1, gy);
            northN = NeighborLayers(cellByGrid, gx, gy + 1);
            southN = NeighborLayers(cellByGrid, gx, gy - 1);
        }

        // Build a table when this cell or at least one neighbor has REAL layer data. The
        // "this cell has no layers but a neighbor does" case uses the synthetic engine-default
        // own layers so the cross-cell fade also extends INTO this cell's near-edge pixels —
        // otherwise the no-layer cell renders as pure engine-default and the boundary is a
        // step from "neighbor's blended edge" to "this cell's solid default."
        var hasOwnLayers = layers is { Count: > 0 };
        var hasRealNeighbor = IsRealLayerList(eastN) || IsRealLayerList(westN)
                           || IsRealLayerList(northN) || IsRealLayerList(southN);

        CellLayerWeightTable? table = null;
        // Morrowind (TES3) ground texturing is the flat 16×16 VTEX grid (resolved to LTEX FormIds),
        // the same source the 3D terrain builder uses. Prefer it whenever present so the 2D textured
        // layer matches the 3D viewer at full per-vertex fidelity. The parser ALSO emits a coarse
        // 4-quadrant TextureLayers fallback (so hasOwnLayers is true for TES3), so this must be checked
        // FIRST — otherwise the fine grid is dead code and Morrowind renders 4 dominant-texture blocks.
        // Fallout/Oblivion/Skyrim have no VtexTextureFormIds and fall through to the quadrant path.
        if (cell.LandVisualData?.VtexTextureFormIds is { Length: > 0 } vtex)
        {
            table = VtexCellWeightTableBuilder.Build(
                HmGridSize,
                (cell.GridX.Value, cell.GridY.Value),
                vtex,
                cellByGrid);
        }
        else if (hasOwnLayers || hasRealNeighbor)
        {
            var srcLayers = hasOwnLayers ? layers! : s_engineDefaultSyntheticLayers;
            // Reuse the per-worker scratch table across cells. BuildInto resets the vertex
            // grid and refills it without allocating a fresh 33×33 array or the ATXT dense
            // grids — those live on the table instance and are pooled across calls.
            var pooled = t_weightTableScratch ??= new CellLayerWeightTable();
            if (CellLayerWeightTable.BuildInto(pooled, srcLayers, eastN, westN, northN, southN))
            {
                table = pooled;
            }
        }

        var cellWorldSize = cell.CellWorldSize > 0 ? cell.CellWorldSize : WorldGridConstants.CellSize;
        var rgba = new byte[pixelsPerCell * pixelsPerCell * 4];
        BlitTerrainTexturesBlended(rgba, pixelsPerCell, pixelsPerCell, table, palette,
            cell.GridX.Value, cell.GridY.Value, imgCellX: 0, imgCellY: 0, cellWorldSize);
        // Modulate the diffuse by VCLR or hillshade BEFORE the water overlay so water stays unshaded.
        if (shading.IsActive)
        {
            ApplyTerrainShading(rgba, pixelsPerCell, shading, cell, cache, cellByGrid);
        }
        WorldMapWaterRenderer.ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, pixelsPerCell, cache, cellByGrid, waterPalette);
        return rgba;
    }

    /// <summary>
    ///     Multiplies a freshly-blitted terrain-texture cell tile by its per-vertex modulation grid —
    ///     either the VCLR vertex colors (engine-accurate terrain tint) or a Lambertian hillshade — both
    ///     a 33×33 vertex grid bilinearly sampled up to <paramref name="pixelsPerCell" />. No-ops when the
    ///     source grid is absent (no VCLR / no terrain), so the raw diffuse shows through.
    /// </summary>
    private static void ApplyTerrainShading(
        byte[] rgba, int pixelsPerCell, TerrainShadingOptions shading,
        CellRecord cell, WorldRenderCache? cache,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid)
    {
        // Both modulations are independent and multiply together. VCLR absent → no tint; no terrain →
        // no hillshade. Either grid being null just drops that factor (raw diffuse if both null).
        byte[]? vc = null;        // 33×33×3 VCLR (LAND vertex order, north-up flip applied on read)
        byte[]? shadeGray = null; // 33×33 hillshade gray (image order, row 0 = north)
        if (shading.VertexColors)
        {
            // Normalize handles Morrowind's native 65×65 VCLR by downsampling to 33×33.
            vc = WorldMapCellBlitter.NormalizeVertexColorsTo33(cell.LandVisualData?.VertexColors);
        }
        if (shading.HillShade)
        {
            var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
            if (terrain.HasTerrain)
            {
                shadeGray = WorldMapLayerRenderer.ComputeCellHillshadeGray(
                    cell, terrain, cellByGrid, cache, shading.LightDir, shading.ZScale);
            }
        }
        if (vc is null && shadeGray is null) return; // nothing to apply

        var pixelToVertex = (float)(HmGridSize - 1) / (pixelsPerCell - 1);
        for (var py = 0; py < pixelsPerCell; py++)
        {
            var vyFloat = py * pixelToVertex;
            var vy0 = (int)vyFloat;
            if (vy0 > HmGridSize - 2) vy0 = HmGridSize - 2;
            var fy = vyFloat - vy0;

            for (var px = 0; px < pixelsPerCell; px++)
            {
                var vxFloat = px * pixelToVertex;
                var vx0 = (int)vxFloat;
                if (vx0 > HmGridSize - 2) vx0 = HmGridSize - 2;
                var fx = vxFloat - vx0;

                float mr = 1f, mg = 1f, mb = 1f; // combined modulation in 0..1
                if (vc is not null)
                {
                    (mr, mg, mb) = BilinearVc(vc, vx0, vy0, fx, fy);
                }
                if (shadeGray is not null)
                {
                    // Soften the hillshade when it MODULATES textures: lift the darkest factor toward
                    // HillshadeMultiplyFloor so steep slopes don't crush the diffuse to near-black. The
                    // standalone Slope layer keeps the full-contrast hillshade; only this multiply is eased.
                    var m = HillshadeMultiplyFloor
                            + (1f - HillshadeMultiplyFloor) * BilinearGray(shadeGray, vx0, vy0, fx, fy);
                    mr *= m;
                    mg *= m;
                    mb *= m;
                }

                var dst = (py * pixelsPerCell + px) * 4;
                rgba[dst] = (byte)(rgba[dst] * mr);
                rgba[dst + 1] = (byte)(rgba[dst + 1] * mg);
                rgba[dst + 2] = (byte)(rgba[dst + 2] * mb);
            }
        }
    }

    /// <summary>Bilinearly samples the VCLR grid at image-vertex (vx0+fx, vy0+fy), returning RGB in
    /// 0..1. VCLR is stored in LAND vertex order, so the image row maps to source row 32 - vy.</summary>
    private static (float r, float g, float b) BilinearVc(byte[] vc, int vx0, int vy0, float fx, float fy)
    {
        var (r00, g00, b00) = VcAt(vc, vx0, vy0);
        var (r10, g10, b10) = VcAt(vc, vx0 + 1, vy0);
        var (r01, g01, b01) = VcAt(vc, vx0, vy0 + 1);
        var (r11, g11, b11) = VcAt(vc, vx0 + 1, vy0 + 1);
        var w00 = (1f - fx) * (1f - fy);
        var w10 = fx * (1f - fy);
        var w01 = (1f - fx) * fy;
        var w11 = fx * fy;
        return (
            (r00 * w00 + r10 * w10 + r01 * w01 + r11 * w11) / 255f,
            (g00 * w00 + g10 * w10 + g01 * w01 + g11 * w11) / 255f,
            (b00 * w00 + b10 * w10 + b01 * w01 + b11 * w11) / 255f);
    }

    /// <summary>One VCLR vertex (image row vyImg → source row 32 - vyImg, matching BlitVertexColorsToCell).</summary>
    private static (float r, float g, float b) VcAt(byte[] vc, int vx, int vyImg)
    {
        var idx = ((HmGridSize - 1 - vyImg) * HmGridSize + vx) * 3;
        return (vc[idx], vc[idx + 1], vc[idx + 2]);
    }

    /// <summary>Bilinearly samples the image-order gray grid at (vx0+fx, vy0+fy), returning 0..1.</summary>
    private static float BilinearGray(byte[] gray, int vx0, int vy0, float fx, float fy)
    {
        var g00 = gray[vy0 * HmGridSize + vx0];
        var g10 = gray[vy0 * HmGridSize + vx0 + 1];
        var g01 = gray[(vy0 + 1) * HmGridSize + vx0];
        var g11 = gray[(vy0 + 1) * HmGridSize + vx0 + 1];
        return (g00 * (1f - fx) * (1f - fy) + g10 * fx * (1f - fy)
                + g01 * (1f - fx) * fy + g11 * fx * fy) / 255f;
    }

    private static IReadOnlyList<LandTextureLayer>? NeighborLayers(
        IReadOnlyDictionary<(int gx, int gy), CellRecord> index, int gx, int gy)
    {
        // Cell absent from the grid (e.g. worldspace edge) → no contribution: this cell's
        // edge keeps its own BTXT, matching the fact that nothing is drawn beyond the
        // worldspace boundary.
        if (!index.TryGetValue((gx, gy), out var neighbor)) return null;

        // Cell present but with no layers → contribute the engine-default sentinel. The
        // renderer paints this neighbor as solid DirtWasteland01, so this cell's edge needs
        // to blend toward that texture or you see a hard line where the engine default
        // begins.
        var layers = neighbor.LandVisualData?.TextureLayers;
        if (layers is not { Count: > 0 }) return s_engineDefaultSyntheticLayers;
        return layers;
    }

    /// <summary>
    ///     Per-worker scratch <see cref="CellLayerWeightTable" /> reused across the streaming
    ///     parallel-foreach. Allocating a fresh 33×33 vertex grid per cell was the largest
    ///     source of transient SOH pressure during a viewport rebuild (~15 MB for 290 cells);
    ///     reusing one instance per worker thread brings that to zero. Follows the existing
    ///     <c>t_xScratch</c> + <see cref="ThreadStaticAttribute" /> convention used by the
    ///     other world-map renderers (see <c>WorldMapOverviewRenderer.t_refScratch</c>).
    /// </summary>
    [ThreadStatic]
    private static CellLayerWeightTable? t_weightTableScratch;

    /// <summary>
    ///     Synthetic "all four quadrants are engine-default" layer set, used to represent a
    ///     neighbor cell that's part of the worldspace but has no LAND texture layers. The
    ///     <see cref="CellLayerWeightTable.EngineDefaultSentinelFormId" /> in each layer
    ///     routes through the same engine-default sampling path as missing-BTXT quadrants in
    ///     real cells, so the cross-cell blend treats "neighbor with no texture data" exactly
    ///     the way the renderer paints that neighbor.
    /// </summary>
    private static readonly IReadOnlyList<LandTextureLayer> s_engineDefaultSyntheticLayers =
    [
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 0 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 1 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 2 },
        new LandTextureLayer { Kind = LandTextureLayerKind.Base, TextureFormId = CellLayerWeightTable.EngineDefaultSentinelFormId, Quadrant = 3 },
    ];

    /// <summary>
    ///     "Real" = a neighbor that actually has authored LAND texture layers, NOT the
    ///     synthetic engine-default stand-in that <see cref="NeighborLayers" /> returns for
    ///     no-layer cells. Distinguishes "this cell has a real neighbor to blend toward"
    ///     (worth building a table) from "this cell and its neighbors are all engine-default"
    ///     (just fall through to the per-pixel engine-default fallback, no table needed).
    /// </summary>
    private static bool IsRealLayerList(IReadOnlyList<LandTextureLayer>? layers)
        => layers is { Count: > 0 } && !ReferenceEquals(layers, s_engineDefaultSyntheticLayers);

    /// <summary>
    ///     Sample-based blit for the rendered "Terrain textures" layer. For each pixel, find
    ///     its four enclosing cell-wide vertices and bilinearly interpolate their per-vertex
    ///     <c>(LTEX_FormID, weight)</c> contributions before sampling the diffuse texture for
    ///     each unique FormID. This mirrors what <c>NiTerrainLandShader</c> does in the live
    ///     engine (and what xLODGen replicates offline): shared edge vertices on the cell
    ///     midlines (vx=16 / vy=16) and the cell center (16,16) hold contributions from
    ///     2 or 4 quadrants' BTXTs respectively, so the bilinear interp produces a smooth
    ///     cross-quadrant blend instead of the hard seam a per-quadrant lookup gives.
    ///     When no LTEX FormID has a loaded tile (or the cell has no LAND texture layers
    ///     at all), falls back to the engine-default landscape texture (DirtWasteland01 per
    ///     the Fallout.ini <c>SDefaultLandDiffuseTexture</c>); if even that can't load (no
    ///     Textures BSA), falls back to a hardcoded RGB tint.
    /// </summary>
    internal static void BlitTerrainTexturesBlended(
        byte[] rgba, int stride, int pixelsPerCell, CellLayerWeightTable? table,
        LandscapeTexturePalette palette,
        int cellGridX, int cellGridY, int imgCellX, int imgCellY, float cellWorldSize)
    {
        var worldUnitsPerPixel = cellWorldSize / (pixelsPerCell - 1);
        var pixelToVertex = (float)(HmGridSize - 1) / (pixelsPerCell - 1);
        var cellOriginX = cellGridX * cellWorldSize;
        var cellOriginY = cellGridY * cellWorldSize;

        // Hoist tile-space UV math out of the per-pixel inner loop. Inside a cell
        // worldX/worldY are linear in px/py, so `worldX % WorldUnitsPerTile / WorldUnitsPerTile`
        // is a sawtooth with constant stride. Precompute the stride + seed each row's
        // tileFracX/Y; the inner loop advances them with a single add + conditional wrap
        // (worldUnitsPerPixel < WorldUnitsPerTile for any sane pixelsPerCell, so one wrap
        // step is sufficient).
        var tileStridePerPixel = worldUnitsPerPixel / LandscapeTexturePalette.WorldUnitsPerTile;

        // Footprint-driven mip selection: how many mip-0 (TileSize) texels one output pixel spans.
        // pixelsPerCell is constant for the whole cell, so the footprint — and thus the pyramid level —
        // is too; pick it once and reuse it per pixel. Zoomed-out renders (small pixelsPerCell) sample a
        // small, area-averaged mip → no minification moire; near-1:1 keeps the full tile. This is the
        // "use the DDS's own mip levels for the size it's displayed" behavior, done per output pixel.
        var texelsPerPixel = tileStridePerPixel * LandscapeTexturePalette.TileSize;
        var mipLevel = LandscapeTexturePalette.MipLevelForFootprint(texelsPerPixel);

        var tileFracX0 = LandscapeTexturePalette.WorldToTileFraction(cellOriginX);
        var worldYAtPy0 = cellOriginY + (pixelsPerCell - 1) * worldUnitsPerPixel;
        var tileFracY = LandscapeTexturePalette.WorldToTileFraction(worldYAtPy0);

        // Cap of 16 covers the worst case at the cell-center vertex (up to 4 BTXTs + up to
        // 12 ATXTs); any single 2×2 enclosing vertex neighborhood after dedup stays under
        // this bound. Stack-allocated, zero GC.
        // Keep this as a normal per-cell managed array instead of stackalloc Span. Crash dumps from
        // the full GUI texture-mode switch landed multiple workers inside AccumulateOne/
        // AccumulateVertexWeights with an ExecutionEngineException and no managed heap corruption.
        // The tiny per-cell allocation is a worthwhile trade for avoiding a heavily contended
        // stackalloc/ref-struct hot path while pageheap is chasing native/resource corruption.
        var combined = new LayerWeight[16];

        for (var py = 0; py < pixelsPerCell; py++)
        {
            var vyFloat = py * pixelToVertex;  // 0..(HmGridSize-1)
            var vy0 = (int)vyFloat;
            if (vy0 > HmGridSize - 2) vy0 = HmGridSize - 2;
            var fy = vyFloat - vy0;

            // Per-row tileFracX stepper resets to the cell-origin column fraction.
            var tileFracX = tileFracX0;

            for (var px = 0; px < pixelsPerCell; px++)
            {
                var vxFloat = px * pixelToVertex;
                var vx0 = (int)vxFloat;
                if (vx0 > HmGridSize - 2) vx0 = HmGridSize - 2;
                var fx = vxFloat - vx0;

                (byte R, byte G, byte B)? color = null;
                if (table is not null)
                {
                    ref var v00 = ref table.At(vx0, vy0);
                    ref var v10 = ref table.At(vx0 + 1, vy0);
                    ref var v01 = ref table.At(vx0, vy0 + 1);
                    ref var v11 = ref table.At(vx0 + 1, vy0 + 1);

                    // Fast path: when all four enclosing vertices carry a single LTEX entry
                    // and they're all the same FormID, the bilinear blend collapses to a
                    // single sample. Covers ~75-90% of pixels (pure-interior + uniform-BTXT
                    // open-desert cells).
                    if (v00.Count == 1 && v10.Count == 1 && v01.Count == 1 && v11.Count == 1
                        && v00.E0.FormId == v10.E0.FormId
                        && v00.E0.FormId == v01.E0.FormId
                        && v00.E0.FormId == v11.E0.FormId)
                    {
                        color = SampleLayer(palette, v00.E0.FormId, tileFracX, tileFracY, mipLevel);
                    }
                    else
                    {
                        var w00 = (1f - fx) * (1f - fy);
                        var w10 = fx * (1f - fy);
                        var w01 = (1f - fx) * fy;
                        var w11 = fx * fy;

                        var combinedCount = 0;
                        AccumulateVertexWeights(ref v00, w00, combined, ref combinedCount);
                        AccumulateVertexWeights(ref v10, w10, combined, ref combinedCount);
                        AccumulateVertexWeights(ref v01, w01, combined, ref combinedCount);
                        AccumulateVertexWeights(ref v11, w11, combined, ref combinedCount);

                        var weightedR = 0f;
                        var weightedG = 0f;
                        var weightedB = 0f;
                        var totalWeight = 0f;

                        for (var i = 0; i < combinedCount; i++)
                        {
                            var entry = combined[i];
                            if (entry.Weight <= 0f) continue;
                            var sample = SampleLayer(palette, entry.FormId, tileFracX, tileFracY, mipLevel);
                            if (sample is null) continue;
                            AddWeighted(sample.Value, entry.Weight,
                                ref weightedR, ref weightedG, ref weightedB);
                            totalWeight += entry.Weight;
                        }

                        if (totalWeight > 0f)
                        {
                            var inv = 1f / totalWeight;
                            color = (
                                FloatToByte(weightedR * inv),
                                FloatToByte(weightedG * inv),
                                FloatToByte(weightedB * inv));
                        }
                    }
                }

                color ??= palette.SampleEngineDefault(tileFracX, tileFracY, mipLevel);
                var (r, g, b) = color ?? (DefaultTerrainR, DefaultTerrainG, DefaultTerrainB);

                var imgX = imgCellX * pixelsPerCell + px;
                var imgY = imgCellY * pixelsPerCell + py;
                var dst = (imgY * stride + imgX) * 4;
                rgba[dst] = r;
                rgba[dst + 1] = g;
                rgba[dst + 2] = b;
                rgba[dst + 3] = 255;

                tileFracX += tileStridePerPixel;
                if (tileFracX >= 1f) tileFracX -= 1f;
            }

            // Image py=0 is north; world Y grows northward, so the per-row tile fraction
            // decreases as py advances.
            tileFracY -= tileStridePerPixel;
            if (tileFracY < 0f) tileFracY += 1f;
        }
    }

    /// <summary>
    ///     Resolve one layer-weight entry to a diffuse color sample at pre-resolved tile-space
    ///     fractions. The engine-default sentinel
    ///     (<see cref="CellLayerWeightTable.EngineDefaultSentinelFormId" />) routes to the
    ///     palette's DirtWasteland01 fallback; any other FormID samples the LTEX's tile, with
    ///     a fallback to the engine default when the tile failed to load (BSA missing, TXST
    ///     broken, etc.). With the engine-default write-through in
    ///     <c>LandscapeTexturePalette.TryGetTile</c>, the second-call fallback below is
    ///     essentially unreachable on a healthy install — kept defensive for the case where
    ///     even the engine-default texture is absent.
    /// </summary>
    private static (byte R, byte G, byte B)? SampleLayer(
        LandscapeTexturePalette palette, uint formId, float tileFracX, float tileFracY, int mipLevel)
    {
        if (formId == CellLayerWeightTable.EngineDefaultSentinelFormId)
        {
            return palette.SampleEngineDefault(tileFracX, tileFracY, mipLevel);
        }
        return palette.Sample(formId, tileFracX, tileFracY, mipLevel)
            ?? palette.SampleEngineDefault(tileFracX, tileFracY, mipLevel);
    }

    /// <summary>
    ///     Pull one vertex's entries into <paramref name="combined" />, multiplied by the
    ///     bilinear weight. Entries with the same FormID across the four enclosing vertices
    ///     are merged in place so we only sample each unique LTEX once per pixel. Caller-side
    ///     <paramref name="combined" /> is a stack-allocated span of capacity 16; entries past
    ///     the cap are dropped (analysis bounds the count at ≤16, so the cap shouldn't be hit
    ///     in practice).
    /// </summary>
    private static void AccumulateVertexWeights(
        ref VertexWeights v, float bw, LayerWeight[] combined, ref int count)
    {
        if (bw <= 0f || v.Count == 0) return;
        var vertexCount = v.Count;
        AccumulateOne(v.E0.FormId, v.E0.Weight * bw, combined, ref count);
        if (vertexCount > 1) AccumulateOne(v.E1.FormId, v.E1.Weight * bw, combined, ref count);
        if (vertexCount > 2) AccumulateOne(v.E2.FormId, v.E2.Weight * bw, combined, ref count);
        if (vertexCount > 3) AccumulateOne(v.E3.FormId, v.E3.Weight * bw, combined, ref count);
        if (vertexCount > 4 && v.Overflow is { } overflow)
        {
            var n = Math.Min(vertexCount - 4, overflow.Length);
            for (var i = 0; i < n; i++)
            {
                AccumulateOne(overflow[i].FormId, overflow[i].Weight * bw, combined, ref count);
            }
        }
    }

    private static void AccumulateOne(uint formId, float weight, LayerWeight[] combined, ref int count)
    {
        if (weight <= 0f) return;
        for (var i = 0; i < count; i++)
        {
            if (combined[i].FormId == formId)
            {
                combined[i] = new LayerWeight(formId, combined[i].Weight + weight);
                return;
            }
        }
        if (count < combined.Length)
        {
            combined[count++] = new LayerWeight(formId, weight);
        }
    }

    private static void AddWeighted(
        (byte R, byte G, byte B) color,
        float weight,
        ref float r,
        ref float g,
        ref float b)
    {
        r += color.R * weight;
        g += color.G * weight;
        b += color.B * weight;
    }

    private static byte FloatToByte(float value) => (byte)Math.Clamp(MathF.Round(value), 0f, 255f);
}
