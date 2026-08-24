using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu;

namespace BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Plans the terrain cell cache's GPU byte budget — the bound that did not exist. The cell LRU's
///     entry capacity is sized at or above its worldspace's cell count (so residency never thrashes
///     on small maps), which means entry count bounds NOTHING: on Fallout 76's Appalachia the
///     eligible pool is 41,219 cells × ~2.25 MiB ≈ 90.6 GiB. This policy converts the adapter's real
///     VRAM budget into a per-worldspace byte cap with a hard floor under it.
///     <para>
///         <b>The floor is correctness, not tuning.</b> An over-budget eviction pass that reached
///         into the visible ring would evict a cell the camera is looking at the moment a new one is
///         built — a guaranteed, reproducible flicker. The floor keeps the cap at or above the
///         retained working set (camera ring + the up-sun shadow-caster margin, whose cells are
///         deliberately NOT recency-bumped by their draws), so enforcement can only ever reach
///         genuinely distant cells. When even the floor exceeds the VRAM share, the correct answer
///         under a no-LOD policy is to accept being over budget — never to shrink the visible world.
///     </para>
///     <para>
///         Pure and GUI-free (sibling of <see cref="TerrainTextureSetWarmPolicy" />) so the renderer
///         and the tests share one arithmetic. Estimates use the CHARGED committed-buffer size via
///         <see cref="GpuResourceFootprint" /> — at a 33-grid, 43% of a cell is placement padding,
///         and a budget computed from the optimistic size would over-commit by exactly that.
///     </para>
/// </summary>
internal static class TerrainCellResidencyPolicy
{
    /// <summary>
    ///     Share of the adapter's local budget terrain may plan for. Terrain is the largest single
    ///     consumer but must leave room for reference geometry, textures, shadow cascades and render
    ///     targets. 0.35 mirrors the proportions observed in profiler captures of dense scenes.
    /// </summary>
    public const double TerrainShare = 0.35;

    /// <summary>
    ///     Safety margin multiplier for the LRU's own byte cap when it is used as a backstop behind
    ///     an explicit distance-ordered sweep. The sweep is the primary enforcer (it knows camera
    ///     distance and pins the retained ring); the LRU cascade is blind LRU order, and terrain's
    ///     shadow pass deliberately reads via TryPeek (no recency bump), so an up-sun caster can sit
    ///     at the LRU tail while actively shadowing. Giving the cascade 25% headroom over the plan
    ///     means it only ever fires if the sweep somehow failed — a genuine backstop, not a second
    ///     enforcer fighting the first.
    /// </summary>
    public const double BackstopHeadroom = 1.25;

    /// <summary>
    ///     Bytes per vertex in the interleaved height/normal/colour stream. Tracks
    ///     <see cref="TerrainVertex.SizeInBytes" /> — it was 72 while terrain rode the shared
    ///     <c>GpuVertex</c>, then 20, and a predictor left behind at an old width would over-estimate
    ///     every cell and leave the byte bound permanently unable to fire.
    /// </summary>
    public const int VertexStreamBytesPerVertex = TerrainVertex.SizeInBytes;

    /// <summary>
    ///     Bytes per vertex in the blend-weight stream: <c>CellTerrainTextureSet.SlotVectors</c> (4)
    ///     × <c>sizeof(Vector4)</c>.
    /// </summary>
    public const int BlendStreamBytesPerVertex = 64;

    /// <summary>
    ///     What one cell actually charges: a single ARENA sub-allocation holding both streams.
    ///     <para>
    ///         Must stay in step with how <c>GpuTerrainArena12</c> allocates — it does, because both
    ///         go through <see cref="GpuResourceFootprint.ArenaSubAllocationBytes" />. It previously
    ///         used the committed-buffer rule, which was correct while each cell owned two committed
    ///         buffers and became a ~128 KiB-per-cell over-estimate the moment the arena landed. That
    ///         mismatch would have left the planned budget permanently above real residency, so the
    ///         byte bound would never have evicted a single cell while still logging a budget.
    ///     </para>
    /// </summary>
    public static long EstimateCellGpuBytes(int gridSize)
    {
        if (gridSize <= 0)
        {
            return 0;
        }

        long vertices = (long)gridSize * gridSize;
        return GpuResourceFootprint.ArenaSubAllocationBytes(
            vertices * VertexStreamBytesPerVertex, vertices * BlendStreamBytesPerVertex);
    }

    /// <summary>
    ///     Cells the retained ring can hold: the (2r+1)² square around the camera cell. The renderer
    ///     retains a one-cell draw-only margin beyond the streaming radius, which its retain radius
    ///     already includes.
    /// </summary>
    public static long MinimumResidentCells(int retainRadiusCells)
    {
        if (retainRadiusCells < 0)
        {
            retainRadiusCells = 0;
        }

        var side = 2L * retainRadiusCells + 1L;
        return side * side;
    }

    /// <summary>
    ///     The planned byte budget:
    ///     <c>clamp(localBudget × TerrainShare, floor, wholeWorldspace)</c>, where the floor is
    ///     <see cref="MinimumResidentCells" /> × <see cref="EstimateCellGpuBytes" /> and the upper
    ///     clamp stops a small worldspace from being handed a budget it can never spend. Returns 0
    ///     (meaning: apply NO byte bound) when the VRAM budget is unknown or degenerate — on
    ///     WARP/iGPU adapters the local budget can be 0, and treating that as "budget 0, evict
    ///     everything" would blank the terrain on exactly the machines least able to afford rebuild
    ///     churn.
    /// </summary>
    public static long PlanBudgetBytes(long localBudgetBytes, int gridSize, int cellCount, int retainRadiusCells)
    {
        if (localBudgetBytes <= 0 || gridSize <= 0 || cellCount <= 0)
        {
            return 0;
        }

        var perCell = EstimateCellGpuBytes(gridSize);
        var wholeWorldspace = perCell * cellCount;
        var floor = perCell * Math.Min(MinimumResidentCells(retainRadiusCells), cellCount);
        var share = (long)(localBudgetBytes * TerrainShare);
        return Math.Clamp(share, floor, Math.Max(floor, wholeWorldspace));
    }
}
