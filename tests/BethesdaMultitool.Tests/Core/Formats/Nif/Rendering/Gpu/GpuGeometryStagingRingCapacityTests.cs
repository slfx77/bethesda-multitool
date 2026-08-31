using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class GpuGeometryStagingRingCapacityTests
{
    private const long Mib = 1024 * 1024;

    [Fact]
    public void Default_backing_mode_preserves_the_existing_upload_heap_path() =>
        Assert.Equal(GpuGeometryArenaBackingMode.UploadHeap, default(GpuGeometryArenaBackingMode));

    [Theory]
    [InlineData("default")]
    [InlineData(" DEFAULT ")]
    [InlineData("Default")]
    public void Only_the_explicit_default_token_selects_device_local_backing(string value) =>
        Assert.Equal(GpuGeometryArenaBackingMode.DefaultHeap,
            GpuGeometryArenaBackingModePolicy.Parse(value));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("upload")]
    [InlineData("1")]
    [InlineData("enabled")]
    [InlineData("default-heap")]
    public void Unset_upload_and_unknown_tokens_fail_closed_to_upload_backing(string? value) =>
        Assert.Equal(GpuGeometryArenaBackingMode.UploadHeap,
            GpuGeometryArenaBackingModePolicy.Parse(value));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Degenerate_requests_plan_no_ring(long bytes) =>
        Assert.Equal(0, GpuGeometryStagingRing12.PlanCapacityBytes(bytes));

    [Fact]
    public void Small_meshes_get_the_floor_needed_for_several_frames_of_traffic() =>
        Assert.Equal(GpuGeometryStagingRing12.MinCapacityBytes,
            GpuGeometryStagingRing12.PlanCapacityBytes(64 * 1024));

    [Fact]
    public void Mid_sized_meshes_plan_three_generations() =>
        Assert.Equal(24 * Mib, GpuGeometryStagingRing12.PlanCapacityBytes(8 * Mib));

    [Fact]
    public void Large_and_overflowing_requests_never_exceed_the_permanent_ceiling()
    {
        Assert.Equal(GpuGeometryStagingRing12.MaxCapacityBytes,
            GpuGeometryStagingRing12.PlanCapacityBytes(32 * Mib));
        Assert.Equal(GpuGeometryStagingRing12.MaxCapacityBytes,
            GpuGeometryStagingRing12.PlanCapacityBytes(long.MaxValue));
    }

    [Fact]
    public void Plan_is_monotonic_and_always_within_the_bounded_range()
    {
        var previous = 0L;
        foreach (var mib in new[] { 1, 2, 4, 8, 16, 32, 64, 128 })
        {
            var planned = GpuGeometryStagingRing12.PlanCapacityBytes(mib * Mib);
            Assert.InRange(planned,
                GpuGeometryStagingRing12.MinCapacityBytes,
                GpuGeometryStagingRing12.MaxCapacityBytes);
            Assert.True(planned >= previous, $"plan shrank at {mib} MiB");
            previous = planned;
        }
    }

    [Fact]
    public void Capacity_covers_more_generations_than_the_recorder_keeps_in_flight()
    {
        Assert.True(GpuGeometryStagingRing12.GenerationsHeld > GpuCommandRecorder12.FramesInFlight);
        Assert.True(GpuGeometryStagingRing12.MaxCapacityBytes <= 64 * Mib);
    }
}
