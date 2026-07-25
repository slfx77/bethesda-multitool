using BethesdaMultitool.Tests.Core.Formats.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Retail authority for the apparent "always orange" Mojave grade. WastelandNV's only exterior
///     XCIM repeats its WRLD INAM, while FFEncounterWorld supplies a genuinely different base IMGS.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvImageSpaceSelectionRetailTests(SampleFileFixture samples)
{
    private const uint WastelandNvFormId = 0x000DA726;
    private const uint WastelandExteriorXcimCellFormId = 0x00084336;
    private const uint NvDefaultExteriorFormId = 0x0008809D;
    private const uint FfEncounterWorldFormId = 0x00031E12;
    private const uint WastelandBaseImageSpaceFormId = 0x00064608;
    private const float CellSize = 4096f;

    [Fact]
    public void WastelandBoundaryAndEncounterWorld_PinAuthoredBaseImageSpaceSources()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        Assert.Equal(14, collection.Worldspaces.Count);
        Assert.Equal(67, collection.ImageSpaces.Count);

        var wasteland = Assert.Single(
            collection.Worldspaces,
            world => world.FormId == WastelandNvFormId);
        var encounter = Assert.Single(
            collection.Worldspaces,
            world => world.FormId == FfEncounterWorldFormId);
        Assert.Equal(NvDefaultExteriorFormId, wasteland.ImageSpaceFormId);
        Assert.Equal(WastelandBaseImageSpaceFormId, encounter.ImageSpaceFormId);

        var exteriorXcim = Assert.Single(wasteland.Cells, cell =>
            cell.GridX.HasValue &&
            cell.GridY.HasValue &&
            cell.ImageSpaceFormId.GetValueOrDefault() != 0);
        Assert.Equal(WastelandExteriorXcimCellFormId, exteriorXcim.FormId);
        Assert.Equal((6, -5), (exteriorXcim.GridX, exteriorXcim.GridY));
        Assert.Equal(NvDefaultExteriorFormId, exteriorXcim.ImageSpaceFormId);

        var grid = wasteland.Cells
            .Where(cell => cell.GridX.HasValue && cell.GridY.HasValue)
            .GroupBy(cell => (cell.GridX!.Value, cell.GridY!.Value))
            .ToDictionary(group => group.Key, group => group.Last());
        var westContext = ImageSpaceSelectionResolver.ResolveExteriorCell(
            grid, 5.5f * CellSize, -4.5f * CellSize, CellSize);
        var eastContext = ImageSpaceSelectionResolver.ResolveExteriorCell(
            grid, 6.5f * CellSize, -4.5f * CellSize, CellSize);
        var west = ImageSpaceSelectionResolver.Resolve(
            westContext, wasteland, collection.Worldspaces,
            false, true);
        var east = ImageSpaceSelectionResolver.Resolve(
            eastContext, wasteland, collection.Worldspaces,
            false, true);
        var encounterSelection = ImageSpaceSelectionResolver.Resolve(
            default, encounter, collection.Worldspaces,
            false, true);

        Assert.Equal((5, -5), (westContext.GridX, westContext.GridY));
        Assert.Equal((6, -5), (eastContext.GridX, eastContext.GridY));
        Assert.Equal(ImageSpaceSelectionSource.WorldspaceInam, west.Source);
        Assert.Equal(ImageSpaceSelectionSource.CellXcim, east.Source);
        Assert.Equal(NvDefaultExteriorFormId, west.ImageSpaceFormId);
        Assert.Equal(NvDefaultExteriorFormId, east.ImageSpaceFormId);
        Assert.Equal(ImageSpaceSelectionSource.WorldspaceInam, encounterSelection.Source);
        Assert.Equal(WastelandBaseImageSpaceFormId, encounterSelection.ImageSpaceFormId);
        Assert.NotEqual(west.ImageSpaceFormId, encounterSelection.ImageSpaceFormId);

        Assert.Equal("NVDefaultExterior", Assert.Single(collection.ImageSpaces,
            imageSpace => imageSpace.FormId == NvDefaultExteriorFormId).EditorId);
        Assert.Equal("WastelandBaseImageSpace", Assert.Single(collection.ImageSpaces,
            imageSpace => imageSpace.FormId == WastelandBaseImageSpaceFormId).EditorId);
    }
}