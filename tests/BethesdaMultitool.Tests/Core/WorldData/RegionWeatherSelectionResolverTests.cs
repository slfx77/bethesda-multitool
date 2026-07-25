using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Runtime;
using Xunit;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class RegionWeatherSelectionResolverTests
{
    private const uint WorldspaceFormId = 0x10;
    private const uint CellFormId = 0x20;
    private const uint RegionFormId = 0x30;
    private const uint WeatherFormId = 0x40;

    [Fact]
    public void Resolve_ContainingPolygonAndSingleCertainUngatedWeather_UsesRegionWeather()
    {
        var weather = Weather(WeatherFormId, "RegionClear");
        var region = Region(
            RegionFormId,
            [new RegionWeatherType(weather.FormId, 100, 0)],
            Square(0, 0, 100, 100));

        var result = Resolve(Cell(RegionFormId), 50, 50, [region], [weather]);

        Assert.Same(weather, result.Weather);
        Assert.True(result.UsesRegionWeather);
        Assert.Equal(RegionWeatherSelectionStatus.Resolved, result.Status);
        Assert.Equal(RegionFormId, result.RegionFormId);
        Assert.Contains("regionWeatherSource=region-rdwt", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_XclrCandidateOutsideItsPolygon_FallsBackToClimate()
    {
        var weather = Weather(WeatherFormId, "RegionClear");
        var region = Region(
            RegionFormId,
            [new RegionWeatherType(weather.FormId, 100, 0)],
            Square(0, 0, 25, 25));

        var result = Resolve(Cell(RegionFormId), 75, 75, [region], [weather]);

        Assert.Null(result.Weather);
        Assert.Equal(RegionWeatherSelectionStatus.NoContainingWeatherRegion, result.Status);
        Assert.Contains("reason=outside-region-polygons", result.Telemetry, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_PointOnPolygonEdge_IsContained()
    {
        var weather = Weather(WeatherFormId, "RegionClear");
        var region = Region(
            RegionFormId,
            [new RegionWeatherType(weather.FormId, 100, 0)],
            Square(0, 0, 100, 100));

        var result = Resolve(Cell(RegionFormId), 0, 50, [region], [weather]);

        Assert.Equal(RegionWeatherSelectionStatus.Resolved, result.Status);
    }

    [Fact]
    public void Resolve_MultipleContainingWeatherRegions_IsAmbiguousEvenWhenEachIsCertain()
    {
        var firstWeather = Weather(0x41, "First");
        var secondWeather = Weather(0x42, "Second");
        var first = Region(0x31, [new RegionWeatherType(firstWeather.FormId, 100, 0)],
            Square(0, 0, 100, 100));
        var second = Region(0x32, [new RegionWeatherType(secondWeather.FormId, 100, 0)],
            Square(25, 25, 125, 125));

        var result = Resolve(
            Cell(first.FormId, second.FormId), 50, 50,
            [first, second], [firstWeather, secondWeather]);

        Assert.Null(result.Weather);
        Assert.Equal(RegionWeatherSelectionStatus.MultipleContainingWeatherRegions, result.Status);
    }

    [Fact]
    public void Resolve_WeightedOrGlobalGatedLists_FailClosed()
    {
        var weather = Weather(WeatherFormId, "RegionClear");
        var weighted = Region(
            RegionFormId,
            [new RegionWeatherType(weather.FormId, 85, 0)],
            Square(0, 0, 100, 100));
        var gated = Region(
            RegionFormId,
            [new RegionWeatherType(weather.FormId, 100, 0x50)],
            Square(0, 0, 100, 100));

        Assert.Equal(
            RegionWeatherSelectionStatus.WeightedWeather,
            Resolve(Cell(RegionFormId), 50, 50, [weighted], [weather]).Status);
        Assert.Equal(
            RegionWeatherSelectionStatus.GlobalGatedWeather,
            Resolve(Cell(RegionFormId), 50, 50, [gated], [weather]).Status);
    }

    [Fact]
    public void Resolve_UnresolvedXclrCandidate_FailsClosedEvenBesideValidRegion()
    {
        var weather = Weather(WeatherFormId, "RegionClear");
        var valid = Region(
            RegionFormId,
            [new RegionWeatherType(weather.FormId, 100, 0)],
            Square(0, 0, 100, 100));

        var result = Resolve(
            Cell(RegionFormId, 0xDEADBEEF), 50, 50,
            [valid], [weather]);

        Assert.Null(result.Weather);
        Assert.Equal(RegionWeatherSelectionStatus.UnresolvedRegion, result.Status);
        Assert.Equal(0xDEADBEEFu, result.RegionFormId);
    }

    [Fact]
    public void Resolve_RuntimeOnlyCandidateWithoutStaticGeometry_FailsClosed()
    {
        var runtimeOnly = new RegionRecord
        {
            FormId = RegionFormId,
            WorldspaceFormId = WorldspaceFormId,
            // RuntimeRegionReader cannot recover RPLI/RPLD or RDAT.
            Areas = [],
            WeatherTypes = []
        };

        var result = Resolve(Cell(RegionFormId), 50, 50, [runtimeOnly], []);

        Assert.Equal(RegionWeatherSelectionStatus.MissingOrInvalidGeometry, result.Status);
        Assert.Null(result.Weather);
    }

    [Fact]
    public void TransitionResolver_RegionWeatherOnlyWinsStaticClimateDefault()
    {
        var climate = Weather(0x41, "Climate");
        var region = Weather(0x42, "Region");
        var explicitPreview = Weather(0x43, "Explicit");
        var runtime = Weather(0x44, "Runtime");
        var index = new Dictionary<uint, WeatherRecord> { [runtime.FormId] = runtime };

        var staticDefault = WeatherTransitionSelectionResolver.Resolve(
            climate,
            true,
            climate,
            null,
            index,
            region);
        var explicitResult = WeatherTransitionSelectionResolver.Resolve(
            explicitPreview,
            false,
            climate,
            null,
            index,
            region);
        var runtimeResult = WeatherTransitionSelectionResolver.Resolve(
            climate,
            true,
            climate,
            Snapshot(runtime.FormId),
            index,
            region);

        Assert.Same(region, staticDefault.CurrentWeather);
        Assert.Contains("weatherSource=region-rdwt", staticDefault.Telemetry, StringComparison.Ordinal);
        Assert.Same(explicitPreview, explicitResult.CurrentWeather);
        Assert.Same(runtime, runtimeResult.CurrentWeather);
        Assert.True(runtimeResult.UsesRuntimeTransition);
    }

    private static ResolvedRegionWeatherSelection Resolve(
        CellRecord cell,
        float x,
        float y,
        IReadOnlyList<RegionRecord> regions,
        IReadOnlyList<WeatherRecord> weathers)
    {
        return RegionWeatherSelectionResolver.Resolve(
            cell,
            x,
            y,
            WorldspaceFormId,
            regions.ToDictionary(region => region.FormId),
            weathers.ToDictionary(weather => weather.FormId));
    }

    private static CellRecord Cell(params uint[] regionFormIds)
    {
        return new CellRecord
        {
            FormId = CellFormId,
            WorldspaceFormId = WorldspaceFormId,
            GridX = 0,
            GridY = 0,
            RadiationRegionFormIds = regionFormIds
        };
    }

    private static RegionRecord Region(
        uint formId,
        IReadOnlyList<RegionWeatherType> weatherTypes,
        IReadOnlyList<RegionPoint> points)
    {
        return new RegionRecord
        {
            FormId = formId,
            WorldspaceFormId = WorldspaceFormId,
            WeatherTypes = weatherTypes,
            Areas = [new RegionArea(0, points)]
        };
    }

    private static WeatherRecord Weather(uint formId, string editorId)
    {
        return new WeatherRecord
        {
            FormId = formId,
            EditorId = editorId
        };
    }

    private static IReadOnlyList<RegionPoint> Square(float minX, float minY, float maxX, float maxY)
    {
        return
        [
            new RegionPoint(minX, minY),
            new RegionPoint(maxX, minY),
            new RegionPoint(maxX, maxY),
            new RegionPoint(minX, maxY)
        ];
    }

    private static WeatherTransitionSnapshot Snapshot(uint currentWeatherFormId)
    {
        return new WeatherTransitionSnapshot(
            0x40000100,
            0x40000400,
            currentWeatherFormId,
            0x40000440,
            currentWeatherFormId,
            1f,
            null);
    }
}