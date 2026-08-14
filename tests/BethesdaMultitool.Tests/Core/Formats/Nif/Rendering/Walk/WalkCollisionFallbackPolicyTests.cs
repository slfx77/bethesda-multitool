using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Nif.Collision;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Walk;
using Xunit;
using Xunit.Sdk;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Walk;

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
        const string path = @"effects\box03.nif";
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        int[] triangles = [0, 1, 2];
        var visualFactoryCalled = false;

        var entry = CollisionCacheEntry.Create(
            path,
            HavokCollisionProvenance.AuthoredMesh,
            positions,
            triangles,
            () =>
            {
                visualFactoryCalled = true;
                return new CollisionMesh(positions, triangles);
            });
        var result = entry.Resolve(path, PlacedObjectCategory.Effects);

        Assert.False(visualFactoryCalled);
        Assert.NotNull(result.Mesh);
        Assert.Equal(CollisionMeshSource.AuthoredHavok, result.Source);
    }

    [Theory]
    [InlineData(@"effects\NV\NVLimestoneDustStormHalfViz.NIF", false, false)]
    [InlineData(@"architecture\some-activator.nif", true, true)]
    public void EffectWithoutAuthoredHavok_ResolvesAuthoritativeNoneAtCacheBoundary(
        string path,
        bool explicitEffectCategory,
        bool expectedVisualFactoryCall)
    {
        var visualFactoryCalled = false;
        var category = explicitEffectCategory
            ? PlacedObjectCategory.Effects
            : PlacedObjectCategory.Unknown;

        var entry = CollisionCacheEntry.Create(
            path,
            HavokCollisionProvenance.AbsentOrUnsupported,
            null,
            null,
            () =>
            {
                visualFactoryCalled = true;
                return new CollisionMesh(
                    [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                    [0, 1, 2]);
            });
        var result = entry.Resolve(path, category);

        Assert.Equal(expectedVisualFactoryCall, visualFactoryCalled);
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

        const string path = @"landscape\rocks\cliffs\CliffVerti_C2.NIF";
        var entry = CollisionCacheEntry.Create(
            path,
            HavokCollisionProvenance.AbsentOrUnsupported,
            null,
            null,
            () => visual);
        var result = entry.Resolve(path, PlacedObjectCategory.Landscape);

        Assert.Same(visual, result.Mesh);
        Assert.Equal(CollisionMeshSource.VisualFallback, result.Source);
    }

    [Fact]
    public void OrdinaryModelWithoutHavokOrVisual_ResolvesAuthoritativeNone()
    {
        const string path = @"architecture\empty-marker.nif";
        var entry = CollisionCacheEntry.Create(
            path, HavokCollisionProvenance.AbsentOrUnsupported, null, null, static () => null);

        var result = entry.Resolve(path, PlacedObjectCategory.Architecture);
        var resolution = CollisionMeshResolution.From(result);

        Assert.Null(result.Mesh);
        Assert.Equal(CollisionMeshSource.None, result.Source);
        Assert.True(resolution.IsResolved);
        Assert.Null(resolution.Mesh);
    }

    [Fact]
    public void Vegetation_NeverReceivesSpeculativeBoundsCollision()
    {
        // Plants and Trees (even a plausibly-solid path) contribute no OBND-box wall.
        foreach (var category in new[] { PlacedObjectCategory.Plants, PlacedObjectCategory.Tree })
        {
            Assert.True(WalkCollisionFallbackPolicy.IsVegetation(category));
            Assert.False(WalkCollisionFallbackPolicy.AllowsObjectBoundsFallback(
                @"plants\WastelandShrub01.nif", category));
        }
    }

    [Fact]
    public void VegetationWithoutHavok_RejectsStoredVisualFallback()
    {
        const string path = @"plants\WastelandShrub01.nif";
        var visual = new CollisionMesh([Vector3.Zero, Vector3.UnitX, Vector3.UnitY], [0, 1, 2]);
        var visualFactoryCalls = 0;
        var entry = CollisionCacheEntry.Create(
            path,
            HavokCollisionProvenance.AbsentOrUnsupported,
            null,
            null,
            () =>
            {
                visualFactoryCalls++;
                return visual;
            });

        foreach (var category in new[] { PlacedObjectCategory.Plants, PlacedObjectCategory.Tree })
        {
            var result = entry.Resolve(path, category);

            Assert.Null(result.Mesh);
            Assert.Equal(CollisionMeshSource.None, result.Source);
        }

        var ordinary = entry.Resolve(path, PlacedObjectCategory.Architecture);
        Assert.Same(visual, ordinary.Mesh);
        Assert.Equal(CollisionMeshSource.VisualFallback, ordinary.Source);
        Assert.Equal(1, visualFactoryCalls);
    }

    [Fact]
    public void VegetationWithAuthoredHavok_StaysSolid()
    {
        // Authored Havok is checked BEFORE the vegetation exclusion, so a plant that genuinely ships
        // collision (e.g. a solid cactus) still collides.
        var positions = new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY };
        int[] triangles = [0, 1, 2];

        const string path = @"plants\SolidCactus01.nif";
        var entry = CollisionCacheEntry.Create(
            path,
            HavokCollisionProvenance.AuthoredMesh,
            positions,
            triangles,
            () => throw new XunitException("visual fallback must not run when Havok is authored"));
        var result = entry.Resolve(path, PlacedObjectCategory.Plants);

        Assert.NotNull(result.Mesh);
        Assert.Equal(CollisionMeshSource.AuthoredHavok, result.Source);
    }
}
