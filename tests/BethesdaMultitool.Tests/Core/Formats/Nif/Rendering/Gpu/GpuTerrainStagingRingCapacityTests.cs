using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Terrain;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     Pins how the shared terrain staging ring sizes itself. The ring is permanently-resident
///     upload-heap memory, so it has to be big enough that the common case never falls back to
///     per-cell committed buffers, and small enough that it never costs more than the transient
///     buffers it replaces ever peaked at.
/// </summary>
public sealed class GpuTerrainStagingRingCapacityTests
{
    private const long Mib = 1024 * 1024;

    [Fact]
    public void A_degenerate_upload_size_plans_no_ring() =>
        Assert.Equal(0, GpuTerrainStagingRing12.PlanCapacityBytes(0));

    [Fact]
    public void A_negative_upload_size_plans_no_ring() =>
        Assert.Equal(0, GpuTerrainStagingRing12.PlanCapacityBytes(-1));

    [Fact]
    public void A_small_grid_gets_the_floor_rather_than_a_ring_too_small_to_absorb_a_frame()
    {
        // 33-grid (Fallout/Oblivion/Skyrim): 47,920 × 8 × 3 = 1.1 MiB, well under the floor.
        var planned = GpuTerrainStagingRing12.PlanCapacityBytes(TerrainCellResidencyPolicy.EstimateCellGpuBytes(33));

        Assert.Equal(GpuTerrainStagingRing12.MinCapacityBytes, planned);
        Assert.True(planned >= 8 * TerrainCellResidencyPolicy.EstimateCellGpuBytes(33),
            "the floor must still absorb a full frame of build starts");
    }

    [Theory]
    [InlineData(33)]
    [InlineData(65)]
    [InlineData(129)]
    public void Every_supported_grid_gets_a_ring_that_absorbs_a_frame_without_exceeding_the_ceiling(int gridSize)
    {
        // The property, rather than a specific figure: the sizing must cover a full frame of build
        // starts (or the ring overflows constantly and buys nothing) and must never reserve more
        // permanently-resident staging than the transient buffers it replaced ever peaked at.
        // Which side of the clamp a given grid lands on is not the contract and has already moved
        // once — before the vertex shrink, the 129 grid asked for 51.8 MiB and was capped.
        var cellBytes = TerrainCellResidencyPolicy.EstimateCellGpuBytes(gridSize);

        var planned = GpuTerrainStagingRing12.PlanCapacityBytes(cellBytes);

        Assert.True(planned >= GpuTerrainStagingRing12.UploadsPerFrameAllowance * cellBytes,
            $"grid {gridSize}: {planned} cannot absorb a frame of {cellBytes}-byte cells");
        Assert.InRange(planned, GpuTerrainStagingRing12.MinCapacityBytes, GpuTerrainStagingRing12.MaxCapacityBytes);
    }

    [Fact]
    public void A_mid_sized_upload_gets_a_ring_proportional_to_it()
    {
        // 512 KiB × 8 × 3 = 12 MiB: between the floor and the ceiling, so the plan is the product.
        const long upload = 512 * 1024;

        Assert.Equal(12 * Mib, GpuTerrainStagingRing12.PlanCapacityBytes(upload));
    }

    [Fact]
    public void An_upload_far_beyond_the_ceiling_still_plans_the_ceiling_without_overflowing()
    {
        // The multiply must not wrap on a nonsense request and produce a tiny (or negative) plan —
        // that would silently disable staging for every cell rather than for the outsized one.
        Assert.Equal(GpuTerrainStagingRing12.MaxCapacityBytes, GpuTerrainStagingRing12.PlanCapacityBytes(long.MaxValue));
        Assert.Equal(GpuTerrainStagingRing12.MaxCapacityBytes, GpuTerrainStagingRing12.PlanCapacityBytes(long.MaxValue / 3));
    }

    [Fact]
    public void The_plan_grows_monotonically_with_the_upload_size()
    {
        var previous = 0L;
        foreach (var kib in new[] { 64, 128, 256, 512, 1024, 2048, 4096, 8192 })
        {
            var planned = GpuTerrainStagingRing12.PlanCapacityBytes(kib * 1024L);
            Assert.True(planned >= previous, $"plan shrank at {kib} KiB: {planned} < {previous}");
            previous = planned;
        }
    }

    [Fact]
    public void A_region_must_outlive_every_frame_the_deletion_queue_holds_it_for()
    {
        // The ring is sized for this many frames of uploads outstanding at once. If it were sized
        // for fewer than the deletion queue holds, the ring would be full of regions awaiting
        // retirement on exactly the frames a flythrough needs it most, and every upload would take
        // the overflow path — a ring that costs memory and buys nothing.
        Assert.True(GpuTerrainStagingRing12.GenerationsHeld > GpuCommandRecorder12.FramesInFlight);
    }

    [Fact]
    public void The_floor_and_ceiling_leave_a_usable_range()
    {
        Assert.True(GpuTerrainStagingRing12.MinCapacityBytes > 0);
        Assert.True(GpuTerrainStagingRing12.MaxCapacityBytes > GpuTerrainStagingRing12.MinCapacityBytes);
        // Above ~64 MiB of permanently-resident staging the ring stops being an optimisation.
        Assert.True(GpuTerrainStagingRing12.MaxCapacityBytes <= 64 * Mib);
    }
}
