using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class WaterAppearanceSelectionResolverTests
{
    [Fact]
    public void Resolve_RetainedCellXcwtWinsOverWorldspaceNam2()
    {
        var cellWater = Water(0x100, "CellWater");
        var worldWater = Water(0x200, "WorldWater");
        var cell = new CellRecord { FormId = 0x10, WaterFormId = cellWater.FormId };
        var worldspace = new WorldspaceRecord
        {
            FormId = 0x20,
            WaterFormId = worldWater.FormId
        };

        var result = WaterAppearanceSelectionResolver.Resolve(
            cell, worldspace, Index(cellWater, worldWater));

        Assert.Same(cellWater, result.Water);
        Assert.Equal(cellWater.FormId, result.WaterFormId);
        Assert.Equal(WaterAppearanceSelectionSource.CellXcwt, result.Source);
        Assert.Equal("cell-xcwt", result.SourceTelemetry);
        Assert.Equal(cell.FormId, result.CellFormId);
        Assert.Equal(worldspace.FormId, result.WorldspaceFormId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0u)]
    [InlineData(0x999u)]
    public void Resolve_MissingZeroOrUnresolvedCellXcwtFallsBackToWorldspaceNam2(uint? cellWaterFormId)
    {
        var worldWater = Water(0x200, "WorldWater");
        var cell = new CellRecord { FormId = 0x10, WaterFormId = cellWaterFormId };
        var worldspace = new WorldspaceRecord
        {
            FormId = 0x20,
            WaterFormId = worldWater.FormId
        };

        var result = WaterAppearanceSelectionResolver.Resolve(
            cell, worldspace, Index(worldWater));

        Assert.Same(worldWater, result.Water);
        Assert.Equal(WaterAppearanceSelectionSource.WorldspaceNam2, result.Source);
        Assert.Equal("worldspace-nam2", result.SourceTelemetry);
    }

    [Fact]
    public void Resolve_NoRetainedCellOrWorldspaceWaterIsUnavailable()
    {
        var result = WaterAppearanceSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10, WaterFormId = 0x100 },
            new WorldspaceRecord { FormId = 0x20, WaterFormId = 0x200 },
            new Dictionary<uint, WaterRecord>());

        Assert.Null(result.Water);
        Assert.Null(result.WaterFormId);
        Assert.Equal(WaterAppearanceSelectionSource.Unavailable, result.Source);
        Assert.Equal("unavailable", result.SourceTelemetry);
    }

    private static WaterRecord Water(uint formId, string editorId)
    {
        return new WaterRecord { FormId = formId, EditorId = editorId };
    }

    private static Dictionary<uint, WaterRecord> Index(params WaterRecord[] waters)
    {
        return waters.ToDictionary(water => water.FormId);
    }
}