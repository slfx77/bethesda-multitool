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
        Assert.Equal(0u, GpuTimestampProfiler12.QueryIndex(0, GpuTimestampRegion.FrameStart));
        Assert.Equal(9u, GpuTimestampProfiler12.QueryIndex(0, GpuTimestampRegion.FrameEnd));
        Assert.Equal(10u, GpuTimestampProfiler12.QueryIndex(1, GpuTimestampRegion.FrameStart));
        Assert.Equal(13u, GpuTimestampProfiler12.QueryIndex(1, GpuTimestampRegion.ReferencesStart));
        Assert.Equal(29u, GpuTimestampProfiler12.QueryIndex(2, GpuTimestampRegion.FrameEnd));
    }
}