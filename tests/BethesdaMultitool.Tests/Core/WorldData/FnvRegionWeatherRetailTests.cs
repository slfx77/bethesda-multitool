using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Core.Formats.Esm;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

/// <summary>
///     Retail authority for a deterministic WastelandNV region-weather point. CELL XCLR lists
///     three coarse candidates at (6,-26), but only the retained Searchlight polygon contains a
///     weather RDAT block and its sole RDWT entry is ungated with chance 100.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
[Trait("Category", BucketBTestGuard.Category)]
public sealed class FnvRegionWeatherRetailTests(SampleFileFixture samples)
{
    private const uint WastelandNvFormId = 0x000DA726;
    private const uint SearchlightCellFormId = 0x000DEBF9;
    private const uint SearchlightRegionFormId = 0x001711C3;
    private const uint SearchlightWeatherFormId = 0x00173567;

    [Fact]
    public void SearchlightCellCenter_ResolvesPolygonContainedCertainRegionWeather()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        Assert.SkipWhen(samples.PcFinalEsm is null, "PC final FalloutNV.esm not available");

        var collection = PcFinalEsmPipelineCache.GetOrBuild(samples.PcFinalEsm!).Collection;
        var worldspace = Assert.Single(
            collection.Worldspaces,
            world => world.FormId == WastelandNvFormId);
        var cell = Assert.Single(
            worldspace.Cells,
            candidate => candidate.FormId == SearchlightCellFormId);
        var regions = collection.Regions
            .GroupBy(region => region.FormId)
            .ToDictionary(group => group.Key, group => group.Last());
        var weathers = collection.Weather
            .GroupBy(weather => weather.FormId)
            .ToDictionary(group => group.Key, group => group.Last());

        Assert.Equal((6, -26), (cell.GridX, cell.GridY));
        Assert.Equal(3, cell.RegionFormIds.Count);
        Assert.Contains(SearchlightRegionFormId, cell.RegionFormIds);
        Assert.All(cell.RegionFormIds, regionFormId => Assert.True(regions.ContainsKey(regionFormId)));

        var weatherRegions = cell.RegionFormIds
            .Select(regionFormId => regions[regionFormId])
            .Where(region => region.WeatherTypes.Count > 0)
            .ToArray();
        var searchlight = Assert.Single(weatherRegions);
        Assert.Equal(SearchlightRegionFormId, searchlight.FormId);
        var area = Assert.Single(searchlight.Areas);
        Assert.Equal(1024u, area.EdgeFalloff);
        Assert.Equal(4, area.Points.Count);
        Assert.Equal(
            new RegionWeatherType(
                SearchlightWeatherFormId, 100, 0),
            Assert.Single(searchlight.WeatherTypes));

        // Center of exterior cell (6,-26): (6.5*4096, -25.5*4096).
        const float cameraX = 26_624f;
        const float cameraY = -104_448f;
        var selection = RegionWeatherSelectionResolver.Resolve(
            cell,
            cameraX,
            cameraY,
            worldspace.FormId,
            regions,
            weathers);

        Assert.Equal(RegionWeatherSelectionStatus.Resolved, selection.Status);
        Assert.Equal(SearchlightRegionFormId, selection.RegionFormId);
        Assert.Equal(SearchlightWeatherFormId, selection.Weather?.FormId);
        Assert.Equal("NVSearchlightWeather2", selection.Weather?.EditorId);
    }
}