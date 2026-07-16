using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class SkySceneContextResolverTests
{
    [Fact]
    public void BehavesLikeExterior_ResolvesMatchingParentAndPrefersCellClimateOverride()
    {
        var parent = new WorldspaceRecord { FormId = 0x10, ClimateFormId = 0x20 };
        var cell = new CellRecord
        {
            FormId = 0x30,
            Flags = 0x81,
            WorldspaceFormId = parent.FormId,
            ClimateFormId = 0x40,
        };

        var result = SkySceneContextResolver.Resolve(cell, selectedExteriorWorldspace: null, parent);

        Assert.True(cell.BehavesLikeExterior);
        Assert.True(result.IsInterior);
        Assert.True(result.BehavesLikeExterior);
        Assert.True(result.RendersExteriorSky);
        Assert.Same(parent, result.Worldspace);
        Assert.Equal(0x40u, result.CellClimateFormId);
        Assert.Equal(0x20u, result.WorldspaceClimateFormId);
        Assert.Equal(0x40u, result.PreferredClimateFormId);
    }

    [Fact]
    public void BehavesLikeExterior_WithoutCellOverrideUsesParentClimate()
    {
        var parent = new WorldspaceRecord { FormId = 0x10, ClimateFormId = 0x20 };
        var cell = new CellRecord { Flags = 0x81, WorldspaceFormId = parent.FormId };

        var result = SkySceneContextResolver.Resolve(cell, selectedExteriorWorldspace: null, parent);

        Assert.Equal(0x20u, result.PreferredClimateFormId);
    }

    [Fact]
    public void OrdinaryInterior_SuppressesParentClimateAndExteriorSky()
    {
        var parent = new WorldspaceRecord { FormId = 0x10, ClimateFormId = 0x20 };
        var cell = new CellRecord
        {
            Flags = 0x01,
            WorldspaceFormId = parent.FormId,
            ClimateFormId = 0x40,
        };

        var result = SkySceneContextResolver.Resolve(cell, selectedExteriorWorldspace: null, parent);

        Assert.False(cell.BehavesLikeExterior);
        Assert.True(result.IsInterior);
        Assert.False(result.BehavesLikeExterior);
        Assert.False(result.RendersExteriorSky);
        Assert.Null(result.Worldspace);
        Assert.Equal(0x40u, result.CellClimateFormId);
        Assert.Null(result.PreferredClimateFormId);
    }

    [Fact]
    public void BehavesLikeExterior_RejectsStaleMismatchedParentWorldspace()
    {
        var cell = new CellRecord
        {
            Flags = 0x81,
            WorldspaceFormId = 0x10,
            ClimateFormId = 0x40,
        };
        var staleParent = new WorldspaceRecord { FormId = 0x11, ClimateFormId = 0x21 };

        var result = SkySceneContextResolver.Resolve(cell, selectedExteriorWorldspace: null, staleParent);

        Assert.True(result.RendersExteriorSky);
        Assert.Null(result.Worldspace);
        Assert.Null(result.WorldspaceClimateFormId);
        Assert.Equal(0x40u, result.PreferredClimateFormId);
    }
}
