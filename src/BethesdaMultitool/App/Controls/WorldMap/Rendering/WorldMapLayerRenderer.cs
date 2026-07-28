using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Esm.Export.Heightmap;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Export;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Textures;
using BethesdaMultitool.Core.Orchestration;

namespace BethesdaMultitool;

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
    private const int MaxLoggedTerrainAggregateCellFailures = 8;

    /// <summary>Cells lacking the layer's source data render in this neutral gray.</summary>
    private const byte MissingR = 40, MissingG = 40, MissingB = 45;

    /// <summary>A rendered layer's RGBA pixel buffer plus the world-space origin (min cell X, max cell Y) it covers.</summary>
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
            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;

            // Normalize handles Morrowind's native 65×65 VCLR by downsampling to 33×33.
            var vc = WorldMapCellBlitter.NormalizeVertexColorsTo33(cell.LandVisualData?.VertexColors);
            if (vc is not null)
            {
                WorldMapCellBlitter.BlitVertexColorsToCell(rgba, width, vc, imgCellX, imgCellY);
                continue;
            }

            // Valid terrain cell with no VCLR subrecord — the engine treats absent vertex color as
            // white (no tint), so fill the cell white instead of leaving the "missing" background
            // showing. HasTerrain matches ComputeHeightmapData's extent test, so this cell is in-bounds.
            var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
            if (terrain.HasTerrain)
            {
                WorldMapCellBlitter.FillCellWhite(rgba, width, imgCellX, imgCellY);
            }
        }

        if (showWater) WorldMapWaterRenderer.OverlayWater(rgba, waterMask, width, height);
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
            var winners = cache?.GetTextureWinners(cell) ?? BuildWinnersFallback(cell);
            if (winners == null) continue;

            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;
            WorldMapCellBlitter.BlitTerrainRegionsToCell(rgba, width, winners, imgCellX, imgCellY);
        }

        if (showWater) WorldMapWaterRenderer.OverlayWater(rgba, waterMask, width, height);
        return new LayerBitmap(rgba, width, height, minX, maxY);
    }

    /// <summary>Minimum px/cell for the aggregate — even a huge worldspace keeps at least this much
    /// detail (a 248-cell worldspace at the 2048 target lands here).</summary>
    internal const int MinAggregatePixelsPerCell = 8;

    /// <summary>
    ///     Whole-worldspace TerrainTextures aggregate: renders every cell's ACTUAL blended terrain
    ///     textures at the coarse heightmap resolution (<see cref="HmGridSize" /> px/cell) and
    ///     composites them into ONE bitmap. Used as the zoomed-out LOD so a large worldspace shows the
    ///     full textured overview in a single fast bitmap instead of per-cell streaming thousands of
    ///     high-res cells (which floods decode/upload). When zoomed in, the caller switches back to the
    ///     per-cell high-res path. Returns null if the worldspace is too large to fit one bitmap under
    ///     <paramref name="maxDimension" /> (caller then keeps per-cell streaming).
    /// </summary>
    internal static LayerBitmap? RenderTerrainTexturesAggregate(
        List<CellRecord> cellSource, LandscapeTexturePalette palette,
        float? defaultWaterHeight, bool showWater,
        out int pixelsPerCell,
        WorldRenderCache? cache = null, WaterColorPalette? waterPalette = null,
        int targetLongEdge = 2048, int maxDimension = 8192,
        TerrainShadingOptions shading = default)
    {
        pixelsPerCell = HmGridSize;

        // ComputeHeightmapData sizes a 33 px/cell grid over cells WITH terrain data; we use only its
        // (minX, maxY) + cell extent. Cells with grid coords but outside that terrain extent (sparse
        // outliers) are skipped below so they can't overflow the buffer.
        var hm = HeightmapRenderer.ComputeHeightmapData(cellSource, defaultWaterHeight, cache);
        if (hm == null) return null;
        var (_, _, hmWidth, hmHeight, minX, maxY) = hm.Value;
        var gridW = hmWidth / HmGridSize;
        var gridH = hmHeight / HmGridSize;
        if (gridW <= 0 || gridH <= 0) return null;

        // Adaptive resolution: size the longest edge to ~targetLongEdge so the zoomed-out view
        // minifies the bitmap only mildly (≤~2-3×) instead of ~6× at a fixed 33 px/cell — that heavy
        // minification (plus GPU bilinear with no mips) is what produced the moire. Clamped to
        // [MinAggregatePixelsPerCell, HmGridSize]; small worldspaces stay at the crisp 33 px/cell.
        var maxCells = Math.Max(gridW, gridH);
        pixelsPerCell = Math.Clamp(targetLongEdge / maxCells, MinAggregatePixelsPerCell, HmGridSize);
        var ppc = pixelsPerCell;
        var width = gridW * ppc;
        var height = gridH * ppc;
        if (width <= 0 || height <= 0 || width > maxDimension || height > maxDimension) return null;

        palette.Preload(cellSource);
        var cellByGrid = BuildCellGridIndex(cellSource);
        var rgba = InitMissingBackground(width, height);

        // Render + blit cells in bounded parallelism: each writes a disjoint ppc×ppc block, so the
        // shared buffer needs no locking. The 2D texture-mode switch used to run this through
        // Parallel.ForEach's default scheduler, which could launch a large set of workers into the
        // weight-table and palette sampler hot path at once. Keep the aggregate path conservative by
        // default; callers can raise the centralized aggregate-concurrency env var when
        // stress-testing after crash fixes.
        var cells = EnumerateCellsWithGrid(cellSource).ToList();
        var aggregatePolicy = ConcurrencyPolicy.Fixed(1).WithEnvironmentOverride(
            EnvironmentVariables.Map2D.TerrainTextureAggregateConcurrency, max: int.MaxValue);
        var aggregateConcurrency = aggregatePolicy.Resolve();
        Map2DProfilerTrace.Event("terrain-aggregate-start",
            $"cells={cells.Count} ppc={ppc} size={width}x{height} concurrency={aggregateConcurrency}");
        var loggedFailures = 0;
        ParallelWork.ForEach(
            "map-layer-render",
            cells,
            aggregatePolicy,
            cell =>
        {
            try
            {
                var imgCellX = cell.GridX!.Value - minX;
                var imgCellY = maxY - cell.GridY!.Value;
                if (imgCellX < 0 || imgCellY < 0 || imgCellX >= gridW || imgCellY >= gridH) return;
                var bytes = WorldMapTextureBlitter.RenderTerrainTextureCellOverview(
                    cell, palette, defaultWaterHeight, showWater, cache, ppc, cellByGrid, waterPalette, shading);
                if (bytes is null) return;
                WorldMapCellBlitter.BlitCellRgbaBlock(rgba, width, bytes, ppc, imgCellX * ppc, imgCellY * ppc);
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref loggedFailures) <= MaxLoggedTerrainAggregateCellFailures)
                {
                    Logger.Instance.Warn(
                        "TerrainTextures aggregate: cell ({0},{1}) render failed and will be skipped: {2}",
                        cell.GridX,
                        cell.GridY,
                        ex);
                }
            }
        });
        Map2DProfilerTrace.Event("terrain-aggregate-complete",
            $"cells={cells.Count} failures={loggedFailures}");

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
    ///     Highest overview/detail cell texture resolution used by the viewport-dependent
    ///     terrain texture path. 1056 = 32 samples per LAND vertex interval (≈132 px per 512-unit
    ///     tile-repeat), so a tile-repeat displayed wider than ~128 px samples the 256 mip and
    ///     downscales instead of magnifying a lower mip — the zoomed-in sharpness the lower 528 cap
    ///     couldn't provide. Only a handful of cells are resident at this zoom, so the 4.5 MB/cell
    ///     bitmaps stay memory-bounded.
    /// </summary>
    internal const int MaxTexturePixelsPerCell = TexturePixelsPerCell * 8;

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
        WorldRenderCache? cache = null,
        int pixelsPerCell = TexturePixelsPerCell,
        WaterColorPalette? waterPalette = null,
        TerrainShadingOptions shading = default)
    {
        pixelsPerCell = NormalizeTexturePixelsPerCell(pixelsPerCell);
        palette.Preload(cellSource);
        var cellByGrid = BuildCellGridIndex(cellSource);
        var result = new Dictionary<(int gx, int gy), byte[]>();
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var bytes = WorldMapTextureBlitter.RenderTerrainTextureCellOverview(
                cell, palette, defaultWaterHeight, showWater, cache, pixelsPerCell, cellByGrid, waterPalette, shading);
            if (bytes is null) continue;
            result[(cell.GridX!.Value, cell.GridY!.Value)] = bytes;
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>
    ///     One streamed cell from <see cref="StreamTerrainTexturesPerCell" />. <see cref="Pixels" /> is
    ///     the water-FREE terrain tile; <see cref="Water" /> is the standalone premultiplied water tile
    ///     (same dimensions) or <c>null</c> for a dry cell. The consumer caches them as two aligned
    ///     layers and draws water after the placed-object overlay.
    /// </summary>
    internal readonly record struct TerrainTextureCellResult(
        int GridX, int GridY, int PixelsPerCell, byte[] Pixels, byte[]? Water);

    /// <summary>
    ///     Decodes cells in parallel on background workers and yields each completed cell
    ///     immediately via <see cref="IAsyncEnumerable{T}" />. Mirrors the Mapbox GL / Cesium
    ///     pattern of "render whatever tiles are currently available" — the UI consumer can
    ///     upload each cell to the GPU and invalidate the canvas one cell at a time so the
    ///     user sees terrain populate progressively instead of waiting for the whole viewport.
    ///     <para>
    ///         Worker count is <c>max(1, ProcessorCount - 2)</c> to leave headroom for the UI
    ///         thread + the parallel-foreach orchestrator. <see cref="LandscapeTexturePalette" />
    ///         tile loads are warmed via <see cref="LandscapeTexturePalette.PreloadAsync" />
    ///         BEFORE the parallel work starts so workers don't contend on tile-cache locks
    ///         during the per-pixel sample loop.
    ///     </para>
    /// </summary>
    internal static async IAsyncEnumerable<TerrainTextureCellResult> StreamTerrainTexturesPerCell(
        IReadOnlyList<CellRecord> cellSource,
        LandscapeTexturePalette palette,
        float? defaultWaterHeight,
        WorldRenderCache? cache,
        int pixelsPerCell,
        WaterColorPalette? waterPalette = null,
        TerrainShadingOptions shading = default,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        pixelsPerCell = NormalizeTexturePixelsPerCell(pixelsPerCell);

        // Warm the landscape-texture tile cache off-thread so the per-pixel Sample() paths in
        // the parallel workers take the no-lock fast paths. The water palette holds two RGB
        // tuples (no I/O), so there's nothing to preload there.
        await palette.PreloadAsync(cellSource).ConfigureAwait(false);

        // Build the (gx, gy) → CellRecord index and the materialized cell list in one pass.
        // The parallel workers read the index concurrently — safe because it's only mutated
        // here, before the workers start. Iterating the materialized list (vs the
        // yield-based EnumerateCellsWithGrid) skips the iterator-state-machine allocation.
        var cellByGrid = BuildCellGridIndexAndList(cellSource, out var workableCells);

        var channel = Channel.CreateUnbounded<TerrainTextureCellResult>(
            new UnboundedChannelOptions
            {
                SingleWriter = false,
                SingleReader = true,
                AllowSynchronousContinuations = false
            });

        var workers = Math.Max(1, Environment.ProcessorCount - 2);
        var renderTask = Task.Run(async () =>
        {
            try
            {
                await Parallel.ForEachAsync(
                    workableCells,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = workers,
                        CancellationToken = ct
                    },
                    (cell, innerCt) =>
                    {
                        innerCt.ThrowIfCancellationRequested();
                        try
                        {
                            // Per-cell try/catch so a single bad cell (corrupt LAND data,
                            // unexpected thread-safety issue in a downstream helper, etc.)
                            // doesn't abort the whole Parallel.ForEachAsync — Parallel's
                            // default behavior is to cancel sibling workers on first
                            // throw, which would manifest as "rendering stops at a radius"
                            // from the user's POV. Log + skip the offender, continue with
                            // the rest of the viewport.
                            // Terrain WITHOUT water (showWater:false) + a standalone water tile. The
                            // water tile is built UNCONDITIONALLY (regardless of the incoming showWater)
                            // so toggling water on later is a pure redraw — no re-stream. The
                            // draw-time toggle decides whether the cached water layer is painted.
                            var bytes = WorldMapTextureBlitter.RenderTerrainTextureCellOverview(
                                cell, palette, defaultWaterHeight, showWater: false, cache, pixelsPerCell, cellByGrid, waterPalette, shading);
                            if (bytes is not null)
                            {
                                var water = BuildCellWaterTile(
                                    cell, defaultWaterHeight, pixelsPerCell, cache, cellByGrid, waterPalette);
                                channel.Writer.TryWrite(new TerrainTextureCellResult(
                                    cell.GridX!.Value, cell.GridY!.Value, pixelsPerCell, bytes, water));
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            throw;
                        }
                        catch (Exception ex)
                        {
                            BethesdaMultitool.Core.Diagnostics.Logger.Instance.Warn(
                                "TerrainTextures: cell ({0},{1}) render failed and will be skipped: {2}",
                                cell.GridX!.Value, cell.GridY!.Value, ex);
                        }
                        return ValueTask.CompletedTask;
                    }).ConfigureAwait(false);
            }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var result in channel.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            yield return result;
        }

        // Surface any exception from the worker task. If ct canceled, this throws
        // OperationCanceledException which the caller's await-foreach catches.
        await renderTask.ConfigureAwait(false);
    }

    // ========================================================================
    // Coarse multi-cell tiling (zoomed-out overview for oversized worldspaces)
    // ========================================================================

    /// <summary>"Max tile size" in pixels per edge — the upper bound on one coarse tile's bitmap so a
    /// huge worldspace is split into a bounded set of GPU textures instead of one giant bitmap (which
    /// froze on FO76's APPALACHIA) or thousands of per-cell tiles (one GPU texture + DrawImage each).</summary>
    internal const int CoarseTilePixelSize = 512;

    /// <summary>Virtual long-edge resolution the whole coarse overview is sized to. Drives the adaptive
    /// px/cell so total tile memory + count stay bounded regardless of how scattered the cells are.</summary>
    internal const int CoarseOverviewTargetLongEdge = 4096;

    /// <summary>Floor for the adaptive coarse px/cell — never drop below this even for the largest grids.</summary>
    internal const int MinCoarsePixelsPerCell = 4;

    /// <summary>A built coarse-tile overview: each tile covers a <see cref="TileCellSpan" />² block of
    /// cells rendered at <see cref="PixelsPerCell" /> px/cell, so its bitmap is
    /// (TileCellSpan·PixelsPerCell)² RGBA. Tiles are keyed by tile-grid coords (cell ÷ TileCellSpan,
    /// floored) so the draw can position each at tileGx·TileCellSpan·CellWorldSize.</summary>
    internal readonly record struct CoarseTileSet(
        Dictionary<(int tileGx, int tileGy), byte[]> Tiles,
        int TileCellSpan,
        int PixelsPerCell);

    /// <summary>
    ///     Renders one of the terrain-derived overview layers (heightmap / vertex colors / regions /
    ///     slope) as COARSE TILES: a small set of multi-cell bitmaps instead of one giant whole-worldspace
    ///     bitmap (the FO76 APPALACHIA freeze) or one bitmap per cell (13k GPU textures + draw calls). The
    ///     px/cell adapts to the worldspace size so total memory + tile count stay bounded for any grid
    ///     extent or scatter. Each cell is rendered with the existing single-cell renderers, then box-
    ///     downsampled to the adaptive px/cell and blitted into its tile. Returns null when no cell
    ///     produced renderable terrain.
    /// </summary>
    internal static CoarseTileSet? RenderLayerCoarseTiles(
        WorldMapLayer layer,
        List<CellRecord> cellSource, float? defaultWaterHeight, bool showWater,
        HeightmapColorScheme scheme, WorldRenderCache? cache,
        Vector3? lightDir = null, float zScale = WorldMapHillshadeRenderer.DefaultZScale,
        int targetLongEdge = CoarseOverviewTargetLongEdge,
        int tilePixelSize = CoarseTilePixelSize)
    {
        var hasGrid = false;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            hasGrid = true;
            var gx = cell.GridX!.Value;
            var gy = cell.GridY!.Value;
            if (gx < minX) minX = gx;
            if (gx > maxX) maxX = gx;
            if (gy < minY) minY = gy;
            if (gy > maxY) maxY = gy;
        }
        if (!hasGrid) return null;

        var maxSpan = Math.Max(maxX - minX + 1, maxY - minY + 1);
        var ppc = Math.Clamp(targetLongEdge / Math.Max(1, maxSpan), MinCoarsePixelsPerCell, HmGridSize);
        var tileCellSpan = Math.Max(1, tilePixelSize / ppc);
        var tileSidePx = tileCellSpan * ppc;

        // Heightmap tint needs a worldspace-global height range so every tile shares one normalization
        // (a per-tile range would make each tile its own contrast). The other layers are per-cell.
        var globalMin = 0f;
        var globalRange = 1f;
        if (layer == WorldMapLayer.Heightmap &&
            !TryComputeGlobalHeightRange(cellSource, cache, out globalMin, out globalRange))
        {
            return null;
        }

        // The Slope layer shades across cell boundaries, so it needs the (gx,gy)→cell index to pull each
        // cell's neighbour border (otherwise the coarse overview seams at every cell edge — Bug 4). The
        // other layers are per-cell and ignore it, so only build it where it's used.
        var cellByGrid = layer == WorldMapLayer.Slope ? BuildCellGridIndex(cellSource) : null;

        var tiles = new Dictionary<(int tileGx, int tileGy), byte[]>();
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var gx = cell.GridX!.Value;
            var gy = cell.GridY!.Value;

            var cell33 = RenderCellForLayer(layer, cell, globalMin, globalRange,
                             defaultWaterHeight, showWater, scheme, cache, cellByGrid, lightDir, zScale)
                         ?? RenderMissingCell(cell, defaultWaterHeight, showWater, cache);
            var cellPixels = ppc == HmGridSize ? cell33 : WorldMapCellBlitter.DownsampleCell(cell33, HmGridSize, ppc);

            var tileGx = FloorDiv(gx, tileCellSpan);
            var tileGy = FloorDiv(gy, tileCellSpan);
            if (!tiles.TryGetValue((tileGx, tileGy), out var tile))
            {
                tile = new byte[tileSidePx * tileSidePx * 4];
                tiles[(tileGx, tileGy)] = tile;
            }

            // Cell position inside the tile. Image Y grows southward (north at top), so the tile's top
            // pixel row is its northernmost cell — mirror the per-cell aggregate's `maxY - gy` flip.
            var localCellX = gx - tileGx * tileCellSpan;
            var topCellY = (tileGy + 1) * tileCellSpan - 1;
            var localImgCellY = topCellY - gy;
            WorldMapCellBlitter.BlitCellRgbaBlock(tile, tileSidePx, cellPixels, ppc, localCellX * ppc, localImgCellY * ppc);
        }

        return tiles.Count == 0 ? null : new CoarseTileSet(tiles, tileCellSpan, ppc);
    }

    private static bool TryComputeGlobalHeightRange(
        List<CellRecord> cellSource, WorldRenderCache? cache, out float globalMin, out float globalRange)
    {
        globalMin = float.MaxValue;
        var globalMax = float.MinValue;
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
            if (!terrain.HasTerrain) continue;
            for (var y = 0; y < HmGridSize; y++)
            {
                for (var x = 0; x < HmGridSize; x++)
                {
                    var h = terrain.HeightAt(x, y);
                    if (h < globalMin) globalMin = h;
                    if (h > globalMax) globalMax = h;
                }
            }
        }

        if (globalMin > globalMax)
        {
            globalRange = 1f;
            return false; // no terrain in any cell
        }

        globalRange = globalMax - globalMin;
        if (globalRange < 0.001f) globalRange = 1f;
        return true;
    }

    private static byte[]? RenderCellForLayer(
        WorldMapLayer layer, CellRecord cell, float globalMin, float globalRange,
        float? defaultWaterHeight, bool showWater, HeightmapColorScheme scheme, WorldRenderCache? cache,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid = null,
        Vector3? lightDir = null, float zScale = WorldMapHillshadeRenderer.DefaultZScale)
        => layer switch
        {
            WorldMapLayer.Heightmap =>
                RenderHeightmapForCell(cell, globalMin, globalRange, defaultWaterHeight, showWater, scheme, cache),
            WorldMapLayer.VertexColors =>
                RenderVertexColorsForCell(cell, defaultWaterHeight, showWater, cache),
            WorldMapLayer.TerrainRegions =>
                RenderTerrainRegionsForCell(cell, defaultWaterHeight, showWater, cache),
            // No-palette fallback (no Textures BSA next to the ESM): regions stand in for textures.
            WorldMapLayer.TerrainTextures =>
                RenderTerrainRegionsForCell(cell, defaultWaterHeight, showWater, cache),
            // Slope is the one terrain-derived layer whose shading reaches across the cell boundary, so it
            // needs the neighbour index (else the hillshade replicates this cell's own edge and the
            // one-sided edge normals draw a grid seam at every cell border — Bug 4), plus the light
            // direction / z-scale so the coarse overview tracks the time-of-day slider like the whole-map path.
            WorldMapLayer.Slope =>
                RenderSlopeForCell(cell, defaultWaterHeight, showWater, cache, cellByGrid, lightDir, zScale),
            _ => null
        };

    /// <summary>Floored integer division (handles negative cell coords so tile-grid bucketing is
    /// contiguous across the origin).</summary>
    private static int FloorDiv(int a, int b)
        => a >= 0 ? a / b : -(((-a) + b - 1) / b);

    /// <summary>Single-cell tinted heightmap tile, normalized against the worldspace-global height
    /// range so it matches its neighbors. Water uses the shared per-cell overlay.</summary>
    private static byte[]? RenderHeightmapForCell(
        CellRecord cell, float globalMin, float globalRange,
        float? defaultWaterHeight, bool showWater, HeightmapColorScheme scheme,
        WorldRenderCache? cache)
    {
        var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
        if (!terrain.HasTerrain) return null;

        var rgba = new byte[HmGridSize * HmGridSize * 4];
        var tR = scheme.R / 255f;
        var tG = scheme.G / 255f;
        var tB = scheme.B / 255f;
        var colorful = scheme.Mode == HeightmapRenderMode.Colorful;
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var height = terrain.HeightAt(px, HmGridSize - 1 - py);
                var normalized = Math.Clamp((height - globalMin) / globalRange, 0f, 1f);
                var idx = (py * HmGridSize + px) * 4;
                if (colorful)
                {
                    var (cr, cg, cb) = HeightmapColorRenderer.HeightToColor(normalized);
                    rgba[idx] = cr;
                    rgba[idx + 1] = cg;
                    rgba[idx + 2] = cb;
                }
                else
                {
                    var gray = normalized * 255f;
                    rgba[idx] = (byte)(gray * tR);
                    rgba[idx + 1] = (byte)(gray * tG);
                    rgba[idx + 2] = (byte)(gray * tB);
                }
                rgba[idx + 3] = 255;
            }
        }

        WorldMapWaterRenderer.ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
    }

    /// <summary>
    ///     Build a (gx, gy) → CellRecord index from the cell source. Used by streaming/batch
    ///     terrain-texture renders to feed neighbor layers into <see cref="CellLayerWeightTable" />
    ///     for the cross-cell blend. Cells without grid coords are skipped.
    /// </summary>
    private static Dictionary<(int gx, int gy), CellRecord> BuildCellGridIndex(
        List<CellRecord> cellSource)
    {
        var index = new Dictionary<(int gx, int gy), CellRecord>(cellSource.Count);
        for (var i = 0; i < cellSource.Count; i++)
        {
            var c = cellSource[i];
            if (c.GridX is int gx && c.GridY is int gy)
            {
                index[(gx, gy)] = c;
            }
        }
        return index;
    }

    /// <summary>
    ///     Combined index + materialized workable-cell list. Streaming paths need both the
    ///     neighbor-lookup dict and a stable list to pass to <c>Parallel.ForEachAsync</c>;
    ///     this folds the two passes over <paramref name="cellSource" /> into one and avoids
    ///     the per-iteration yield-iterator state machine that <see cref="EnumerateCellsWithGrid" />
    ///     would otherwise allocate.
    /// </summary>
    private static Dictionary<(int gx, int gy), CellRecord> BuildCellGridIndexAndList(
        IReadOnlyList<CellRecord> cellSource,
        out List<CellRecord> workableCells)
    {
        var index = new Dictionary<(int gx, int gy), CellRecord>(cellSource.Count);
        workableCells = new List<CellRecord>(cellSource.Count);
        for (var i = 0; i < cellSource.Count; i++)
        {
            var c = cellSource[i];
            if (c.GridX is int gx && c.GridY is int gy)
            {
                index[(gx, gy)] = c;
                workableCells.Add(c);
            }
        }
        return index;
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

    internal static LayerBitmap? RenderSlope(
        List<CellRecord> cellSource, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null, Vector3? lightDir = null,
        float zScale = WorldMapHillshadeRenderer.DefaultZScale)
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

        // Per-cell hillshade with a 1-vertex neighbor border (see RenderCellHillshadeBordered): adjacent
        // cells share their edge vertex, so shading one contiguous field double-counts that vertex and
        // halves the cross-seam gradient — a visible grid line at every cell edge. Shading each cell over
        // a bordered 35×35 field makes the edge central-difference span a uniform 2-vertex distance, so
        // both cells produce the same shade at the shared vertex (seamless) while staying 33 px/cell.
        var cellByGrid = BuildCellGridIndex(cellSource);
        var rgba = new byte[width * height * 4];
        // Opaque-black background for no-terrain gaps inside the bounding box (matches the old
        // whole-field ComputeHillshade, which wrote (0,0,0,255) where hasHeight was false).
        for (var i = 0; i < width * height; i++) rgba[i * 4 + 3] = 255;
        var waterMask = new byte[width * height];

        foreach (var (cell, terrain) in cells)
        {
            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;

            var cellShade = RenderCellHillshadeBordered(cell, terrain, cellByGrid, cache, lightDir, zScale);
            WorldMapCellBlitter.BlitCellRgbaBlock(
                rgba, width, cellShade, HmGridSize, imgCellX * HmGridSize, imgCellY * HmGridSize);

            var waterH = WorldMapWaterRenderer.ResolveWaterHeight(cell, defaultWaterHeight);
            if (!waterH.HasValue || waterH.Value is <= -1e6f or >= 1e6f) continue;
            for (var py = 0; py < HmGridSize; py++)
            {
                for (var px = 0; px < HmGridSize; px++)
                {
                    if (terrain.HeightAt(px, HmGridSize - 1 - py) >= waterH.Value) continue;
                    var imgX = imgCellX * HmGridSize + px;
                    var imgY = imgCellY * HmGridSize + py;
                    waterMask[imgY * width + imgX] = 180;
                }
            }
        }

        HeightmapRenderer.BlurWaterMask(waterMask, width, height);
        if (showWater) WorldMapWaterRenderer.OverlayWater(rgba, waterMask, width, height);
        return new LayerBitmap(rgba, width, height, minX, maxY);
    }

    /// <summary>
    ///     Computes a single cell's 33×33 hillshade RGBA against a 1-vertex neighbor border so cell edges
    ///     shade seamlessly: the field is built at 35×35 (the cell's 33 vertices plus one ring pulled from
    ///     the 4 neighbours — corners are unused by the 4-neighbour normal), shaded, then cropped to the
    ///     inner 33×33. At the true worldspace edge (no neighbour) the border replicates the cell's own
    ///     edge, preserving the old clamped look there. Heights use the image convention (row 0 = north,
    ///     col 0 = west). <paramref name="lightDir" /> is null → the renderer's NW default.
    /// </summary>
    private static byte[] RenderCellHillshadeBordered(
        CellRecord cell, DecodedTerrainCell terrain,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid,
        WorldRenderCache? cache, Vector3? lightDir,
        float zScale = WorldMapHillshadeRenderer.DefaultZScale)
    {
        const int n = HmGridSize;   // 33 vertices
        const int w = n + 2;        // 35 (1-vertex border per side)
        var field = new float[w * w];
        var has = new bool[w * w];

        // Inner 33×33 at offset (1,1). HeightAt(vx, vy): vy=32 is north, so image row py → vy = 32 - py.
        for (var py = 0; py < n; py++)
        {
            for (var px = 0; px < n; px++)
            {
                field[(py + 1) * w + (px + 1)] = terrain.HeightAt(px, n - 1 - py);
                has[(py + 1) * w + (px + 1)] = true;
            }
        }

        var gx = cell.GridX!.Value;
        var gy = cell.GridY!.Value;
        var west = NeighborTerrain(cellByGrid, cache, gx - 1, gy);
        var east = NeighborTerrain(cellByGrid, cache, gx + 1, gy);
        var north = NeighborTerrain(cellByGrid, cache, gx, gy + 1);
        var south = NeighborTerrain(cellByGrid, cache, gx, gy - 1);

        for (var py = 0; py < n; py++)
        {
            // Left border (col 0) = vertex one west of this cell's west edge = west neighbour's vx=31.
            field[(py + 1) * w + 0] = west is { } we ? we.HeightAt(n - 2, n - 1 - py) : field[(py + 1) * w + 1];
            has[(py + 1) * w + 0] = true;
            // Right border (col 34) = one east of the east edge = east neighbour's vx=1.
            field[(py + 1) * w + (w - 1)] = east is { } ea ? ea.HeightAt(1, n - 1 - py) : field[(py + 1) * w + n];
            has[(py + 1) * w + (w - 1)] = true;
        }
        for (var px = 0; px < n; px++)
        {
            // Top border (row 0, north) = one north of the north edge = north neighbour's vy=1.
            field[0 * w + (px + 1)] = north is { } no ? no.HeightAt(px, 1) : field[1 * w + (px + 1)];
            has[0 * w + (px + 1)] = true;
            // Bottom border (row 34, south) = one south of the south edge = south neighbour's vy=31.
            field[(w - 1) * w + (px + 1)] = south is { } so ? so.HeightAt(px, n - 2) : field[n * w + (px + 1)];
            has[(w - 1) * w + (px + 1)] = true;
        }

        var rgba35 = WorldMapHillshadeRenderer.ComputeHillshade(field, has, w, w, lightDir, zScale);

        var rgba = new byte[n * n * 4];
        for (var py = 0; py < n; py++)
        {
            Array.Copy(rgba35, ((py + 1) * w + 1) * 4, rgba, py * n * 4, n * 4);
        }
        return rgba;
    }

    /// <summary>
    ///     Seamless 33×33 hillshade GRAY grid (one byte per vertex) for a cell, used by the textured
    ///     hill-shade modulation (item 6). Wraps <see cref="RenderCellHillshadeBordered" /> and keeps
    ///     only its (R==G==B) gray channel.
    /// </summary>
    internal static byte[] ComputeCellHillshadeGray(
        CellRecord cell, DecodedTerrainCell terrain,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid,
        WorldRenderCache? cache, Vector3? lightDir,
        float zScale = WorldMapHillshadeRenderer.DefaultZScale)
    {
        var rgba = RenderCellHillshadeBordered(cell, terrain, cellByGrid, cache, lightDir, zScale);
        var gray = new byte[HmGridSize * HmGridSize];
        for (var i = 0; i < gray.Length; i++) gray[i] = rgba[i * 4];
        return gray;
    }

    /// <summary>Decoded terrain for a neighbour cell, or null when there is no such cell (worldspace
    /// edge) or it has no terrain. Used to feed the hillshade border ring.</summary>
    private static DecodedTerrainCell? NeighborTerrain(
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid,
        WorldRenderCache? cache, int gx, int gy)
    {
        if (cellByGrid is null || !cellByGrid.TryGetValue((gx, gy), out var c)) return null;
        var t = cache?.GetTerrain(c) ?? DecodedTerrainCell.Decode(c);
        return t.HasTerrain ? t : null;
    }

    // ========================================================================
    // Single-cell renderers (for cell detail view)
    // ========================================================================

    internal static byte[]? RenderVertexColorsForCell(
        CellRecord cell, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        // Normalize handles Morrowind's native 65×65 VCLR by downsampling to 33×33.
        var vc = WorldMapCellBlitter.NormalizeVertexColorsTo33(cell.LandVisualData?.VertexColors);
        var rgba = new byte[HmGridSize * HmGridSize * 4];
        if (vc is not null)
        {
            WorldMapCellBlitter.BlitVertexColorsToCell(rgba, HmGridSize, vc, imgCellX: 0, imgCellY: 0);
        }
        else
        {
            // No VCLR: render white (engine default) for a valid terrain cell; nothing for a cell
            // without terrain so it stays blank rather than a misleading white tile.
            var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
            if (!terrain.HasTerrain) return null;
            WorldMapCellBlitter.FillCellWhite(rgba, HmGridSize, imgCellX: 0, imgCellY: 0);
        }

        WorldMapWaterRenderer.ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
    }

    /// <summary>Builds the terrain-region winner grid when there's no cache: Fallout BTXT/ATXT layers, or
    /// Morrowind's flat 16×16 VTEX grid. Mirrors <c>WorldRenderCache.GetTextureWinners</c>.</summary>
    private static TextureWinnerGrid? BuildWinnersFallback(CellRecord cell)
    {
        var layers = cell.LandVisualData?.TextureLayers;
        if (layers is { Count: > 0 }) return TextureWinnerGrid.Build(layers);
        if (cell.LandVisualData?.VtexTextureFormIds is { Length: > 0 } vtex) return TextureWinnerGrid.BuildFromVtex(vtex);
        return null;
    }

    internal static byte[]? RenderTerrainRegionsForCell(
        CellRecord cell, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null)
    {
        var winners = cache?.GetTextureWinners(cell) ?? BuildWinnersFallback(cell);
        if (winners == null) return null;

        var rgba = new byte[HmGridSize * HmGridSize * 4];
        WorldMapCellBlitter.BlitTerrainRegionsToCell(rgba, HmGridSize, winners, imgCellX: 0, imgCellY: 0);
        WorldMapWaterRenderer.ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
    }

    internal static byte[]? RenderTerrainTexturesForCell(
        CellRecord cell, LandscapeTexturePalette? palette,
        float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null,
        int pixelsPerCell = TexturePixelsPerCell,
        TerrainShadingOptions shading = default)
    {
        if (palette is null)
        {
            return RenderTerrainRegionsForCell(cell, defaultWaterHeight, showWater, cache);
        }

        if (!cell.GridX.HasValue || !cell.GridY.HasValue) return null;

        // Single-cell detail render (no cross-cell neighbour blend): the shared overview path with a
        // null grid index produces this cell's own blended diffuse, then applies VCLR/hillshade shading.
        palette.Preload([cell]);
        return WorldMapTextureBlitter.RenderTerrainTextureCellOverview(
            cell, palette, defaultWaterHeight, showWater, cache,
            NormalizeTexturePixelsPerCell(pixelsPerCell), cellByGrid: null, waterPalette: null, shading);
    }

    internal static byte[]? RenderSlopeForCell(
        CellRecord cell, float? defaultWaterHeight, bool showWater,
        WorldRenderCache? cache = null,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid = null,
        Vector3? lightDir = null,
        float zScale = WorldMapHillshadeRenderer.DefaultZScale)
    {
        var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
        if (!terrain.HasTerrain) return null;

        byte[] rgba;
        if (cellByGrid is not null && cell.GridX.HasValue && cell.GridY.HasValue)
        {
            // Seamless: shade against the neighbour border so this cell's edges match its neighbours
            // (used where cells are shown adjacent, e.g. the textured hill-shade modulation).
            rgba = RenderCellHillshadeBordered(cell, terrain, cellByGrid, cache, lightDir, zScale);
        }
        else
        {
            // Standalone cell-detail view: no adjacent cells are shown, so clamp at the cell boundary.
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

            rgba = WorldMapHillshadeRenderer.ComputeHillshade(
                heightField, hasHeight, HmGridSize, HmGridSize, lightDir, zScale);
        }

        WorldMapWaterRenderer.ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
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
        WorldMapWaterRenderer.ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
    }

    private static IEnumerable<CellRecord> EnumerateCellsWithGrid(List<CellRecord> cellSource)
    {
        for (var i = 0; i < cellSource.Count; i++)
        {
            var c = cellSource[i];
            if (c.GridX.HasValue && c.GridY.HasValue)
            {
                yield return c;
            }
        }
    }

    /// <summary>
    ///     Renders a cell's water as a STANDALONE premultiplied-alpha RGBA tile (RGB = water color ×
    ///     coverage, A = coverage). Drawn source-over onto the water-free terrain tile it reproduces the
    ///     old baked overlay byte-for-byte (<c>dst + (color − dst)·coverage</c>), but as its own layer
    ///     it can be drawn AFTER the placed-object overlay (so models are occluded by water — 2D-5) and
    ///     toggled without rebuilding the terrain cache (2D-6). Returns <c>null</c> for a dry cell.
    ///     Premultiplied so it matches how the terrain tiles are uploaded (<c>CanvasBitmap.CreateFromBytes</c>
    ///     default alpha mode) and blends correctly under Win2D source-over.
    /// </summary>
    internal static byte[]? BuildCellWaterTile(CellRecord cell, float? defaultWaterHeight,
        int pixelsPerCell, WorldRenderCache? cache,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid,
        WaterColorPalette? waterPalette)
    {
        var mask = WorldMapWaterRenderer.ComputeCellWaterMask(cell, defaultWaterHeight, pixelsPerCell, cache, cellByGrid);
        if (mask is null) return null;

        var pixelCount = pixelsPerCell * pixelsPerCell;
        var tile = new byte[pixelCount * 4];
        return WorldMapWaterRenderer.WriteWaterTilePixels(tile, mask, pixelCount, waterPalette) ? tile : null;
    }

    /// <summary>
    ///     Whole-worldspace standalone water layer: one premultiplied-alpha bitmap (transparent where
    ///     dry) built from the SAME 33 px/cell coverage mask the heightmap/vertex/region/slope aggregates
    ///     use (<see cref="HeightmapRenderer.ComputeHeightmapData" />), so it aligns with them exactly and
    ///     also registers over the adaptive-resolution TerrainTextures aggregate (drawn scaled in world
    ///     space). Lets every world-overview layer render water-free and draw water as one toggle-able
    ///     pass after the rendered-models overlay — mirroring the per-cell <see cref="BuildCellWaterTile" />
    ///     path at the zoomed-out LODs. Returns null when the worldspace has no water.
    /// </summary>
    internal static LayerBitmap? RenderWorldWaterAggregate(
        List<CellRecord> cellSource, float? defaultWaterHeight,
        WorldRenderCache? cache = null, WaterColorPalette? waterPalette = null)
    {
        var hm = HeightmapRenderer.ComputeHeightmapData(cellSource, defaultWaterHeight, cache);
        if (hm == null) return null;
        var (_, waterMask, width, height, minX, maxY) = hm.Value;

        var rgba = new byte[width * height * 4];
        return WorldMapWaterRenderer.WriteWaterTilePixels(rgba, waterMask, width * height, waterPalette)
            ? new LayerBitmap(rgba, width, height, minX, maxY)
            : null;
    }

    internal static int NormalizeTexturePixelsPerCell(int pixelsPerCell)
    {
        // Snap to the tier set {33, 66, 132, 264, 528, 1056}. The two low tiers serve the zoomed-out
        // transition zone (see ChooseTerrainTexturePixelsPerCell) so cells render near display
        // resolution instead of a heavily-minified 132; the 1056 top tier is the zoomed-in detail
        // resolution that lets a tile-repeat sample the 256 mip and downscale.
        if (pixelsPerCell <= HmGridSize)
        {
            return HmGridSize; // 33
        }

        if (pixelsPerCell <= HmGridSize * 2)
        {
            return HmGridSize * 2; // 66
        }

        if (pixelsPerCell <= TexturePixelsPerCell)
        {
            return TexturePixelsPerCell; // 132
        }

        if (pixelsPerCell <= TexturePixelsPerCell * 2)
        {
            return TexturePixelsPerCell * 2; // 264
        }

        if (pixelsPerCell <= TexturePixelsPerCell * 4)
        {
            return TexturePixelsPerCell * 4; // 528
        }

        return MaxTexturePixelsPerCell; // 1056
    }
}

