using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class WaterTransparencyPartitionTests
{
    [Theory]
    [InlineData(-2f, 1f, 0f, true)]
    [InlineData(-1f, 1f, 0f, false)]
    [InlineData(0f, 1f, 0f, false)]
    [InlineData(2f, 1f, 0f, false)]
    public void OnlyConservativeBoundsWhollyBelowPlaneEnterSnapshot(
        float centerZ,
        float radius,
        float planeZ,
        bool expected)
    {
        Assert.Equal(
            expected,
            WaterTransparencyPartition.IsWhollyBelow(centerZ, radius, planeZ));
    }

    [Theory]
    [InlineData(float.NaN, 1f, 0f)]
    [InlineData(0f, float.NaN, 0f)]
    [InlineData(0f, -1f, 0f)]
    [InlineData(0f, 1f, float.PositiveInfinity)]
    public void InvalidBoundsRemainInPostWaterComplement(float centerZ, float radius, float planeZ)
    {
        Assert.False(WaterTransparencyPartition.IsWhollyBelow(centerZ, radius, planeZ));
    }
}