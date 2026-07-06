using BethesdaMultitool.Core.Resources;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Resources;

public sealed class ReferenceMeshCapacityPlannerTests
{
    [Theory]
    // Below the floor (count + 512 < 2048) → floor 2048 (never size smaller than the shipped default).
    [InlineData(0, 2048)]
    [InlineData(100, 2048)]
    [InlineData(1535, 2048)] // 1535 + 512 = 2047
    [InlineData(1536, 2048)] // 1536 + 512 = 2048 (boundary)
    // In-band → count + headroom (512).
    [InlineData(5000, 5512)]
    [InlineData(7431, 7943)] // ~game-wide distinct REFR'd model PATHS
    [InlineData(20000, 20512)] // FO4-scale distinct KEYS (paths × material-swap variants)
    [InlineData(48640, 49152)] // 48640 + 512 = 49152 (ceiling boundary)
    // Above the ceiling → clamp to 49152 (bounds corrupt/oversized input).
    [InlineData(100000, 49152)]
    // Negative treated as zero → floor.
    [InlineData(-5, 2048)]
    public void Plan_clamps_count_plus_headroom_between_floor_and_ceiling(int uniqueMeshCount, int expected)
    {
        Assert.Equal(expected, ReferenceMeshCapacityPlanner.Plan(uniqueMeshCount));
    }

    [Fact]
    public void Constants_are_ordered_floor_below_ceiling()
    {
        Assert.True(ReferenceMeshCapacityPlanner.FloorCapacity < ReferenceMeshCapacityPlanner.CeilingCapacity);
        Assert.True(ReferenceMeshCapacityPlanner.Headroom > 0);
    }
}