using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

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
            elapsedSeconds, 12f, LegacyWaterAnimation.FrameCount));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.NegativeInfinity)]
    [InlineData(-1f)]
    public void InvalidElapsedTimeFallsBackToFrameZero(float elapsedSeconds)
    {
        Assert.Equal(0, LegacyWaterAnimation.SelectFrame(elapsedSeconds, 12f, 32));
    }

    [Fact]
    public void GameShippingNoFramesYieldsNoneSoTheCallerKnowsToSynthesize()
    {
        // Retail Oblivion ships no water00-31.dds — the engine generates the surface at runtime.
        // This MUST come back empty. The bug it replaces probed the resolved bindless index instead,
        // and the texture cache answers every non-empty path with a valid permanently-placeholder
        // index, so all 32 "resolved" — the synthesizer never ran and the water never moved.
        Assert.Empty(LegacyWaterAnimation.ExistingFramePaths(static _ => false));
    }

    [Fact]
    public void GameShippingTheFullSequenceYieldsAllThirtyTwoInFrameOrder()
    {
        var paths = LegacyWaterAnimation.ExistingFramePaths(static _ => true);

        Assert.Equal(LegacyWaterAnimation.FrameCount, paths.Count);
        Assert.Equal(@"textures\water\water00.dds", paths[0]);
        Assert.Equal(@"textures\water\water31.dds", paths[^1]);
    }

    [Fact]
    public void PartiallyShippedSequenceKeepsOnlyThePresentFramesInOrder()
    {
        // A replacer that ships a subset must not leave placeholder gaps in the cycle.
        var paths = LegacyWaterAnimation.ExistingFramePaths(
            static path => path.EndsWith("water00.dds", StringComparison.Ordinal) ||
                           path.EndsWith("water17.dds", StringComparison.Ordinal));

        Assert.Equal(
            [@"textures\water\water00.dds", @"textures\water\water17.dds"],
            paths);
    }

    [Fact]
    public void NullProbeIsRejectedRatherThanSilentlyTreatedAsAbsent()
    {
        Assert.Throws<ArgumentNullException>(() => LegacyWaterAnimation.ExistingFramePaths(null!));
    }
}