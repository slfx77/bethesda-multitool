using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Games;
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

    /// <summary>
    ///     Interiors have no worldspace, so an interior with no XCWT used to resolve to nothing and the
    ///     renderer substituted the FNV exterior-lake preset — blue-lake tints and the NVCleanWater
    ///     surface defaults, in a cistern. ~46% of the watery FNV interiors sampled carry no XCWT.
    /// </summary>
    [Fact]
    public void Resolve_XcwtLessFnvInteriorFallsBackToDefaultInteriorWater()
    {
        var interiorDefault = Water(0x0000421E, "DefaultInteriorWater");
        var exteriorDefault = Water(0x00000018, "DefaultWater");

        var result = WaterAppearanceSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10, WaterFormId = null },
            worldspace: null,
            Index(interiorDefault, exteriorDefault),
            BethesdaGame.FalloutNewVegas,
            isInterior: true);

        Assert.Same(interiorDefault, result.Water);
        Assert.Equal(WaterAppearanceSelectionSource.EngineDefault, result.Source);
        Assert.Equal("engine-default", result.SourceTelemetry);
    }

    [Fact]
    public void Resolve_XcwtLessFnvExteriorFallsBackToDefaultWater()
    {
        var interiorDefault = Water(0x0000421E, "DefaultInteriorWater");
        var exteriorDefault = Water(0x00000018, "DefaultWater");

        var result = WaterAppearanceSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10, WaterFormId = null },
            worldspace: null,
            Index(interiorDefault, exteriorDefault),
            BethesdaGame.FalloutNewVegas,
            isInterior: false);

        Assert.Same(exteriorDefault, result.Water);
        Assert.Equal(WaterAppearanceSelectionSource.EngineDefault, result.Source);
    }

    /// <summary>The engine-default tier must never outrank authored data.</summary>
    [Fact]
    public void Resolve_AuthoredXcwtStillWinsOverTheEngineDefault()
    {
        var cellWater = Water(0x100, "1ECisternWater");
        var interiorDefault = Water(0x0000421E, "DefaultInteriorWater");

        var result = WaterAppearanceSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10, WaterFormId = cellWater.FormId },
            worldspace: null,
            Index(cellWater, interiorDefault),
            BethesdaGame.FalloutNewVegas,
            isInterior: true);

        Assert.Same(cellWater, result.Water);
        Assert.Equal(WaterAppearanceSelectionSource.CellXcwt, result.Source);
    }

    /// <summary>
    ///     Scoped to the two games confirmed to ship these forms; everything else keeps the previous
    ///     behaviour rather than inheriting Fallout's water by FormID coincidence.
    /// </summary>
    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Unknown)]
    public void Resolve_EngineDefaultTierIsScopedToFalloutThreeAndNewVegas(BethesdaGame game)
    {
        var result = WaterAppearanceSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10, WaterFormId = null },
            worldspace: null,
            Index(Water(0x0000421E, "DefaultInteriorWater"), Water(0x00000018, "DefaultWater")),
            game,
            isInterior: true);

        Assert.Null(result.Water);
        Assert.Equal(WaterAppearanceSelectionSource.Unavailable, result.Source);
    }

    /// <summary>Renumbered data still resolves, because the EditorID is searched too.</summary>
    [Fact]
    public void Resolve_EngineDefaultIsFoundByEditorIdWhenTheFormIdWasRenumbered()
    {
        var renumbered = Water(0x0B00421E, "DefaultInteriorWater");

        var result = WaterAppearanceSelectionResolver.Resolve(
            new CellRecord { FormId = 0x10, WaterFormId = null },
            worldspace: null,
            Index(renumbered),
            BethesdaGame.Fallout3,
            isInterior: true);

        Assert.Same(renumbered, result.Water);
        Assert.Equal(WaterAppearanceSelectionSource.EngineDefault, result.Source);
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