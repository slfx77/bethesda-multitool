using BethesdaMultitool.Core.Formats.Esm.Export.Support;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Regression for "water is absent when not at the worldspace default height": WastelandNV cell
///     (14, 7) — the Lake Las Vegas basin — authors an explicit XCLW of 2600 against the worldspace
///     default of -2300, and the 3D viewer's spatial index must emit its water cell at the authored
///     height. Verified absent in a headless capture 2026-07-22 (dry basin, pier in sand).
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class WastelandNvExplicitWaterHeightTests(SampleFileFixture samples)
{
    private const uint WastelandNvFormId = 0x000DA726;
    private const uint LakeLasVegasCellFormId = 0x000DDE1D;

    [Fact]
    public async Task LakeLasVegasCell_EmitsWaterCellAtAuthoredHeight()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC ESM sample not available");

        var result = await RealAssetEsmCache.LoadAsync(samples.PcFinalEsm!,
            TestContext.Current.CancellationToken);

        var worldspace = result.Records.Worldspaces.Single(w => w.FormId == WastelandNvFormId);
        // The 3D viewer's exact cell source is the worldspace record's ATTACHED cell list
        // (GetSelectedWorldspaceCells reads ws.Cells), not a WorldspaceFormId filter — test the
        // attached list so a linkage/attach regression cannot hide behind a passing filter.
        var cells = worldspace.Cells
            .Where(c => c.GridX is int && c.GridY is int)
            .ToList();

        var lakeCell = Assert.Single(cells, c => c.FormId == LakeLasVegasCellFormId);
        Assert.Equal(14, lakeCell.GridX);
        Assert.Equal(7, lakeCell.GridY);
        Assert.True(lakeCell.HasWater);
        Assert.NotNull(lakeCell.WaterHeight);
        Assert.Equal(2600f, lakeCell.WaterHeight!.Value, 1);

        // Stage 1: effective-height resolution (explicit XCLW wins over the worldspace default).
        var resolved = WorldRenderCache.ResolveEffectiveWaterHeight(
            lakeCell, worldspace.DefaultWaterHeight, worldspace.WaterFromParentWorldspace);
        Assert.Equal(2600f, resolved!.Value, 1);

        // Stage 2: the 3D spatial index (the renderer's water source) must carry the cell.
        var data = EmptyWorldViewData();
        var index = WorldSpatialIndex.BuildFor3D(
            data, cells, worldspace.DefaultWaterHeight, worldspace.WaterFromParentWorldspace);

        var water = Assert.Single(index.WaterCells, w => w.Key == (14, 7));
        Assert.Equal(2600f, water.Height, 1);
    }

    private static WorldViewData EmptyWorldViewData()
    {
        return new WorldViewData
        {
            Worldspaces = [],
            InteriorCells = [],
            BoundsIndex = [],
            CategoryIndex = [],
            Resolver = FormIdResolver.Empty,
            MapMarkers = [],
            MarkersByWorldspace = [],
            AllCells = [],
            CellByFormId = [],
            PlacedRefs = PlacedRefIndex.Empty,
            UnlinkedExteriorCells = [],
            UnlinkedMapMarkers = []
        };
    }
}