using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Particles;

public sealed class ParticleSubtextureModifierTests
{
    [Theory]
    [InlineData(16, 15f, 1f, 0.992317f, 15)]
    [InlineData(64, 63f, 1f, 0.190132f, 63)]
    public void SampleFrame_ClampsFudgedStartToAtlasRange(
        int atlasCount,
        float startFrame,
        float startFrameFudge,
        float seed,
        int expected)
    {
        var modifier = new SubtextureModifierDefinition
        {
            StartFrame = startFrame,
            StartFrameFudge = startFrameFudge,
            EndFrame = startFrame,
            LoopStartFrame = startFrame,
            LoopStartFrameFudge = startFrameFudge,
            FrameCount = 1f
        };

        Assert.Equal(expected, modifier.SampleFrame(2f, seed, atlasCount));
    }

    [Fact]
    public void SampleFrame_LoopsWithinClampedAtlasRange()
    {
        var modifier = new SubtextureModifierDefinition
        {
            StartFrame = 0f,
            EndFrame = 100f,
            LoopStartFrame = 0f,
            FrameCount = 100f
        };

        Assert.Equal(4, modifier.SampleFrame(1f, 0f, 16));
    }
}