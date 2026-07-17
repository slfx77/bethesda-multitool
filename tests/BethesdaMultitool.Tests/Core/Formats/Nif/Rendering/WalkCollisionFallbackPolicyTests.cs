using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
using System.Numerics;
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
        Assert.False(WalkCollisionFallbackPolicy.AllowsVisualMeshFallback(path));
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
        Assert.True(WalkCollisionFallbackPolicy.AllowsVisualMeshFallback(
            @"landscape\rocks\cliffs\CliffVerti_C2.NIF"));
    }

    [Fact]
    public void EffectCategory_AcceptsOnlyAuthoredHavokCollision()
    {
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        int[] triangles = [0, 1, 2];
        var authored = new CollisionMesh(
            positions,
            triangles,
            CollisionMeshSource.AuthoredHavok);
        var inferred = new CollisionMesh(
            positions,
            triangles,
            CollisionMeshSource.VisualFallback);

        Assert.True(WalkCollisionFallbackPolicy.AllowsResolvedCollision(
            authored,
            PlacedObjectCategory.Effects));
        Assert.False(WalkCollisionFallbackPolicy.AllowsResolvedCollision(
            inferred,
            PlacedObjectCategory.Effects));
        Assert.True(WalkCollisionFallbackPolicy.AllowsResolvedCollision(
            inferred,
            PlacedObjectCategory.Landscape));
    }
}
