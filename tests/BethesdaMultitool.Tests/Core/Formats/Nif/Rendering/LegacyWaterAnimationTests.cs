using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class LegacyWaterAnimationTests
{
    [Fact]
    public void FramePathsCoverTheAuthoredWater00ThroughWater31Sequence()
    {
        Assert.Equal(32, LegacyWaterAnimation.FrameCount);
        Assert.Equal(@"textures\water\water00.dds", LegacyWaterAnimation.FramePath(0));
        Assert.Equal(@"textures\water\water09.dds", LegacyWaterAnimation.FramePath(9));
        Assert.Equal(@"textures\water\water31.dds", LegacyWaterAnimation.FramePath(31));
        Assert.Throws<ArgumentOutOfRangeException>(() => LegacyWaterAnimation.FramePath(32));
    }

    [Theory]
    [InlineData(0f, 0)]
    [InlineData(1f / 12f, 1)]
    [InlineData(31f / 12f, 31)]
    [InlineData(32f / 12f, 0)]
    [InlineData(33f / 12f, 1)]
    public void TwelveFpsSelectionLoopsAcrossAllThirtyTwoFrames(float elapsedSeconds, int expected)
    {
        Assert.Equal(expected, LegacyWaterAnimation.SelectFrame(
            elapsedSeconds, framesPerSecond: 12f, LegacyWaterAnimation.FrameCount));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1f)]
    public void InvalidElapsedTimeFallsBackToFrameZero(float elapsedSeconds)
    {
        Assert.Equal(0, LegacyWaterAnimation.SelectFrame(elapsedSeconds, 12f, 32));
    }
}
