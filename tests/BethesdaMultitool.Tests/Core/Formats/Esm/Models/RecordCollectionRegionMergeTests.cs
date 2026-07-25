using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Models;

public sealed class RecordCollectionRegionMergeTests
{
    [Fact]
    public void RuntimeOnlyOverlay_PreservesStaticGeometryAndWeatherPayloads()
    {
        var area = new RegionArea(
            1024,
            [new RegionPoint(0, 0), new RegionPoint(100, 0), new RegionPoint(0, 100)]);
        var weatherType = new RegionWeatherType(0x40, 100, 0);
        var dataBlock = new RegionDataBlock(
            3,
            0x00006401,
            [new RegionSubrecord("RDWT", new byte[12])]);
        var staticRegion = new RegionRecord
        {
            FormId = 0x30,
            EditorId = "StaticRegion",
            WorldspaceFormId = 0x10,
            EmittanceColorR = 1,
            Areas = [area],
            DataBlockCount = 1,
            DataBlocks = [dataBlock],
            WeatherTypes = [weatherType],
            GrassFormIds = [0x50]
        };
        var runtimeRegion = new RegionRecord
        {
            FormId = staticRegion.FormId,
            EditorId = "RuntimeRegion",
            WorldspaceFormId = staticRegion.WorldspaceFormId,
            EmittanceColorR = 200,
            IsRuntimeOnly = true
        };

        var merged = new RecordCollection { Regions = [staticRegion] }
            .MergeWith(new RecordCollection { Regions = [runtimeRegion] });
        var region = Assert.Single(merged.Regions);

        Assert.True(region.IsRuntimeOnly);
        Assert.Equal("RuntimeRegion", region.EditorId);
        Assert.Equal(200, region.EmittanceColorR);
        Assert.Equal([area], region.Areas);
        Assert.Equal(1, region.DataBlockCount);
        Assert.Equal([dataBlock], region.DataBlocks);
        Assert.Equal([weatherType], region.WeatherTypes);
        Assert.Equal([0x50u], region.GrassFormIds);
    }

    [Fact]
    public void StaticPluginOverlay_RemainsWholeRecordOverlayWins()
    {
        var staticRegion = new RegionRecord
        {
            FormId = 0x30,
            Areas =
            [
                new RegionArea(
                    0,
                    [new RegionPoint(0, 0), new RegionPoint(100, 0), new RegionPoint(0, 100)])
            ],
            WeatherTypes = [new RegionWeatherType(0x40, 100, 0)]
        };
        var pluginOverride = new RegionRecord
        {
            FormId = staticRegion.FormId,
            EditorId = "PluginOverride",
            Areas = [],
            WeatherTypes = [],
            IsRuntimeOnly = false
        };

        var merged = new RecordCollection { Regions = [staticRegion] }
            .MergeWith(new RecordCollection { Regions = [pluginOverride] });

        Assert.Same(pluginOverride, Assert.Single(merged.Regions));
    }
}