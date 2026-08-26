using BethesdaMultitool.Core.Formats.Esm.Land.Btd;
using BethesdaMultitool.Core.Formats.Esm.Models.World;

namespace BethesdaMultitool.Core.Formats.Esm.Land;

/// <summary>
///     Lazy per-cell height decoder over a kept-open <see cref="BtdFile" />. Owns the file and hands
///     out 129×129 exact-height grids on demand through a bounded LRU, so a 40k-cell worldspace
///     (Appalachia ≈ 2.7 GB if fully materialized) costs only the cells actually being looked at:
///     the 3D viewer streams a visible ring, the 2D map's aggregate layers walk decode→downsample→
///     discard, and CLI loads that never touch terrain decode nothing at all.
///     <para>
///         <see cref="BtdFile" /> is thread-affine (its tile cache is unsynchronized) while height
///         consumers run concurrently (3D mesh-build pool tasks, the 2D layer build task, the
///         render-thread walk sampler), so every decode is serialized behind one gate. A decode is a
///         few tile-cache-amortized zlib inflates — milliseconds — so contention stays acceptable.
///     </para>
///     <para>
///         A corrupt tile degrades to a flat zero grid for that cell (cached, so it is not
///         re-attempted), matching the injector's whole-worldspace "leave flat" policy for a BTD
///         that fails to open. Lifetime: created by <c>BtdTerrainInjector.Inject</c>, owned by the
///         load result (<c>UnifiedAnalysisResult</c> / the GUI session), disposed with it.
///     </para>
/// </summary>
internal sealed class BtdHeightSource(BtdFile btd, int blendGridEdge) : IDisposable
{
    /// <summary>
    ///     Cells kept decoded: 512 × ~67 KB ≈ 34 MB — comfortably above the 3D viewer's visible ring
    ///     and the 2D detail/sampler working set, a rounding error next to eager materialization.
    /// </summary>
    private const int MaxCachedCells = 512;

    /// <summary>
    ///     Texture layers kept decoded. Measured ~31 KB per Appalachia cell, so 512 ≈ 16 MB against
    ///     the 1,250 MB the eager sweep retained for the same worldspace.
    /// </summary>
    private const int MaxCachedLayerCells = 512;

    private readonly Dictionary<(int X, int Y), LinkedListNode<CachedCell>> _byCell = new();

    private readonly object _gate = new();
    private readonly Dictionary<(int X, int Y), LinkedListNode<CachedLayers>> _layersByCell = new();
    private readonly LinkedList<CachedLayers> _layersRecency = new();
    private readonly LinkedList<CachedCell> _recency = new();
    private bool _disposed;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _byCell.Clear();
            _recency.Clear();
            _layersByCell.Clear();
            _layersRecency.Clear();
            btd.Dispose();
        }
    }

    /// <summary>
    ///     Returns the cell's 129×129 exact-height grid, decoding it from the BTD on first request.
    ///     The returned array stays valid after cache eviction (eviction only drops the cache's
    ///     reference); a flat zero grid is returned for a cell whose tile data is corrupt, and for
    ///     reads that race past <see cref="Dispose" /> (session teardown vs the render loop).
    /// </summary>
    public float[,] GetExactHeights(int cellX, int cellY)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                // A renderer thread can race session teardown (the 3D frame loop / in-flight mesh
                // builds vs the GUI closing the file, which disposes the injection): degrade to a
                // flat grid instead of throwing across the frame loop. The stale visual lasts only
                // until the viewer's own teardown completes.
                return new float[BtdTerrainInjector.HeightGridSize, BtdTerrainInjector.HeightGridSize];
            }

            if (_byCell.TryGetValue((cellX, cellY), out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                return node.Value.Heights;
            }

            float[,] heights;
            try
            {
                heights = BtdTerrainInjector.BuildExactHeights(btd, cellX, cellY);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                heights = new float[BtdTerrainInjector.HeightGridSize, BtdTerrainInjector.HeightGridSize];
            }

            var added = _recency.AddFirst(new CachedCell((cellX, cellY), heights));
            _byCell[(cellX, cellY)] = added;
            if (_byCell.Count > MaxCachedCells)
            {
                var oldest = _recency.Last!;
                _recency.RemoveLast();
                _byCell.Remove(oldest.Value.Key);
            }

            return heights;
        }
    }

    /// <summary>
    ///     Returns the cell's BTXT/ATXT layers, rebuilding them from the BTD on first request. Shares
    ///     <see cref="GetExactHeights" />'s gate — <see cref="BtdFile" />'s tile cache is unsynchronized
    ///     and both routes read it — and its degradation contract: a corrupt tile yields an empty layer
    ///     list, cached so it is not re-attempted, and a read racing <see cref="Dispose" /> (renderer
    ///     thread vs session teardown) returns empty rather than throwing across the frame loop.
    /// </summary>
    public List<LandTextureLayer> GetTextureLayers(int cellX, int cellY)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return [];
            }

            if (_layersByCell.TryGetValue((cellX, cellY), out var node))
            {
                _layersRecency.Remove(node);
                _layersRecency.AddFirst(node);
                return node.Value.Layers;
            }

            List<LandTextureLayer> layers;
            try
            {
                layers = BtdTerrainInjector.BuildTextureLayers(btd, cellX, cellY, blendGridEdge) ?? [];
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
            {
                layers = [];
            }

            var added = _layersRecency.AddFirst(new CachedLayers((cellX, cellY), layers));
            _layersByCell[(cellX, cellY)] = added;
            if (_layersByCell.Count > MaxCachedLayerCells)
            {
                var oldest = _layersRecency.Last!;
                _layersRecency.RemoveLast();
                _layersByCell.Remove(oldest.Value.Key);
            }

            return layers;
        }
    }

    private sealed record CachedCell((int X, int Y) Key, float[,] Heights);

    private sealed record CachedLayers((int X, int Y) Key, List<LandTextureLayer> Layers);
}
