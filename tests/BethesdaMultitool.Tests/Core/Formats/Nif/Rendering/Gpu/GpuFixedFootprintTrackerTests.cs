using BethesdaMultitool.Core.Diagnostics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

/// <summary>
///     The fixed-footprint tracker is the accounting home for GPU allocations that used to appear
///     nowhere (upload ring, shadow cascades, render targets). Its arithmetic must be boring and
///     exact — the residency governor steers on the gap between the DXGI budget and these numbers.
/// </summary>
public sealed class GpuFixedFootprintTrackerTests
{
    [Fact]
    public void Add_and_dispose_adjust_the_total_symmetrically()
    {
        var tracker = new GpuFixedFootprintTracker12(GpuMemorySegment.Local);

        var ring = tracker.Add("ring", 256L * 1024 * 1024);
        var shadows = tracker.Add("shadows", 64L * 1024 * 1024);
        Assert.Equal(320L * 1024 * 1024, tracker.GetStats().EstimatedBytes);
        Assert.Equal(2, tracker.GetStats().EntryCount);

        ring.Dispose();
        Assert.Equal(64L * 1024 * 1024, tracker.GetStats().EstimatedBytes);
        Assert.Equal(1, tracker.GetStats().EntryCount);

        shadows.Dispose();
        Assert.Equal(0, tracker.GetStats().EstimatedBytes);
        Assert.Equal(0, tracker.GetStats().EntryCount);
    }

    [Fact]
    public void Double_dispose_is_a_no_op()
    {
        // Resize/teardown paths overlap (the swap-chain's failure arm releases what Dispose would
        // also release), so a handle disposed twice must not subtract twice and go negative.
        var tracker = new GpuFixedFootprintTracker12(GpuMemorySegment.Local);
        var a = tracker.Add("a", 100);
        tracker.Add("b", 50);

        a.Dispose();
        a.Dispose();

        Assert.Equal(50, tracker.GetStats().EstimatedBytes);
        Assert.Equal(1, tracker.GetStats().EntryCount);
    }

    [Fact]
    public void Stats_carry_the_segment_the_tracker_was_built_for()
    {
        Assert.Equal(
            GpuMemorySegment.NonLocal,
            new GpuFixedFootprintTracker12(GpuMemorySegment.NonLocal).GetStats().Segment);
        Assert.Equal(ResourceCategory.GpuResident,
            new GpuFixedFootprintTracker12(GpuMemorySegment.Local).Category);
    }

    [Fact]
    public void Non_positive_sizes_register_as_zero_byte_entries()
    {
        // A degenerate target (0-wide window during startup) is caller state, not an error; it must
        // neither throw nor poison the total.
        var tracker = new GpuFixedFootprintTracker12(GpuMemorySegment.Local);
        var handle = tracker.Add("degenerate", -1);

        Assert.Equal(0, tracker.GetStats().EstimatedBytes);
        Assert.Equal(1, tracker.GetStats().EntryCount);
        handle.Dispose();
        Assert.Equal(0, tracker.GetStats().EntryCount);
    }
}
