using FalloutXbox360Utils.Core.Formats.Esm.Models.World;

namespace FalloutXbox360Utils.Core.Formats.Nif.Rendering.Textures;

/// <summary>
///     A single <c>(LTEX FormID, weight)</c> pair stored on a cell vertex.
///     <c>FormID == 0</c> is the engine-default sentinel; see
///     <see cref="CellLayerWeightTable.EngineDefaultSentinelFormId" />.
/// </summary>
public readonly record struct LayerWeight(uint FormId, float Weight);

/// <summary>
///     Per-vertex sparse list of <see cref="LayerWeight" />. Four entries inline cover the
///     common interior case (one quadrant's BTXT + up to three of its ATXTs); the cell
///     center and shared edges may carry more, so a heap-backed overflow array is allocated
///     lazily when the count exceeds four.
/// </summary>
public struct VertexWeights
{
    // Public fields, not properties: VertexWeights is a mutable value type whose Add() mutates these
    // in place through array slots (table.Vertices[i].Add(...)). Encapsulating as properties risks
    // silent struct copies on a hot path. CA1051 has the canonical exclude_structs option for this;
    // S1104 has no struct exemption, so it's suppressed here.
#pragma warning disable S1104
    public LayerWeight E0, E1, E2, E3;
    public byte Count;
    public LayerWeight[]? Overflow;
#pragma warning restore S1104

    public void Add(uint formId, float weight)
    {
        if (weight <= 0f) return;

        // If the same FormID already has an entry, merge in place — happens at shared edge
        // vertices when two quadrants reference the same BTXT.
        if (Count > 0 && E0.FormId == formId) { E0 = new LayerWeight(formId, E0.Weight + weight); return; }
        if (Count > 1 && E1.FormId == formId) { E1 = new LayerWeight(formId, E1.Weight + weight); return; }
        if (Count > 2 && E2.FormId == formId) { E2 = new LayerWeight(formId, E2.Weight + weight); return; }
        if (Count > 3 && E3.FormId == formId) { E3 = new LayerWeight(formId, E3.Weight + weight); return; }
        if (Overflow is not null)
        {
            var n = Count - 4;
            for (var i = 0; i < n; i++)
            {
                if (Overflow[i].FormId == formId)
                {
                    Overflow[i] = new LayerWeight(formId, Overflow[i].Weight + weight);
                    return;
                }
            }
        }

        switch (Count)
        {
            case 0: E0 = new LayerWeight(formId, weight); Count = 1; return;
            case 1: E1 = new LayerWeight(formId, weight); Count = 2; return;
            case 2: E2 = new LayerWeight(formId, weight); Count = 3; return;
            case 3: E3 = new LayerWeight(formId, weight); Count = 4; return;
            default:
                var overflowIndex = Count - 4;
                if (Overflow is null)
                {
                    Overflow = new LayerWeight[4];
                }
                else if (overflowIndex >= Overflow.Length)
                {
                    Array.Resize(ref Overflow, Overflow.Length * 2);
                }
                Overflow[overflowIndex] = new LayerWeight(formId, weight);
                Count++;
                return;
        }
    }

    public readonly float TotalWeight()
    {
        var sum = 0f;
        if (Count > 0) sum += E0.Weight;
        if (Count > 1) sum += E1.Weight;
        if (Count > 2) sum += E2.Weight;
        if (Count > 3) sum += E3.Weight;
        if (Overflow is not null)
        {
            var n = Count - 4;
            for (var i = 0; i < n; i++) sum += Overflow[i].Weight;
        }
        return sum;
    }

    public void Scale(float factor)
    {
        if (Count > 0) E0 = new LayerWeight(E0.FormId, E0.Weight * factor);
        if (Count > 1) E1 = new LayerWeight(E1.FormId, E1.Weight * factor);
        if (Count > 2) E2 = new LayerWeight(E2.FormId, E2.Weight * factor);
        if (Count > 3) E3 = new LayerWeight(E3.FormId, E3.Weight * factor);
        if (Overflow is not null)
        {
            var n = Count - 4;
            for (var i = 0; i < n; i++)
            {
                Overflow[i] = new LayerWeight(Overflow[i].FormId, Overflow[i].Weight * factor);
            }
        }
    }
}

/// <summary>
///     Cell-wide 33×33 grid of per-vertex layer-weight lists. Built once per cell from the
///     <c>BTXT/ATXT/VTXT</c> subrecords and consumed by the per-pixel terrain-textures blit.
///     Shared edge vertices (cell column 16, cell row 16) and the cell center carry
///     contributions from 2 or 4 quadrants respectively — that overlap is exactly what kills
///     the BTXT seam at the cell midlines during bilinear interp.
///
///     This mirrors what FNV's <c>NiTerrainLandShader</c> does in the live engine (and what
///     xLODGen replicates offline): a unified per-vertex blend table that the per-pixel pass
///     bilinearly interpolates. The hard-quadrant-boundary layout the 2D overview previously
///     used produced a seam any time adjacent quadrants used different BTXTs; this layout
///     reproduces the engine's smooth cross-quadrant blend.
///
///     Streaming workers reuse a single thread-local instance across cells via
///     <see cref="BuildInto" />, so the ATXT dense grids and the alpha scratch list are
///     pooled on the table itself (zero per-cell GC pressure on the hot path). The static
///     <see cref="Build" /> entry point is preserved as a one-shot allocating wrapper for
///     single-cell callers (tests, cell-detail render).
/// </summary>
public sealed class CellLayerWeightTable
{
    /// <summary>33 — one vertex per LAND grid sample (16 quads per quadrant axis × 2 quadrants + 1 shared edge).</summary>
    public const int CellVertexCount = 33;

    /// <summary>Quadrant-local vertex grid edge length (17×17 per quadrant).</summary>
    public const int QuadSize = 17;

    private const int QuadVertCount = QuadSize * QuadSize;

    /// <summary>
    ///     FormID sentinel used to mark "quadrant has no BTXT — fill the residual with the
    ///     engine-default landscape texture." Zero is safe because FormID 0 is invalid in
    ///     Bethesda's record system. The per-pixel sampler routes this sentinel to the
    ///     palette's DirtWasteland01 fallback.
    /// </summary>
    public const uint EngineDefaultSentinelFormId = 0u;

    public VertexWeights[] Vertices { get; } = new VertexWeights[CellVertexCount * CellVertexCount];

    /// <summary>
    ///     Pooled dense 17×17 opacity grids, one per ATXT. Grown lazily; rebuilt from
    ///     scratch (Array.Clear + sparse fill) on every <see cref="BuildInto" /> so the
    ///     pool's lifetime spans many cells.
    /// </summary>
    private float[][] _atxtDenseGridsPool = new float[8][];

    /// <summary>
    ///     Pooled alpha-layer scratch list reused across quadrants and across calls. Cleared
    ///     by <see cref="QuadrantLayerSelector.SelectBaseAndAlphas" /> before each refill.
    /// </summary>
    private readonly List<LandTextureLayer> _alphaScratch = new(4);

    public ref VertexWeights At(int vx, int vy) => ref Vertices[vy * CellVertexCount + vx];

    /// <summary>
    ///     Clear the vertex grid so this instance can be reused by <see cref="BuildInto" />.
    ///     The ATXT dense-grid pool is not cleared here — each grid is cleared on rent in
    ///     <see cref="PopulateAtxtGrids" /> so we don't pay for grid slots that aren't reused
    ///     in the next build.
    /// </summary>
    public void Reset()
    {
        Array.Clear(Vertices, 0, Vertices.Length);
    }

    /// <summary>
    ///     Fill pool slots 0..<c>alphas.Count-1</c> with sparse-to-dense 17×17 opacity grids
    ///     for each ATXT in <paramref name="alphas" />. Each grid is cleared before fill,
    ///     so positions absent from the layer's <c>BlendEntries</c> read as zero. Callers
    ///     read from <see cref="_atxtDenseGridsPool" /> directly to avoid method-call
    ///     overhead in the per-vertex inner loop.
    /// </summary>
    private void PopulateAtxtGrids(List<LandTextureLayer> alphas)
    {
        if (alphas.Count > _atxtDenseGridsPool.Length)
        {
            Array.Resize(
                ref _atxtDenseGridsPool,
                Math.Max(alphas.Count, _atxtDenseGridsPool.Length * 2));
        }
        for (var i = 0; i < alphas.Count; i++)
        {
            var grid = _atxtDenseGridsPool[i] ??= new float[QuadVertCount];
            Array.Clear(grid, 0, grid.Length);
            foreach (var entry in alphas[i].BlendEntries)
            {
                if (entry.Position < QuadVertCount)
                {
                    grid[entry.Position] = entry.Opacity;
                }
            }
        }
    }

    /// <summary>
    ///     Build a <see cref="CellLayerWeightTable" /> from a cell's LAND texture layers. For
    ///     each quadrant we walk its 17×17 vertices, map them into the cell-wide 33×33 grid,
    ///     and append <c>(FormID, weight)</c> entries: one per ATXT with non-zero VTXT opacity
    ///     at that vertex, plus the quadrant's BTXT with the residual weight
    ///     (<c>1 - Σ ATXT opacity</c>). When a quadrant has no BTXT, the residual is filled
    ///     with the engine-default sentinel so the per-pixel path can fall back to
    ///     DirtWasteland01 (matching the prior per-quadrant behavior). Returns null when no
    ///     quadrant contributed anything.
    ///
    ///     After all four quadrants are processed, each cell vertex's weights are renormalized
    ///     to sum to 1. Shared edge vertices typically receive weight=1 from each adjacent
    ///     quadrant's BTXT (when no ATXTs cover that vertex) for a raw sum of 2 → renormalized
    ///     to 0.5/0.5; bilinear interp during sampling then gives the smooth boundary fade.
    ///
    ///     Pass the four directional neighbor cells' layer lists to extend the same blend
    ///     ACROSS cell boundaries. The shared edge vertices (vx=0/32 and vy=0/32) accumulate
    ///     contributions from the neighbor's adjacent edge quadrants, producing a smooth
    ///     cross-cell fade instead of the prior hard cell-boundary seam. With the diagonal
    ///     neighbor missing, corner vertices receive contributions from 3 of 4 surrounding
    ///     cells — close enough to the engine's behavior; the diagonal contribution is small
    ///     in practice because adjacent cells usually share BTXTs at boundaries.
    ///
    ///     Allocating one-shot entry point. Streaming workers should call
    ///     <see cref="BuildInto" /> on a pooled instance instead.
    /// </summary>
    public static CellLayerWeightTable? Build(
        IReadOnlyList<LandTextureLayer> layers,
        IReadOnlyList<LandTextureLayer>? eastNeighborLayers = null,
        IReadOnlyList<LandTextureLayer>? westNeighborLayers = null,
        IReadOnlyList<LandTextureLayer>? northNeighborLayers = null,
        IReadOnlyList<LandTextureLayer>? southNeighborLayers = null)
    {
        var table = new CellLayerWeightTable();
        return BuildInto(table, layers, eastNeighborLayers, westNeighborLayers, northNeighborLayers, southNeighborLayers)
            ? table
            : null;
    }

    /// <summary>
    ///     Pooled entry point. Resets <paramref name="table" /> and refills it from the same
    ///     inputs as <see cref="Build" />. Returns <c>true</c> when at least one vertex
    ///     received a contribution; <c>false</c> when the inputs produced an empty table
    ///     (mirrors <see cref="Build" />'s null-return semantics).
    /// </summary>
    public static bool BuildInto(
        CellLayerWeightTable table,
        IReadOnlyList<LandTextureLayer> layers,
        IReadOnlyList<LandTextureLayer>? eastNeighborLayers = null,
        IReadOnlyList<LandTextureLayer>? westNeighborLayers = null,
        IReadOnlyList<LandTextureLayer>? northNeighborLayers = null,
        IReadOnlyList<LandTextureLayer>? southNeighborLayers = null)
    {
        table.Reset();
        if (layers.Count == 0) return false;

        var alphaScratch = table._alphaScratch;
        var any = false;

        for (var quadrant = 0; quadrant < 4; quadrant++)
        {
            var baseLayer = QuadrantLayerSelector.SelectBaseAndAlphas(layers, quadrant, alphaScratch);
            table.PopulateAtxtGrids(alphaScratch);
            var atxtGrids = table._atxtDenseGridsPool;

            for (var qy = 0; qy < QuadSize; qy++)
            {
                // Cell-wide vy (vy=0 is the cell's north edge). North quadrants (NW=2, NE=3)
                // cover rows 0..16; south quadrants (SW=0, SE=1) cover rows 16..32. Quadrant-
                // local qy=0 is the SW corner of the quadrant (per VTXT Position convention),
                // so within a quadrant qy grows northward.
                var cellVy = quadrant switch
                {
                    2 or 3 => 16 - qy,
                    _ => 32 - qy
                };
                for (var qx = 0; qx < QuadSize; qx++)
                {
                    // Cell-wide vx. East quadrants (SE=1, NE=3) cover columns 16..32.
                    var cellVx = quadrant switch
                    {
                        1 or 3 => 16 + qx,
                        _ => qx
                    };

                    var atxtSum = 0f;
                    for (var i = 0; i < alphaScratch.Count; i++)
                    {
                        var op = atxtGrids[i][qy * QuadSize + qx];
                        if (op <= 0f) continue;
                        if (op > 1f) op = 1f;
                        atxtSum += op;
                        table.At(cellVx, cellVy).Add(alphaScratch[i].TextureFormId, op);
                        any = true;
                    }

                    var baseWeight = 1f - atxtSum;
                    if (baseWeight <= 0f) continue;

                    var baseFormId = baseLayer?.TextureFormId ?? EngineDefaultSentinelFormId;
                    table.At(cellVx, cellVy).Add(baseFormId, baseWeight);
                    any = true;
                }
            }
        }

        if (!any) return false;

        // Accumulate neighbor cell contributions onto the four edges so the cell-to-cell
        // seam fades the same way the intra-cell midline does. Each helper only touches its
        // own edge column/row, so they're independent and the renormalize loop below catches
        // the new sums.
        if (eastNeighborLayers is { Count: > 0 })
        {
            AccumulateNeighborEastEdge(table, eastNeighborLayers);
        }
        if (westNeighborLayers is { Count: > 0 })
        {
            AccumulateNeighborWestEdge(table, westNeighborLayers);
        }
        if (northNeighborLayers is { Count: > 0 })
        {
            AccumulateNeighborNorthEdge(table, northNeighborLayers);
        }
        if (southNeighborLayers is { Count: > 0 })
        {
            AccumulateNeighborSouthEdge(table, southNeighborLayers);
        }

        // Renormalize each vertex so weights sum to 1. Interior vertices already sum to ≤1
        // (a single quadrant's contributions); shared edge / center vertices accumulated
        // contributions from 2 or 4 quadrants and need the rescale to produce the smooth
        // boundary blend.
        for (var i = 0; i < table.Vertices.Length; i++)
        {
            var total = table.Vertices[i].TotalWeight();
            if (total > 0f && MathF.Abs(total - 1f) > 1e-4f)
            {
                table.Vertices[i].Scale(1f / total);
            }
        }

        return true;
    }

    /// <summary>
    ///     Push one (qx, qy) vertex's BTXT + ATXT contributions onto the cell-wide
    ///     <paramref name="table" /> at <c>(cellVx, cellVy)</c>. Common code for the four
    ///     neighbor-edge helpers below; the only direction-specific parts are the qx/qy →
    ///     cellVx/cellVy mapping the caller picks.
    /// </summary>
    private static void AccumulateOneVertex(
        CellLayerWeightTable table,
        int cellVx, int cellVy,
        int qx, int qy,
        LandTextureLayer? baseLayer,
        List<LandTextureLayer> alphas,
        float[][] atxtGrids)
    {
        var atxtSum = 0f;
        for (var i = 0; i < alphas.Count; i++)
        {
            var op = atxtGrids[i][qy * QuadSize + qx];
            if (op <= 0f) continue;
            if (op > 1f) op = 1f;
            atxtSum += op;
            table.At(cellVx, cellVy).Add(alphas[i].TextureFormId, op);
        }
        var baseWeight = 1f - atxtSum;
        if (baseWeight <= 0f) return;
        var baseFormId = baseLayer?.TextureFormId ?? EngineDefaultSentinelFormId;
        table.At(cellVx, cellVy).Add(baseFormId, baseWeight);
    }

    // Per-edge quadrant selectors. Pre-staticed so the per-cell neighbor-accumulate path
    // doesn't allocate four two-element arrays on every BuildInto call.
    private static readonly int[] s_eastEdgeQuads = { 0, 2 };
    private static readonly int[] s_westEdgeQuads = { 1, 3 };
    private static readonly int[] s_northEdgeQuads = { 0, 1 };
    private static readonly int[] s_southEdgeQuads = { 2, 3 };

    /// <summary>
    ///     East neighbor: its west quadrants (SW=0, NW=2) share their western edge column
    ///     (qx=0) with this cell's eastern edge column (cellVx=32). Walk the 17 vertices of
    ///     each west quadrant's west edge and contribute.
    /// </summary>
    private static void AccumulateNeighborEastEdge(
        CellLayerWeightTable table,
        IReadOnlyList<LandTextureLayer> neighborLayers)
    {
        var alphaScratch = table._alphaScratch;
        foreach (var quadrant in s_eastEdgeQuads)
        {
            var baseLayer = QuadrantLayerSelector.SelectBaseAndAlphas(neighborLayers, quadrant, alphaScratch);
            table.PopulateAtxtGrids(alphaScratch);
            var atxtGrids = table._atxtDenseGridsPool;
            for (var qy = 0; qy < QuadSize; qy++)
            {
                var cellVy = quadrant == 0 ? 32 - qy : 16 - qy;
                AccumulateOneVertex(table, cellVx: 32, cellVy: cellVy,
                    qx: 0, qy: qy, baseLayer, alphaScratch, atxtGrids);
            }
        }
    }

    /// <summary>
    ///     West neighbor: its east quadrants (SE=1, NE=3) share their eastern edge column
    ///     (qx=16) with this cell's western edge column (cellVx=0).
    /// </summary>
    private static void AccumulateNeighborWestEdge(
        CellLayerWeightTable table,
        IReadOnlyList<LandTextureLayer> neighborLayers)
    {
        var alphaScratch = table._alphaScratch;
        foreach (var quadrant in s_westEdgeQuads)
        {
            var baseLayer = QuadrantLayerSelector.SelectBaseAndAlphas(neighborLayers, quadrant, alphaScratch);
            table.PopulateAtxtGrids(alphaScratch);
            var atxtGrids = table._atxtDenseGridsPool;
            for (var qy = 0; qy < QuadSize; qy++)
            {
                var cellVy = quadrant == 1 ? 32 - qy : 16 - qy;
                AccumulateOneVertex(table, cellVx: 0, cellVy: cellVy,
                    qx: 16, qy: qy, baseLayer, alphaScratch, atxtGrids);
            }
        }
    }

    /// <summary>
    ///     North neighbor: its south quadrants (SW=0, SE=1) share their southern edge row
    ///     (qy=0) with this cell's northern edge row (cellVy=0).
    /// </summary>
    private static void AccumulateNeighborNorthEdge(
        CellLayerWeightTable table,
        IReadOnlyList<LandTextureLayer> neighborLayers)
    {
        var alphaScratch = table._alphaScratch;
        foreach (var quadrant in s_northEdgeQuads)
        {
            var baseLayer = QuadrantLayerSelector.SelectBaseAndAlphas(neighborLayers, quadrant, alphaScratch);
            table.PopulateAtxtGrids(alphaScratch);
            var atxtGrids = table._atxtDenseGridsPool;
            for (var qx = 0; qx < QuadSize; qx++)
            {
                var cellVx = quadrant == 0 ? qx : 16 + qx;
                AccumulateOneVertex(table, cellVx: cellVx, cellVy: 0,
                    qx: qx, qy: 0, baseLayer, alphaScratch, atxtGrids);
            }
        }
    }

    /// <summary>
    ///     South neighbor: its north quadrants (NW=2, NE=3) share their northern edge row
    ///     (qy=16) with this cell's southern edge row (cellVy=32).
    /// </summary>
    private static void AccumulateNeighborSouthEdge(
        CellLayerWeightTable table,
        IReadOnlyList<LandTextureLayer> neighborLayers)
    {
        var alphaScratch = table._alphaScratch;
        foreach (var quadrant in s_southEdgeQuads)
        {
            var baseLayer = QuadrantLayerSelector.SelectBaseAndAlphas(neighborLayers, quadrant, alphaScratch);
            table.PopulateAtxtGrids(alphaScratch);
            var atxtGrids = table._atxtDenseGridsPool;
            for (var qx = 0; qx < QuadSize; qx++)
            {
                var cellVx = quadrant == 2 ? qx : 16 + qx;
                AccumulateOneVertex(table, cellVx: cellVx, cellVy: 32,
                    qx: qx, qy: 16, baseLayer, alphaScratch, atxtGrids);
            }
        }
    }
}
