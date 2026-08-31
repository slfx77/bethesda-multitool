using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class StarfieldWaterMaterialSelectionResolverTests
{
    [Fact]
    public void Resolve_CellXcwmWinsOverWorldspaceNam7()
    {
        var cell = new CellRecord
        {
            FormId = 0x10,
            StarfieldWaterType = "materials/water/cell.mat"
        };
        var worldspace = new WorldspaceRecord
        {
            FormId = 0x20,
            StarfieldWaterMaterial = "materials/water/world.mat"
        };

        var result = StarfieldWaterMaterialSelectionResolver.Resolve(cell, worldspace);

        Assert.Equal("materials/water/cell.mat", result.Material);
        Assert.Equal(StarfieldWaterMaterialSelectionSource.CellXcwm, result.Source);
        Assert.Equal("cell-xcwm", result.SourceTelemetry);
        Assert.Equal(cell.FormId, result.CellFormId);
        Assert.Equal(worldspace.FormId, result.WorldspaceFormId);
    }

    [Fact]
    public void Resolve_AbsentCellXcwmFallsBackToWorldspaceNam7()
    {
        var worldspace = new WorldspaceRecord
        {
            FormId = 0x20,
            StarfieldWaterMaterial = "materials/water/world.mat"
        };

        var result = StarfieldWaterMaterialSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10 }, worldspace);

        Assert.Equal("materials/water/world.mat", result.Material);
        Assert.Equal(StarfieldWaterMaterialSelectionSource.WorldspaceNam7, result.Source);
        Assert.Equal("worldspace-nam7", result.SourceTelemetry);
    }

    [Fact]
    public void Resolve_AuthoredEmptyXcwmSuppressesWorldspaceNam7()
    {
        var result = StarfieldWaterMaterialSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10, StarfieldWaterType = string.Empty },
            new WorldspaceRecord
            {
                FormId = 0x20,
                StarfieldWaterMaterial = "materials/water/world.mat"
            });

        Assert.Equal(string.Empty, result.Material);
        Assert.Equal(StarfieldWaterMaterialSelectionSource.CellXcwm, result.Source);
    }

    [Fact]
    public void Resolve_AbsentRecordsAreUnavailableWithoutInventingFallback()
    {
        var result = StarfieldWaterMaterialSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10 },
            new WorldspaceRecord { FormId = 0x20 });

        Assert.Null(result.Material);
        Assert.Equal(StarfieldWaterMaterialSelectionSource.Unavailable, result.Source);
        Assert.Equal("unavailable", result.SourceTelemetry);
    }
}
