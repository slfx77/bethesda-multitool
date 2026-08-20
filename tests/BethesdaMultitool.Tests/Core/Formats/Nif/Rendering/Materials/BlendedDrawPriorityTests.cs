using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Materials;

public sealed class BlendedDrawPriorityTests
{
    [Theory]
    [InlineData(2, 2, 2, 1)]
    [InlineData(1, 3, 1, 2)]
    [InlineData(0, 4, 0, 3)]
    public void CapacityPressure_SelectsNearestDrawableSuffix(
        int capacity,
        int expectedFirst,
        int expectedSelected,
        int expectedTruncated)
    {
        byte[] drawable = [1, 0, 1, 1]; // farthest -> nearest

        var plan = BlendedDrawPriority.PlanNearestBackToFront(drawable, capacity);

        Assert.Equal(expectedFirst, plan.FirstSelected);
        Assert.Equal(expectedSelected, plan.SelectedCount);
        Assert.Equal(expectedTruncated, plan.TruncatedCount);
    }

    [Fact]
    public void QuietFrames_ConsumeNoCapacityOrTelemetry()
    {
        byte[] drawable = [0, 0, 0];

        var plan = BlendedDrawPriority.PlanNearestBackToFront(drawable, 0);

        Assert.Equal(drawable.Length, plan.FirstSelected);
        Assert.Equal(0, plan.SelectedCount);
        Assert.Equal(0, plan.TruncatedCount);
    }

    [Theory]
    [InlineData(0u, 1024u, 240u, 256u, 4)]
    [InlineData(1u, 1024u, 240u, 256u, 3)]
    [InlineData(768u, 1024u, 240u, 256u, 1)]
    [InlineData(769u, 1024u, 240u, 256u, 0)]
    public void AlignedCapacity_MatchesRingBumpLayout(
        uint current,
        uint total,
        uint size,
        uint alignment,
        int expected)
    {
        Assert.Equal(
            expected,
            BlendedDrawPriority.CountAlignedAllocations(current, total, size, alignment));
    }
}