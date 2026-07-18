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
        var visualFactoryCalled = false;

        var result = CollisionMeshBuilder.Build(
            @"effects\box03.nif",
            PlacedObjectCategory.Effects,
            positions,
            triangles,
            () =>
            {
                visualFactoryCalled = true;
                return new CollisionMesh(positions, triangles);
            });

        Assert.False(visualFactoryCalled);
        Assert.NotNull(result.Mesh);
        Assert.Equal(CollisionMeshSource.AuthoredHavok, result.Source);
    }

    [Theory]
    [InlineData(@"effects\NV\NVLimestoneDustStormHalfViz.NIF", false)]
    [InlineData(@"architecture\some-activator.nif", true)]
    public void EffectWithoutAuthoredHavok_ResolvesNoneWithoutBuildingVisualFallback(
        string path,
        bool explicitEffectCategory)
    {
        var visualFactoryCalled = false;
        var category = explicitEffectCategory
            ? PlacedObjectCategory.Effects
            : PlacedObjectCategory.Unknown;

        var result = CollisionMeshBuilder.Build(
            path,
            category,
            authoredPositions: null,
            authoredTriangles: null,
            () =>
            {
                visualFactoryCalled = true;
                return new CollisionMesh(
                    [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                    [0, 1, 2]);
            });

        Assert.False(visualFactoryCalled);
        Assert.Null(result.Mesh);
        Assert.Equal(CollisionMeshSource.None, result.Source);
        var resolution = CollisionMeshResolution.From(result);
        Assert.True(resolution.IsResolved);
        Assert.Null(resolution.Mesh);
    }

    [Fact]
    public void OrdinaryModelWithoutHavok_BuildsVisualFallback()
    {
        var visual = new CollisionMesh(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 2]);

        var result = CollisionMeshBuilder.Build(
            @"landscape\rocks\cliffs\CliffVerti_C2.NIF",
            PlacedObjectCategory.Landscape,
            authoredPositions: null,
            authoredTriangles: null,
            () => visual);

        Assert.Same(visual, result.Mesh);
        Assert.Equal(CollisionMeshSource.VisualFallback, result.Source);
    }
}
