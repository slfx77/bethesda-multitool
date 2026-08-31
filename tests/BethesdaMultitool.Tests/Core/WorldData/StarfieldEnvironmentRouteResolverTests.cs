using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Parsing;
using BethesdaMultitool.Core.WorldData;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.StarfieldPlanetDataTestData;

namespace BethesdaMultitool.Tests.Core.WorldData;

public sealed class StarfieldEnvironmentRouteResolverTests
{
    private const uint Worldspace = 0x100;
    private const uint Planet = 0x200;
    private const uint Atmosphere = 0x300;
    private const uint Climate = 0x400;
    private const uint WeatherSettings = 0x500;

    [Fact]
    public void Resolve_ConnectsUniqueEarthLikeStaticRoute()
    {
        var route = StarfieldEnvironmentRouteResolver.Resolve(
            Worldspace,
            PlanetIndex(),
            Atmospheres(),
            Climates(new ClimateWeatherSettingsEntry(WeatherSettings, 100, 0)),
            WeatherSettingsRecords());

        Assert.True(route.IsResolved, route.FailureDetail);
        Assert.Equal(StarfieldEnvironmentRouteStatus.Resolved, route.Status);
        Assert.Equal(Planet, route.PlanetCandidate!.Planet.FormId);
        Assert.Equal(Atmosphere, route.Atmosphere!.TargetFormId);
        Assert.Equal(Climate, route.Climate!.FormId);
        Assert.Equal(WeatherSettings, route.WeatherSettings!.TargetFormId);
        Assert.Equal(0u, route.SunPresetOverrideFormId);
        Assert.Equal(0x600u, route.VolumetricLightingFormId);
        Assert.Equal(0x700u, route.CloudFormId);
    }

    [Fact]
    public void Resolve_PreservesAmbiguousPlanetInsteadOfPickingOne()
    {
        var first = PlanetRecord(Planet);
        var second = PlanetRecord(Planet + 1);
        var index = StarfieldPlanetWorldspaceIndex.Build([first, second]);

        var route = StarfieldEnvironmentRouteResolver.Resolve(
            Worldspace,
            index,
            Atmospheres(),
            Climates(new ClimateWeatherSettingsEntry(WeatherSettings, 100, 0)),
            WeatherSettingsRecords());

        Assert.False(route.IsResolved);
        Assert.Equal(StarfieldEnvironmentRouteStatus.PlanetAmbiguous, route.Status);
        Assert.Contains("2 PNDT", route.FailureDetail ?? string.Empty);
    }

    [Fact]
    public void Resolve_RejectsUnprovenWeightedOrGlobalSelection()
    {
        var weighted = StarfieldEnvironmentRouteResolver.Resolve(
            Worldspace,
            PlanetIndex(),
            Atmospheres(),
            Climates(
                new ClimateWeatherSettingsEntry(WeatherSettings, 50, 0),
                new ClimateWeatherSettingsEntry(WeatherSettings + 1, 50, 0)),
            WeatherSettingsRecords());
        var gated = StarfieldEnvironmentRouteResolver.Resolve(
            Worldspace,
            PlanetIndex(),
            Atmospheres(),
            Climates(new ClimateWeatherSettingsEntry(WeatherSettings, 100, 0x900)),
            WeatherSettingsRecords());

        Assert.Equal(StarfieldEnvironmentRouteStatus.WeatherSettingsChoiceAmbiguous, weighted.Status);
        Assert.Equal(StarfieldEnvironmentRouteStatus.WeatherSettingsChoiceNotSelectable, gated.Status);
    }

    [Fact]
    public void Resolve_PropagatesAtmosphereFailureWithoutFallingBackToWrldClimate()
    {
        var route = StarfieldEnvironmentRouteResolver.Resolve(
            Worldspace,
            PlanetIndex(),
            new Dictionary<uint, StarfieldAtmosphereRecord>(),
            Climates(new ClimateWeatherSettingsEntry(WeatherSettings, 100, 0)),
            WeatherSettingsRecords());

        Assert.False(route.IsResolved);
        Assert.Equal(StarfieldEnvironmentRouteStatus.AtmosphereResolutionFailed, route.Status);
        Assert.Equal(StarfieldAtmosphereResolutionStatus.TargetNotFound, route.Atmosphere!.Status);
        Assert.Null(route.Climate);
    }

    private static StarfieldPlanetWorldspaceIndexResult PlanetIndex() =>
        StarfieldPlanetWorldspaceIndex.Build([PlanetRecord(Planet)]);

    private static StarfieldPlanetDataRecord PlanetRecord(uint formId)
    {
        var entry = new StarfieldPlanetWorldspaceEntry(29.7604d, -95.3698d, Worldspace);
        Assert.True(StarfieldPlanetDataDecoder.TryDecode(
            ValidMasterData([entry], atmosphereFormId: Atmosphere),
            false,
            out var record,
            out var error), error);
        return record with { FormId = formId };
    }

    private static IReadOnlyDictionary<uint, StarfieldAtmosphereRecord> Atmospheres() =>
        new Dictionary<uint, StarfieldAtmosphereRecord>
        {
            [Atmosphere] = new()
            {
                FormId = Atmosphere,
                PayloadKind = StarfieldAtmospherePayloadKind.FullObject,
                Patch = new StarfieldAtmospherePatch
                {
                    ParentFormId = 0,
                    SunPresetOverrideFormId = 0,
                    ClimateOverrideFormId = Climate
                }
            }
        };

    private static IReadOnlyDictionary<uint, ClimateRecord> Climates(
        params ClimateWeatherSettingsEntry[] choices) =>
        new Dictionary<uint, ClimateRecord>
        {
            [Climate] = new()
            {
                FormId = Climate,
                WeatherSettingsTypes = choices
            }
        };

    private static IReadOnlyDictionary<uint, StarfieldWeatherSettingsRecord> WeatherSettingsRecords() =>
        new Dictionary<uint, StarfieldWeatherSettingsRecord>
        {
            [WeatherSettings] = new()
            {
                FormId = WeatherSettings,
                PayloadKind = StarfieldWeatherSettingsPayloadKind.FullObject,
                Patch = new StarfieldWeatherSettingsPatch
                {
                    ParentFormId = 0,
                    VolumetricLightingFormId = 0x600,
                    CloudsFormId = 0x700
                }
            }
        };
}
