using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;
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
            DataFlagSemantics = CellDataFlagSemantics.ClassicBit7,
            WorldspaceFormId = parent.FormId,
            ClimateFormId = 0x40
        };

        var result = SkySceneContextResolver.Resolve(cell, null, parent);

        Assert.True(cell.BehavesLikeExterior);
        Assert.True(result.IsInterior);
        Assert.True(result.BehavesLikeExterior);
        Assert.False(result.ShowsSky);
        Assert.False(result.UsesSkyLighting);
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
        var cell = new CellRecord
        {
            Flags = 0x81,
            DataFlagSemantics = CellDataFlagSemantics.ClassicBit7,
            WorldspaceFormId = parent.FormId
        };

        var result = SkySceneContextResolver.Resolve(cell, null, parent);

        Assert.Equal(0x20u, result.PreferredClimateFormId);
    }

    [Fact]
    public void OrdinaryInterior_SuppressesParentClimateAndExteriorSky()
    {
        var parent = new WorldspaceRecord { FormId = 0x10, ClimateFormId = 0x20 };
        var cell = new CellRecord
        {
            Flags = 0x01,
            DataFlagSemantics = CellDataFlagSemantics.ClassicBit7,
            WorldspaceFormId = parent.FormId,
            ClimateFormId = 0x40
        };

        var result = SkySceneContextResolver.Resolve(cell, null, parent);

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
            DataFlagSemantics = CellDataFlagSemantics.ClassicBit7,
            WorldspaceFormId = 0x10,
            ClimateFormId = 0x40
        };
        var staleParent = new WorldspaceRecord { FormId = 0x11, ClimateFormId = 0x21 };

        var result = SkySceneContextResolver.Resolve(cell, null, staleParent);

        Assert.True(result.RendersExteriorSky);
        Assert.Null(result.Worldspace);
        Assert.Null(result.WorldspaceClimateFormId);
        Assert.Equal(0x40u, result.PreferredClimateFormId);
    }

    [Fact]
    public void CreationShowSky_RendersSkyButRetainsInteriorClassification()
    {
        var cell = new CellRecord
        {
            FormId = 0x000165A3,
            Flags = 0x00A1,
            DataFlagSemantics = CellDataFlagSemantics.Creation,
            ImageSpaceFormId = 0x00036ED2,
            LightingData = new Dictionary<string, object?> { ["Ambient Color"] = "retained" }
        };

        var result = SkySceneContextResolver.Resolve(cell, null, null);

        Assert.True(result.IsInterior);
        Assert.False(result.BehavesLikeExterior);
        Assert.True(result.ShowsSky);
        Assert.False(result.UsesSkyLighting);
        Assert.True(result.RendersExteriorSky);
        Assert.Null(result.Worldspace);
        Assert.Null(result.PreferredClimateFormId);
        Assert.Equal(0x00036ED2u, cell.ImageSpaceFormId);
        Assert.NotNull(cell.LightingData);
    }

    [Fact]
    public void CreationUseSkyLighting_DoesNotImplicitlyEnableSkyOrExteriorWeather()
    {
        var cell = new CellRecord
        {
            Flags = 0x0101,
            DataFlagSemantics = CellDataFlagSemantics.Creation
        };

        var result = SkySceneContextResolver.Resolve(cell, null, null);

        Assert.True(result.IsInterior);
        Assert.False(result.BehavesLikeExterior);
        Assert.False(result.ShowsSky);
        Assert.True(result.UsesSkyLighting);
        Assert.False(result.RendersExteriorSky);
        Assert.Null(result.Worldspace);
        Assert.Null(result.PreferredClimateFormId);
    }
}