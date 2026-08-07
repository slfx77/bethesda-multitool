using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Gpu;

public sealed class GpuTimestampMathTests
{
    [Fact]
    public void TicksToMilliseconds_ConvertsWithQueueFrequency()
    {
        var milliseconds = GpuTimestampMath.TicksToMilliseconds(
            100,
            600,
            1000);

        Assert.Equal(500.0, milliseconds, 6);
    }

    [Theory]
    [InlineData(0ul, 0ul, 1000ul)]
    [InlineData(10ul, 9ul, 1000ul)]
    [InlineData(10ul, 20ul, 0ul)]
    public void TicksToMilliseconds_ReturnsZeroForInvalidRanges(
        ulong start,
        ulong end,
        ulong frequency)
    {
        Assert.Equal(0.0, GpuTimestampMath.TicksToMilliseconds(start, end, frequency));
    }

    [Fact]
    public void QueryIndex_PacksFrameSlotAndRegionSequentially()
    {
        // Derived from the constant rather than hard-coded: the region set grows as passes are
        // instrumented, and the previous literals silently encoded QueryCountPerFrame == 10.
        const uint stride = GpuTimestampProfiler12.QueryCountPerFrame;

        Assert.Equal(0u, GpuTimestampProfiler12.QueryIndex(0, GpuTimestampRegion.FrameStart));
        Assert.Equal(9u, GpuTimestampProfiler12.QueryIndex(0, GpuTimestampRegion.FrameEnd));
        Assert.Equal(stride, GpuTimestampProfiler12.QueryIndex(1, GpuTimestampRegion.FrameStart));
        Assert.Equal(stride + 3u, GpuTimestampProfiler12.QueryIndex(1, GpuTimestampRegion.ReferencesStart));
        Assert.Equal((2u * stride) + 9u, GpuTimestampProfiler12.QueryIndex(2, GpuTimestampRegion.FrameEnd));
    }

    [Fact]
    public void RegionSlots_FitTheWrittenMaskAndTheReservedQueryCount()
    {
        // Every region must address a distinct slot inside QueryCountPerFrame, and the written-slot
        // mask (a uint) must be able to hold one bit per region — a silent overflow there would
        // report stale ticks as real durations.
        var regions = Enum.GetValues<GpuTimestampRegion>();
        Assert.Equal(regions.Length, regions.Distinct().Count());
        Assert.All(regions, r => Assert.InRange((int)r, 0, GpuTimestampProfiler12.QueryCountPerFrame - 1));
        Assert.True(GpuTimestampProfiler12.QueryCountPerFrame <= 32);
    }

    [Fact]
    public void ShadowCascadeSlotTables_AreOrderedAndOnePerCascade()
    {
        // ShadowMapRenderer12.CascadeCount is WINDOWS_GUI-only, so the count is asserted literally
        // here; the two must stay in step (the slot tables are indexed by cascade at the call site).
        const int count = 4;
        Assert.Equal(count, GpuTimestampProfiler12.ShadowCascadeStart.Length);
        Assert.Equal(count, GpuTimestampProfiler12.ShadowCascadeRefs.Length);
        Assert.Equal(count, GpuTimestampProfiler12.ShadowCascadeEnd.Length);

        for (var i = 0; i < count; i++)
        {
            // Start < Refs < End, so Start..Refs is the reference replay and Refs..End the terrain gather.
            Assert.True((int)GpuTimestampProfiler12.ShadowCascadeStart[i] < (int)GpuTimestampProfiler12.ShadowCascadeRefs[i]);
            Assert.True((int)GpuTimestampProfiler12.ShadowCascadeRefs[i] < (int)GpuTimestampProfiler12.ShadowCascadeEnd[i]);
        }
    }
}