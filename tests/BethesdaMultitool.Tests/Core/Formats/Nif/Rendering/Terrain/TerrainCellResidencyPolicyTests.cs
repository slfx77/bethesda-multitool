using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Terrain;

/// <summary>
///     Pins the terrain byte-budget plan. Expected values are computed here from the vertex/blend
///     stream formats and the arena's 16-byte sub-region rule — never by calling the policy, which
///     would make the assertions agree with any behaviour the policy happened to have.
///     <para>
///         These values have now changed three times, for the same reason each time — the policy
///         predicts what the renderer charges, so it moves whenever the renderer's allocation or its
///         vertex format does. A 33-grid cell was 262,144 bytes under per-cell committed buffers,
///         148,112 once those became arena sub-allocations, 91,488 once the vertex narrowed from the
///         shared 72-byte GpuVertex to a 20-byte TerrainVertex, and 82,768 once world X/Y left the
///         vertex for the shader. A predictor left behind at any of those would keep the planned
///         budget above real residency, and the byte bound would never evict a cell while still
///         logging a healthy-looking budget.
///     </para>
/// </summary>
public sealed class TerrainCellResidencyPolicyTests
{
    // One arena sub-allocation per cell: align16(verts × 12) + verts × 64, whole range align16.
    private const long Cell33 = 82_768; // 1089 verts: 13,068→13,072 + 69,696
    private const long Cell65 = 321_104; // 4225 verts: 50,700→50,704 + 270,400
    private const long Cell129 = 1_264_720; // 16,641 verts: 199,692→199,696 + 1,065,024

    [Theory]
    [InlineData(33, Cell33)]
    [InlineData(65, Cell65)]
    [InlineData(129, Cell129)]
    public void EstimateCellGpuBytes_charges_one_arena_suballocation_for_both_streams(
        int gridSize, long expected) =>
        Assert.Equal(expected, TerrainCellResidencyPolicy.EstimateCellGpuBytes(gridSize));

    [Fact]
    public void EstimateCellGpuBytes_is_far_below_the_old_committed_buffer_charge()
    {
        // Guards the regression direction that matters: if this ever climbs back toward the
        // committed-buffer figure, the predictor has drifted away from the arena and the bound is
        // inert. 33-grid committed cost was 4 × 64 KiB = 262,144, and the shared-GpuVertex arena
        // charge was 148,112. The blend-weight stream now dominates what remains (Phase 3c).
        Assert.True(TerrainCellResidencyPolicy.EstimateCellGpuBytes(33) < 148_112 * 0.6);
    }

    [Fact]
    public void A_degenerate_grid_costs_nothing()
    {
        Assert.Equal(0, TerrainCellResidencyPolicy.EstimateCellGpuBytes(0));
        Assert.Equal(0, TerrainCellResidencyPolicy.EstimateCellGpuBytes(-33));
    }

    [Theory]
    [InlineData(0, 1L)] // the camera's own cell
    [InlineData(1, 9L)]
    [InlineData(10, 441L)]
    [InlineData(-5, 1L)] // negative radius clamps to the single cell, never to a negative area
    public void MinimumResidentCells_is_the_square_ring(int retainRadius, long expected) =>
        Assert.Equal(expected, TerrainCellResidencyPolicy.MinimumResidentCells(retainRadius));

    [Fact]
    public void An_unknown_vram_budget_means_no_bound_rather_than_evict_everything()
    {
        // WARP / iGPU / a failed IDXGIAdapter3 query all surface as a non-positive local budget.
        // Returning a tiny budget there would blank terrain on exactly the machines least able to
        // afford rebuild churn, so 0 must mean "apply no byte bound".
        Assert.Equal(0, TerrainCellResidencyPolicy.PlanBudgetBytes(0, 33, 5_000, 10));
        Assert.Equal(0, TerrainCellResidencyPolicy.PlanBudgetBytes(-1, 33, 5_000, 10));
        Assert.Equal(0, TerrainCellResidencyPolicy.PlanBudgetBytes(8L << 30, 33, 0, 10));
    }

    [Fact]
    public void The_plan_is_the_vram_share_when_it_sits_between_the_floor_and_the_worldspace()
    {
        // 8 GiB × 0.35 = 3,006,477,107 B. Against a 33-grid Appalachia-sized worldspace the floor is
        // 441 × 82,768 = 34.8 MiB and the whole worldspace is 41,219 × 82,768 = 3.18 GiB, so the
        // share genuinely falls between them and is what gets planned.
        const long budget = 8L << 30;
        var expected = (long)(budget * TerrainCellResidencyPolicy.TerrainShare);
        Assert.True(expected > 441L * Cell33 && expected < 41_219L * Cell33, "scenario premise broken");

        Assert.Equal(expected, TerrainCellResidencyPolicy.PlanBudgetBytes(budget, 33, 41_219, 10));
    }

    [Fact]
    public void The_floor_wins_over_a_small_vram_share()
    {
        // 441 retained 129-grid cells cost 441 × 1,264,720 = 531.9 MiB. A 1 GiB adapter's 35% share
        // is 358 MiB — below it. Planning the share would guarantee the sweep evicted visible
        // terrain the instant a new cell was built, which is the flicker the floor exists to stop.
        var floor = 441L * Cell129;

        var plan = TerrainCellResidencyPolicy.PlanBudgetBytes(1L << 30, 129, 41_219, 10);

        Assert.Equal(floor, plan);
        Assert.True(plan > (long)((1L << 30) * TerrainCellResidencyPolicy.TerrainShare));
    }

    [Fact]
    public void The_plan_never_exceeds_the_whole_worldspace()
    {
        // A 24 GiB adapter against a 200-cell interior-ish worldspace: no point reserving more
        // than every cell it has.
        const int cells = 200;

        Assert.Equal(cells * Cell33, TerrainCellResidencyPolicy.PlanBudgetBytes(24L << 30, 33, cells, 10));
    }

    [Fact]
    public void A_worldspace_smaller_than_the_retain_ring_floors_at_its_own_cell_count()
    {
        // 9 cells with a radius-10 ring: the floor must not claim 441 cells' worth of budget for a
        // worldspace that only has 9 (which would also push the plan above the whole worldspace).
        var plan = TerrainCellResidencyPolicy.PlanBudgetBytes(1L << 20, 33, 9, 10);

        Assert.Equal(9L * Cell33, plan);
    }

    [Fact]
    public void The_plan_grows_monotonically_with_available_vram()
    {
        var previous = 0L;
        foreach (var gib in new[] { 1, 2, 4, 8, 12, 16, 24 })
        {
            var plan = TerrainCellResidencyPolicy.PlanBudgetBytes((long)gib << 30, 129, 41_219, 10);
            Assert.True(plan >= previous, $"plan shrank at {gib} GiB: {plan} < {previous}");
            previous = plan;
        }
    }

    [Fact]
    public void The_backstop_headroom_sits_above_the_plan_but_stays_bounded()
    {
        // The LRU cascade is the backstop behind the distance sweep; it must not fire first (>1)
        // and must not be so loose it stops bounding anything.
        Assert.True(TerrainCellResidencyPolicy.BackstopHeadroom > 1.0);
        Assert.True(TerrainCellResidencyPolicy.BackstopHeadroom <= 1.5);

        // Terrain must claim a real share but leave room for reference geometry, textures, shadow
        // cascades and render targets. (Asserting `share is > 0 and < 1` on a const the compiler can
        // fold is a tautology — CS8793 — so bound it where the value actually has to live.)
        Assert.InRange(TerrainCellResidencyPolicy.TerrainShare, 0.15, 0.60);
    }
}
