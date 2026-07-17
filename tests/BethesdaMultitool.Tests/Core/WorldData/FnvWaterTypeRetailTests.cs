using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Tests.Core.Formats.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Retail regression for the exact WastelandNV camera cell used by fnv-water-night-matrix.
///     It authors NVCleanWater through CELL XCWT even though WRLD NAM2 defaults to Potomac.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvWaterTypeRetailTests(SampleFileFixture samples)
{
    private const uint WastelandNvFormId = 0x000DA726;
    private const uint CameraCellFormId = 0x000DDCF8;
    private const uint NvcCleanWaterFormId = 0x001009CA;
    private const uint PotomacFormId = 0x00030009;

    [Fact]
    public void WastelandNvCameraCell_ResolvesAuthoredXcwtInsteadOfWorldspaceDefault()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var worldspace = Assert.Single(
            collection.Worldspaces,
            world => world.FormId == WastelandNvFormId);
        var cell = Assert.Single(worldspace.Cells, candidate => candidate.FormId == CameraCellFormId);
        var waters = collection.Water
            .GroupBy(water => water.FormId)
            .ToDictionary(group => group.Key, group => group.Last());

        Assert.Equal(PotomacFormId, worldspace.WaterFormId);
        Assert.Equal(NvcCleanWaterFormId, cell.WaterFormId);

        var selection = WaterAppearanceSelectionResolver.Resolve(cell, worldspace, waters);
        var appearance = Assert.IsType<WaterAppearance>(
            WaterAppearance.FromWaterRecord(selection.Water));

        Assert.Equal(WaterAppearanceSelectionSource.CellXcwt, selection.Source);
        Assert.Equal(NvcCleanWaterFormId, selection.WaterFormId);
        Assert.Equal("NVCleanWater", selection.Water!.EditorId);
        Assert.Equal((0x34, 0x4B, 0x42), appearance.Shallow);
        Assert.Equal((0x16, 0x29, 0x22), appearance.Deep);
        Assert.Equal((0x15, 0x20, 0x17), appearance.Reflection);
    }
}
