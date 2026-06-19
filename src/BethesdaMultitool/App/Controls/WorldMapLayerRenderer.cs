using System.Numerics;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using BethesdaMultitool.Core;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.World;
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

    /// <summary>Water tint for the underwater overlay, matches HeightmapRenderer.</summary>
    private const byte WaterR = 30, WaterG = 55, WaterB = 120;

    /// <summary>Cells lacking the layer's source data render in this neutral gray.</summary>
    private const byte MissingR = 40, MissingG = 40, MissingB = 45;

    /// <summary>
    ///     Last-ditch terrain color for the Terrain Textures layer when even the engine-default
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
            var imgCellX = cell.GridX!.Value - minX;
            var imgCellY = maxY - cell.GridY!.Value;

            var vc = cell.LandVisualData?.VertexColors;
            if (vc is { Length: HmGridSize * HmGridSize * 3 })
            {
                BlitVertexColorsToCell(rgba, width, vc, imgCellX, imgCellY);
                continue;
            }

            // Valid terrain cell with no VCLR subrecord — the engine treats absent vertex color as
            // white (no tint), so fill the cell white instead of leaving the "missing" background
            // showing. HasTerrain matches ComputeHeightmapData's extent test, so this cell is in-bounds.
            var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
            if (terrain.HasTerrain)
            {
                FillCellWhite(rgba, width, imgCellX, imgCellY);
            }
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
        int targetLongEdge = 2048, int maxDimension = 8192)
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
                var bytes = RenderTerrainTextureCellOverview(
                    cell, palette, defaultWaterHeight, showWater, cache, ppc, cellByGrid, waterPalette);
                if (bytes is null) return;
                BlitCellRgbaBlock(rgba, width, bytes, ppc, imgCellX * ppc, imgCellY * ppc);
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

    /// <summary>Copies a <paramref name="cellSize" />×<paramref name="cellSize" /> RGBA cell block into
    /// the aggregate buffer at pixel offset (<paramref name="dstX" />,<paramref name="dstY" />).</summary>
    private static void BlitCellRgbaBlock(byte[] dst, int dstStride, byte[] cell, int cellSize, int dstX, int dstY)
    {
        var rowBytes = cellSize * 4;
        for (var row = 0; row < cellSize; row++)
        {
            var srcOff = row * rowBytes;
            var dstOff = ((dstY + row) * dstStride + dstX) * 4;
            Array.Copy(cell, srcOff, dst, dstOff, rowBytes);
        }
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
        WaterColorPalette? waterPalette = null)
    {
        pixelsPerCell = NormalizeTexturePixelsPerCell(pixelsPerCell);
        palette.Preload(cellSource);
        var cellByGrid = BuildCellGridIndex(cellSource);
        var result = new Dictionary<(int gx, int gy), byte[]>();
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var bytes = RenderTerrainTextureCellOverview(
                cell, palette, defaultWaterHeight, showWater, cache, pixelsPerCell, cellByGrid, waterPalette);
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
                            // so toggling water on later is a pure redraw — no re-stream (2D-6). The
                            // draw-time toggle decides whether the cached water layer is painted.
                            var bytes = RenderTerrainTextureCellOverview(
                                cell, palette, defaultWaterHeight, showWater: false, cache, pixelsPerCell, cellByGrid, waterPalette);
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
                            BethesdaMultitool.Core.Logger.Instance.Warn(
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

        var tiles = new Dictionary<(int tileGx, int tileGy), byte[]>();
        foreach (var cell in EnumerateCellsWithGrid(cellSource))
        {
            var gx = cell.GridX!.Value;
            var gy = cell.GridY!.Value;

            var cell33 = RenderCellForLayer(layer, cell, globalMin, globalRange,
                             defaultWaterHeight, showWater, scheme, cache)
                         ?? RenderMissingCell(cell, defaultWaterHeight, showWater, cache);
            var cellPixels = ppc == HmGridSize ? cell33 : DownsampleCell(cell33, HmGridSize, ppc);

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
            BlitCellRgbaBlock(tile, tileSidePx, cellPixels, ppc, localCellX * ppc, localImgCellY * ppc);
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
        float? defaultWaterHeight, bool showWater, HeightmapColorScheme scheme, WorldRenderCache? cache)
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
            WorldMapLayer.Slope =>
                RenderSlopeForCell(cell, defaultWaterHeight, showWater, cache),
            _ => null
        };

    /// <summary>Box-downsamples a square <paramref name="srcSize" />² RGBA cell tile to
    /// <paramref name="dstSize" />² (averaging the covered source texels). Used to render coarse-tile
    /// cells below the native 33 px/cell so a large worldspace's overview stays memory-bounded.</summary>
    private static byte[] DownsampleCell(byte[] src, int srcSize, int dstSize)
    {
        if (dstSize >= srcSize) return src;
        var dst = new byte[dstSize * dstSize * 4];
        var scale = (float)srcSize / dstSize;
        for (var dy = 0; dy < dstSize; dy++)
        {
            var sy0 = (int)(dy * scale);
            var sy1 = Math.Min(srcSize, (int)((dy + 1) * scale));
            if (sy1 <= sy0) sy1 = sy0 + 1;
            for (var dx = 0; dx < dstSize; dx++)
            {
                var sx0 = (int)(dx * scale);
                var sx1 = Math.Min(srcSize, (int)((dx + 1) * scale));
                if (sx1 <= sx0) sx1 = sx0 + 1;

                int r = 0, g = 0, b = 0, a = 0, n = 0;
                for (var sy = sy0; sy < sy1; sy++)
                {
                    for (var sx = sx0; sx < sx1; sx++)
                    {
                        var s = (sy * srcSize + sx) * 4;
                        r += src[s];
                        g += src[s + 1];
                        b += src[s + 2];
                        a += src[s + 3];
                        n++;
                    }
                }

                var d = (dy * dstSize + dx) * 4;
                dst[d] = (byte)(r / n);
                dst[d + 1] = (byte)(g / n);
                dst[d + 2] = (byte)(b / n);
                dst[d + 3] = (byte)(a / n);
            }
        }

        return dst;
    }

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
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var height = terrain.HeightAt(px, HmGridSize - 1 - py);
                var normalized = Math.Clamp((height - globalMin) / globalRange, 0f, 1f);
                var gray = normalized * 255f;
                var idx = (py * HmGridSize + px) * 4;
                rgba[idx] = (byte)(gray * tR);
                rgba[idx + 1] = (byte)(gray * tG);
                rgba[idx + 2] = (byte)(gray * tB);
                rgba[idx + 3] = 255;
            }
        }

        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, cache: cache);
        return rgba;
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
        WorldRenderCache? cache,
        int pixelsPerCell,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid = null,
        WaterColorPalette? waterPalette = null)
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
        if (hasOwnLayers || hasRealNeighbor)
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

        var rgba = new byte[pixelsPerCell * pixelsPerCell * 4];
        BlitTerrainTexturesBlended(rgba, pixelsPerCell, pixelsPerCell, table, palette,
            cell.GridX.Value, cell.GridY.Value, imgCellX: 0, imgCellY: 0);
        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, pixelsPerCell, cache, cellByGrid, waterPalette);
        return rgba;
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
        var rgba = new byte[HmGridSize * HmGridSize * 4];
        if (vc is { Length: HmGridSize * HmGridSize * 3 })
        {
            BlitVertexColorsToCell(rgba, HmGridSize, vc, imgCellX: 0, imgCellY: 0);
        }
        else
        {
            // No VCLR: render white (engine default) for a valid terrain cell; nothing for a cell
            // without terrain so it stays blank rather than a misleading white tile.
            var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
            if (!terrain.HasTerrain) return null;
            FillCellWhite(rgba, HmGridSize, imgCellX: 0, imgCellY: 0);
        }

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
        WorldRenderCache? cache = null,
        int pixelsPerCell = TexturePixelsPerCell)
    {
        if (palette is null)
        {
            return RenderTerrainRegionsForCell(cell, defaultWaterHeight, showWater, cache);
        }

        if (!cell.GridX.HasValue || !cell.GridY.HasValue) return null;
        pixelsPerCell = NormalizeTexturePixelsPerCell(pixelsPerCell);

        var layers = cell.LandVisualData?.TextureLayers;
        var table = layers is { Count: > 0 } ? CellLayerWeightTable.Build(layers) : null;

        palette.Preload([cell]);
        var rgba = new byte[pixelsPerCell * pixelsPerCell * 4];
        BlitTerrainTexturesBlended(rgba, pixelsPerCell, pixelsPerCell, table, palette,
            cell.GridX.Value, cell.GridY.Value, imgCellX: 0, imgCellY: 0);
        ApplyCellWaterOverlay(rgba, cell, defaultWaterHeight, showWater, pixelsPerCell, cache);
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

    /// <summary>Fills one cell's px block with opaque white — the engine's default (no-tint) vertex
    /// color, used for valid terrain cells that carry no VCLR subrecord.</summary>
    private static void FillCellWhite(byte[] rgba, int stride, int imgCellX, int imgCellY)
    {
        for (var py = 0; py < HmGridSize; py++)
        {
            for (var px = 0; px < HmGridSize; px++)
            {
                var imgX = imgCellX * HmGridSize + px;
                var imgY = imgCellY * HmGridSize + py;
                var dst = (imgY * stride + imgX) * 4;
                rgba[dst] = 255;
                rgba[dst + 1] = 255;
                rgba[dst + 2] = 255;
                rgba[dst + 3] = 255;
            }
        }
    }

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
    private static void BlitTerrainTexturesBlended(
        byte[] rgba, int stride, int pixelsPerCell, CellLayerWeightTable? table,
        LandscapeTexturePalette palette,
        int cellGridX, int cellGridY, int imgCellX, int imgCellY)
    {
        var worldUnitsPerPixel = 4096f / (pixelsPerCell - 1);
        var pixelToVertex = (float)(HmGridSize - 1) / (pixelsPerCell - 1);
        var cellOriginX = cellGridX * 4096f;
        var cellOriginY = cellGridY * 4096f;

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
        for (var i = 0; i < cellSource.Count; i++)
        {
            var c = cellSource[i];
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
        WorldRenderCache? cache = null,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid = null,
        WaterColorPalette? waterPalette = null)
    {
        if (!showWater) return;

        var hiResMask = ComputeCellWaterMask(cell, defaultWaterHeight, pixelsPerCell, cache, cellByGrid);
        if (hiResMask is null) return;

        // DNAM color path: when the WATR record exposed Shallow/Deep colors, lerp Shallow→Deep
        // by mask intensity so different worldspaces' waters (Potomac muddy brown vs Lake Mead
        // clean blue, Vault water, etc.) actually look different. Fall back to the legacy
        // solid blue when the WATR record is missing or has no DNAM colors — preserves the
        // pre-DNAM look exactly.
        if (waterPalette is not null)
        {
            OverlayWaterColored(rgba, hiResMask, pixelsPerCell, pixelsPerCell, waterPalette);
        }
        else
        {
            OverlayWater(rgba, hiResMask, pixelsPerCell, pixelsPerCell);
        }
    }

    /// <summary>
    ///     Computes the per-pixel water coverage mask for one cell (0 = dry, up to ~180 interior
    ///     pre-blur, with a soft shoreline fade), or <c>null</c> when the cell has no water. Shared by
    ///     the bake path (<see cref="ApplyCellWaterOverlay" />, still used for export + secondary
    ///     layers) and the standalone water-tile path (<see cref="BuildCellWaterTile" />) so both read
    ///     the SAME shoreline geometry.
    ///     <para>
    ///         When the texture path requests <c>pixelsPerCell &gt; HmGridSize</c> the mask is built
    ///         directly at the target resolution (per-pixel bilinear height vs waterH → linear shoreline
    ///         fade), which replaces the legacy "33×33, blur, nearest-neighbor upscale" chain that
    ///         produced blocky high-zoom shorelines. Cross-cell continuity is automatic there because
    ///         adjacent cells share edge vertices. The 33×33 path keeps the neighbor-aware blur because
    ///         its 3×3 box blur clamps at borders.
    ///     </para>
    /// </summary>
    private static byte[]? ComputeCellWaterMask(CellRecord cell, float? defaultWaterHeight,
        int pixelsPerCell, WorldRenderCache? cache,
        IReadOnlyDictionary<(int gx, int gy), CellRecord>? cellByGrid)
    {
        var terrain = cache?.GetTerrain(cell) ?? DecodedTerrainCell.Decode(cell);
        var waterH = ResolveWaterHeight(cell, defaultWaterHeight);

        if (pixelsPerCell == HmGridSize)
        {
            if (cellByGrid is not null && cell.GridX is int gx && cell.GridY is int gy)
            {
                return DecodedTerrainCell.BuildLowResWaterMaskWithNeighbors(
                    terrain,
                    GetNeighborTerrain(cellByGrid, gx, gy + 1, cache),
                    GetNeighborTerrain(cellByGrid, gx, gy - 1, cache),
                    GetNeighborTerrain(cellByGrid, gx + 1, gy, cache),
                    GetNeighborTerrain(cellByGrid, gx - 1, gy, cache),
                    waterH);
            }

            return terrain.GetLowResWaterMask(waterH);
        }

        return terrain.GetHiResWaterMask(waterH, pixelsPerCell);
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
        var mask = ComputeCellWaterMask(cell, defaultWaterHeight, pixelsPerCell, cache, cellByGrid);
        if (mask is null) return null;

        var pixelCount = pixelsPerCell * pixelsPerCell;
        var tile = new byte[pixelCount * 4];
        return WriteWaterTilePixels(tile, mask, pixelCount, waterPalette) ? tile : null;
    }

    /// <summary>
    ///     Writes a premultiplied-alpha water layer (RGB = water color × coverage, A = mask) from a
    ///     coverage <paramref name="mask" /> into a transparent <paramref name="tile" /> buffer. Source
    ///     of truth for the standalone-water look, shared by the per-cell <see cref="BuildCellWaterTile" />
    ///     and the whole-worldspace <see cref="RenderWorldWaterAggregate" />. Color matches the old bake:
    ///     DNAM Shallow→Deep lerp by depth (mask/180) when a palette is given, else solid blue; coverage
    ///     is always mask/255 (same shoreline AA as <see cref="OverlayWater" />). Returns whether any
    ///     non-dry pixel was written.
    /// </summary>
    private static bool WriteWaterTilePixels(byte[] tile, byte[] mask, int pixelCount, WaterColorPalette? waterPalette)
    {
        var any = false;
        if (waterPalette is not null)
        {
            const float MaskInteriorMax = 180f; // Mirror OverlayWaterColored: lerp Shallow→Deep by mask/180.
            float shallowR = waterPalette.Shallow.R, shallowG = waterPalette.Shallow.G, shallowB = waterPalette.Shallow.B;
            float deepR = waterPalette.Deep.R, deepG = waterPalette.Deep.G, deepB = waterPalette.Deep.B;
            for (var i = 0; i < pixelCount; i++)
            {
                var maskValue = mask[i];
                if (maskValue == 0) continue;
                any = true;

                var depthT = maskValue / MaskInteriorMax;
                if (depthT > 1f) depthT = 1f;
                var coverage = maskValue / 255f;

                var dst = i * 4;
                tile[dst] = (byte)((shallowR + (deepR - shallowR) * depthT) * coverage);
                tile[dst + 1] = (byte)((shallowG + (deepG - shallowG) * depthT) * coverage);
                tile[dst + 2] = (byte)((shallowB + (deepB - shallowB) * depthT) * coverage);
                tile[dst + 3] = maskValue;
            }
        }
        else
        {
            for (var i = 0; i < pixelCount; i++) // Mirror OverlayWater: solid blue, coverage = mask/255.
            {
                var maskValue = mask[i];
                if (maskValue == 0) continue;
                any = true;

                var coverage = maskValue / 255f;
                var dst = i * 4;
                tile[dst] = (byte)(WaterR * coverage);
                tile[dst + 1] = (byte)(WaterG * coverage);
                tile[dst + 2] = (byte)(WaterB * coverage);
                tile[dst + 3] = maskValue;
            }
        }

        return any;
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
        return WriteWaterTilePixels(rgba, waterMask, width * height, waterPalette)
            ? new LayerBitmap(rgba, width, height, minX, maxY)
            : null;
    }

    private static DecodedTerrainCell? GetNeighborTerrain(
        IReadOnlyDictionary<(int gx, int gy), CellRecord> cellByGrid,
        int gx, int gy,
        WorldRenderCache? cache)
    {
        if (!cellByGrid.TryGetValue((gx, gy), out var neighborCell)) return null;
        return cache?.GetTerrain(neighborCell) ?? DecodedTerrainCell.Decode(neighborCell);
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
    ///     Two-color water overlay: lerp Shallow→Deep by the (blurred) water-mask intensity,
    ///     then lerp terrain→that color by the same intensity. Mask interiors saturate around
    ///     ~180 (the planted "below water" value before blur), so dividing by that gives a
    ///     full Shallow→Deep range at interior vs shoreline. Coverage uses /255 to preserve
    ///     the existing shoreline softness from <see cref="OverlayWater" />.
    ///     <para>
    ///         From the runtime decompile of <c>TESWaterSystem::UpdateWaterShaderProperties</c>:
    ///         the pixel shader picks between Shallow and Deep based on the view depth (Fog
    ///         depth params), then mixes Reflection over via Fresnel. We can't reproduce the
    ///         Fresnel/Reflection pass at overview scale, but Shallow→Deep-by-coverage gives
    ///         a perceptually-close result and uses values straight off the WATR record.
    ///     </para>
    /// </summary>
    private static void OverlayWaterColored(
        byte[] rgba, byte[] waterMask, int width, int height, WaterColorPalette colors)
    {
        const float MaskInteriorMax = 180f; // BuildLowResWaterMaskWithNeighbors plants 180 pre-blur

        var pixelCount = width * height;
        var shallowR = colors.Shallow.R; var shallowG = colors.Shallow.G; var shallowB = colors.Shallow.B;
        var deepR = colors.Deep.R; var deepG = colors.Deep.G; var deepB = colors.Deep.B;

        for (var i = 0; i < pixelCount; i++)
        {
            var maskValue = waterMask[i];
            if (maskValue == 0) continue;

            // Depth proxy: full mask = interior = Deep; partial = shoreline = mostly Shallow.
            // Clamp >1 so blur values that happen to exceed the planted 180 (none today, but
            // future kernel changes might) don't push the lerp past the Deep endpoint.
            var depthT = maskValue / MaskInteriorMax;
            if (depthT > 1f) depthT = 1f;

            var waterR = shallowR + (deepR - shallowR) * depthT;
            var waterG = shallowG + (deepG - shallowG) * depthT;
            var waterB = shallowB + (deepB - shallowB) * depthT;

            // Coverage: same /255 mask-to-alpha as the solid-tint fallback, so shoreline AA
            // looks identical to the pre-DNAM path — only the in-water tint color changed.
            var coverage = maskValue / 255f;
            var dst = i * 4;
            rgba[dst] = (byte)(rgba[dst] + (waterR - rgba[dst]) * coverage);
            rgba[dst + 1] = (byte)(rgba[dst + 1] + (waterG - rgba[dst + 1]) * coverage);
            rgba[dst + 2] = (byte)(rgba[dst + 2] + (waterB - rgba[dst + 2]) * coverage);
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
