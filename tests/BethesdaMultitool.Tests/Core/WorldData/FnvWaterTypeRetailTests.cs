using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Camera;
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
    private const uint Dlc03TbCleanWaterFormId = 0x000E2C29;
    private const uint PotomacFormId = 0x00030009;
    private const uint LakeMeadWestCellFormId = 0x000DDDF8;
    private const uint LakeMeadEastCellFormId = 0x000DDDF7;
    private const uint FfEncounterWorldFormId = 0x00031E12;
    private const uint DefaultWaterFormId = 0x00000018;

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

    [Fact]
    public void LakeMeadBoundary_PreservesEachCellsDistinctAuthoredWaterType()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var worldspace = Assert.Single(
            collection.Worldspaces,
            world => world.FormId == WastelandNvFormId);

        // The reported camera positions 49000 and 49297 straddle x=49152, the exact boundary
        // between exterior grid x=11 and x=12. The two adjacent retail cells intentionally author
        // different XCWT records; renderer constants must therefore be selected per draw batch.
        var west = Assert.Single(worldspace.Cells, cell => cell.FormId == LakeMeadWestCellFormId);
        var east = Assert.Single(worldspace.Cells, cell => cell.FormId == LakeMeadEastCellFormId);

        Assert.Equal((11, 12), (west.GridX, west.GridY));
        Assert.Equal((12, 12), (east.GridX, east.GridY));
        Assert.Equal(Dlc03TbCleanWaterFormId, west.WaterFormId);
        Assert.Equal(NvcCleanWaterFormId, east.WaterFormId);
        Assert.NotEqual(west.WaterFormId, east.WaterFormId);
    }

    [Fact]
    public void FfEncounterWorld_DefaultWaterDebugFillPhaseRemainsTimeDependent()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var worldspace = Assert.Single(
            collection.Worldspaces,
            world => world.FormId == FfEncounterWorldFormId);
        var water = Assert.Single(
            collection.Water.GroupBy(record => record.FormId).Select(group => group.Last()),
            record => record.FormId == DefaultWaterFormId);
        var appearance = Assert.IsType<WaterAppearance>(WaterAppearance.FromWaterRecord(water));

        Assert.Equal(DefaultWaterFormId, worldspace.WaterFormId);
        Assert.Equal("DefaultWater", water.EditorId);
        Assert.Equal(
            "data\\textures\\water\\genaratednoise01.dds",
            appearance.NoiseTexture?.Replace('/', '\\').ToLowerInvariant());

        var debugFill = BitConverter.Int32BitsToSingle(unchecked((int)0xCDCDCDCD));
        Assert.Equal(0f, appearance.Surface.Layer1.WindSpeed);
        Assert.Equal(debugFill, appearance.Surface.Layer2.WindDirDegrees);
        Assert.Equal(debugFill, appearance.Surface.Layer2.WindSpeed);
        Assert.Equal(debugFill, appearance.Surface.Layer3.WindDirDegrees);
        Assert.Equal(debugFill, appearance.Surface.Layer3.WindSpeed);
        Assert.NotEqual(
            FnvWaterNoiseAnimation.Scroll(appearance.Surface.Layer2, 0f),
            FnvWaterNoiseAnimation.Scroll(appearance.Surface.Layer2, 1.2345f));
    }
}
