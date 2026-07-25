using BethesdaMultitool.Core.Formats.Esm.Planner.References.Walkers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Planner.References.Walkers;

public sealed class ImageSpaceModifierReferenceWalkerTests
{
    [Fact]
    public void Walk_ReportsOptionalIntroAndOutroSoundPaths()
    {
        var model = ImageSpaceModifierTestFactory.Complete(
            introSound: 0x00123456,
            outroSound: 0x00654321);

        var references = new ImageSpaceModifierReferenceWalker().Walk(model).ToArray();

        Assert.Collection(references,
            intro =>
            {
                Assert.Equal("RDSD", intro.FieldPath);
                Assert.Equal(0x00123456u, intro.FormId);
            },
            outro =>
            {
                Assert.Equal("RDSI", outro.FieldPath);
                Assert.Equal(0x00654321u, outro.FormId);
            });
    }

    [Fact]
    public void Walk_WithoutSounds_YieldsNothing()
    {
        Assert.Empty(new ImageSpaceModifierReferenceWalker().Walk(ImageSpaceModifierTestFactory.Complete()));
    }
}