using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class WalkCollisionFallbackPolicyTests
{
    [Theory]
    [InlineData(@"effects\NV\NVLimestoneDustStormHalfViz.NIF")]
    [InlineData(@"Effects/Ambient/Industrial/IndFXLightRaysRight01.NIF")]
    [InlineData(@"meshes\effects\Smoke\SomeCard.nif")]
    public void EffectModel_NeverReceivesSpeculativeBoundsCollision(string path)
    {
        Assert.False(WalkCollisionFallbackPolicy.AllowsObjectBoundsFallback(path));
    }

    [Fact]
    public void EffectCategory_NeverReceivesSpeculativeBoundsCollision()
    {
        Assert.False(WalkCollisionFallbackPolicy.AllowsObjectBoundsFallback(
            @"architecture\some-activator.nif", PlacedObjectCategory.Effects));
    }

    [Fact]
    public void OrdinaryColdSolid_RetainsBoundsFallback()
    {
        Assert.True(WalkCollisionFallbackPolicy.AllowsObjectBoundsFallback(
            @"landscape\rocks\cliffs\CliffVerti_C2.NIF", PlacedObjectCategory.Landscape));
    }
}
