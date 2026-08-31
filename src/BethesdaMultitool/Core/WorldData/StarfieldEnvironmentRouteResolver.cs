using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.WorldData;

internal enum StarfieldEnvironmentRouteStatus
{
    Resolved,
    PlanetNotFound,
    PlanetAmbiguous,
    MissingAtmosphereReference,
    AtmosphereResolutionFailed,
    MissingClimateReference,
    ClimateNotFound,
    NoWeatherSettingsChoice,
    WeatherSettingsChoiceAmbiguous,
    WeatherSettingsChoiceNotSelectable,
    WeatherSettingsResolutionFailed
}

/// <summary>
///     Exact authored records selected by the currently proven Starfield planet-weather route.
///     This result carries no claim about CE2 weather randomisation, time transitions, atmosphere
///     scattering, cloud integration, or sun-placement equations.
/// </summary>
internal sealed record StarfieldEnvironmentRoute(
    StarfieldEnvironmentRouteStatus Status,
    uint WorldspaceFormId,
    StarfieldPlanetWorldspaceCandidate? PlanetCandidate,
    StarfieldAtmosphereResolution? Atmosphere,
    ClimateRecord? Climate,
    ClimateWeatherSettingsEntry? WeatherSettingsChoice,
    StarfieldWeatherSettingsResolution? WeatherSettings,
    string? FailureDetail = null)
{
    internal bool IsResolved =>
        Status == StarfieldEnvironmentRouteStatus.Resolved &&
        PlanetCandidate is not null &&
        Atmosphere?.IsResolved == true &&
        Climate is not null &&
        WeatherSettingsChoice is not null &&
        WeatherSettings?.IsResolved == true;

    internal uint? SunPresetOverrideFormId => Atmosphere?.EffectivePatch?.SunPresetOverrideFormId;
    internal uint? VolumetricLightingFormId => WeatherSettings?.EffectivePatch?.VolumetricLightingFormId;
    internal uint? CloudFormId => WeatherSettings?.EffectivePatch?.CloudsFormId;
}

/// <summary>
///     Resolves the source-backed static Starfield route
///     WRLD ← PNDT → ATMO → CLMT → WTHS. Only a single ungated WSLT entry can be selected without
///     inventing CE2's runtime weighted/global/transition algorithm; every ambiguous case fails
///     closed and remains observable.
/// </summary>
internal static class StarfieldEnvironmentRouteResolver
{
    internal static StarfieldEnvironmentRoute Resolve(
        uint worldspaceFormId,
        StarfieldPlanetWorldspaceIndexResult planetIndex,
        IReadOnlyDictionary<uint, StarfieldAtmosphereRecord> atmospheresByFormId,
        IReadOnlyDictionary<uint, ClimateRecord> climatesByFormId,
        IReadOnlyDictionary<uint, StarfieldWeatherSettingsRecord> weatherSettingsByFormId)
    {
        ArgumentNullException.ThrowIfNull(planetIndex);
        ArgumentNullException.ThrowIfNull(atmospheresByFormId);
        ArgumentNullException.ThrowIfNull(climatesByFormId);
        ArgumentNullException.ThrowIfNull(weatherSettingsByFormId);

        if (!planetIndex.CandidatesByWorldspaceFormId.TryGetValue(worldspaceFormId, out var candidates) ||
            candidates.Count == 0)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.PlanetNotFound,
                $"WRLD {worldspaceFormId:X8} has no resolved PNDT inverse candidate.");
        }

        if (candidates.Count != 1)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.PlanetAmbiguous,
                $"WRLD {worldspaceFormId:X8} has {candidates.Count} PNDT inverse candidates.");
        }

        var planetCandidate = candidates[0];
        var atmosphereFormId = planetCandidate.Planet.Body.Atmosphere.AtmosphereFormId;
        if (atmosphereFormId == 0)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.MissingAtmosphereReference,
                $"PNDT {planetCandidate.Planet.FormId:X8} authors a null ATMO reference.",
                planetCandidate);
        }

        var atmosphere = StarfieldAtmosphereResolver.Resolve(atmosphereFormId, atmospheresByFormId);
        if (!atmosphere.IsResolved)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.AtmosphereResolutionFailed,
                atmosphere.FailureDetail ?? $"ATMO {atmosphereFormId:X8} did not resolve.",
                planetCandidate,
                atmosphere);
        }

        if (atmosphere.EffectivePatch!.ClimateOverrideFormId is not { } climateFormId ||
            climateFormId == 0)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.MissingClimateReference,
                $"Effective ATMO {atmosphereFormId:X8} has no nonzero climate override.",
                planetCandidate,
                atmosphere);
        }

        if (!climatesByFormId.TryGetValue(climateFormId, out var climate))
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.ClimateNotFound,
                $"ATMO climate override {climateFormId:X8} is absent from the merged CLMT index.",
                planetCandidate,
                atmosphere);
        }

        var choices = climate.WeatherSettingsTypes;
        if (choices is null || choices.Count == 0)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.NoWeatherSettingsChoice,
                $"CLMT {climate.FormId:X8} has no WSLT entries.",
                planetCandidate,
                atmosphere,
                climate);
        }

        if (choices.Count != 1)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.WeatherSettingsChoiceAmbiguous,
                $"CLMT {climate.FormId:X8} has {choices.Count} WSLT entries; runtime selection is not inferred.",
                planetCandidate,
                atmosphere,
                climate);
        }

        var choice = choices[0];
        if (choice.WeatherSettingsFormId == 0 || choice.Chance <= 0 || choice.GlobalFormId != 0)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.WeatherSettingsChoiceNotSelectable,
                $"CLMT {climate.FormId:X8} sole WSLT entry is null, non-positive, or global-gated.",
                planetCandidate,
                atmosphere,
                climate,
                choice);
        }

        var weatherSettings = StarfieldWeatherSettingsResolver.Resolve(
            choice.WeatherSettingsFormId,
            weatherSettingsByFormId);
        if (!weatherSettings.IsResolved)
        {
            return Fail(
                StarfieldEnvironmentRouteStatus.WeatherSettingsResolutionFailed,
                weatherSettings.FailureDetail ??
                $"WTHS {choice.WeatherSettingsFormId:X8} did not resolve.",
                planetCandidate,
                atmosphere,
                climate,
                choice,
                weatherSettings);
        }

        return new StarfieldEnvironmentRoute(
            StarfieldEnvironmentRouteStatus.Resolved,
            worldspaceFormId,
            planetCandidate,
            atmosphere,
            climate,
            choice,
            weatherSettings);

        StarfieldEnvironmentRoute Fail(
            StarfieldEnvironmentRouteStatus status,
            string detail,
            StarfieldPlanetWorldspaceCandidate? candidate = null,
            StarfieldAtmosphereResolution? atmosphereResolution = null,
            ClimateRecord? selectedClimate = null,
            ClimateWeatherSettingsEntry? selectedChoice = null,
            StarfieldWeatherSettingsResolution? weatherResolution = null)
        {
            return new StarfieldEnvironmentRoute(
                status,
                worldspaceFormId,
                candidate,
                atmosphereResolution,
                selectedClimate,
                selectedChoice,
                weatherResolution,
                detail);
        }
    }
}
