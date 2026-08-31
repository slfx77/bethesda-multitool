using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.WorldData;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.App;

public sealed class StarfieldEnvironmentCaptureTelemetryTests
{
    [Fact]
    public void VolumetricLighting_ProjectsEveryTypedFloat()
    {
        const uint formId = 0x00064D26;
        var record = new StarfieldVolumetricLightingRecord
        {
            FormId = formId,
            EditorId = "EarthVolumetricLighting",
            Settings = VolumetricSettings()
        };

        var telemetry = StarfieldEnvironmentCaptureTelemetry.ProjectVolumetricLighting(
            formId,
            new Dictionary<uint, StarfieldVolumetricLightingRecord> { [formId] = record });

        Assert.Equal("0x00064D26", telemetry["referencedFormId"]);
        Assert.True(Assert.IsType<bool>(telemetry["found"]));
        Assert.Equal("EarthVolumetricLighting", telemetry["editorId"]);
        Assert.Equal("decoded", telemetry["decodeStatus"]);
        Assert.Null(telemetry["decodeFailure"]);

        var settings = Object(telemetry, "settings");
        Assert.Equal(
            new[] { "exteriorAndInterior", "exterior", "distantLighting" },
            settings.Keys);
        Assert.Equal(
            Enumerable.Range(1, 32).Select(value => (float)value),
            FloatLeaves(settings));
    }

    [Fact]
    public void VolumetricLighting_MissingOrInvalidTargetFailsClosed()
    {
        const uint formId = 0x00064D26;
        var missing = StarfieldEnvironmentCaptureTelemetry.ProjectVolumetricLighting(
            formId,
            new Dictionary<uint, StarfieldVolumetricLightingRecord>());

        Assert.False(Assert.IsType<bool>(missing["found"]));
        Assert.Equal("target-missing", missing["decodeStatus"]);
        Assert.Contains("0x00064D26", Assert.IsType<string>(missing["decodeFailure"]),
            StringComparison.Ordinal);
        Assert.Null(missing["settings"]);

        var invalid = StarfieldEnvironmentCaptureTelemetry.ProjectVolumetricLighting(
            formId,
            new Dictionary<uint, StarfieldVolumetricLightingRecord>
            {
                [formId] = new()
                {
                    FormId = formId,
                    EditorId = "RejectedVolumetric",
                    Settings = VolumetricSettings(),
                    DecodeFailure = "retail schema rejected"
                }
            });

        Assert.True(Assert.IsType<bool>(invalid["found"]));
        Assert.Equal("RejectedVolumetric", invalid["editorId"]);
        Assert.Equal("decode-failed", invalid["decodeStatus"]);
        Assert.Equal("retail schema rejected", invalid["decodeFailure"]);
        Assert.Null(invalid["settings"]);
    }

    [Fact]
    public void CloudForm_ProjectsShadowsLayersPlanesTintAndCardSequence()
    {
        const uint formId = 0x00117A28;
        var definition = CloudDefinition();
        var record = new StarfieldCloudFormRecord
        {
            FormId = formId,
            EditorId = "CloudyMistClouds",
            Definition = definition
        };

        var telemetry = StarfieldEnvironmentCaptureTelemetry.ProjectCloudForm(
            formId,
            new Dictionary<uint, StarfieldCloudFormRecord> { [formId] = record });

        Assert.Equal("0x00117A28", telemetry["referencedFormId"]);
        Assert.True(Assert.IsType<bool>(telemetry["found"]));
        Assert.Equal("CloudyMistClouds", telemetry["editorId"]);
        Assert.Equal("decoded", telemetry["decodeStatus"]);
        Assert.Null(telemetry["decodeFailure"]);

        var projected = Object(telemetry, "definition");
        Assert.Equal(1, projected["layerCount"]);
        Assert.Equal(1, projected["planeCount"]);
        Assert.Equal("0x00ABCDEF", projected["cloudCardSequenceFormId"]);

        var shadows = Object(projected, "shadows");
        Assert.True(Assert.IsType<bool>(shadows["enabled"]));
        Assert.Equal("textures/clouds/shadow.dds", shadows["opacityTexture"]);
        Assert.Equal(4f, shadows["windScale"]);

        var layer = Assert.Single(
            Assert.IsType<Dictionary<string, object?>[]>(projected["layers"]));
        Assert.Equal(
            new[]
            {
                "name", "colorTexture", "thicknessTexture", "normalTexture", "opacityTexture",
                "elevationKm", "heightKm", "distanceKm", "thickness", "textureShadowOffset",
                "textureShadowStrength", "normalShadowStrength", "tiling", "verticalTiling",
                "topBlendDistanceKm", "topBlendStartKm", "bottomBlendDistanceKm",
                "bottomBlendStartKm", "windScale", "density", "coverage", "alphaAdd",
                "alphaMultiply", "tint"
            },
            layer.Keys);
        Assert.Equal((uint)8, layer["tiling"]);
        Assert.Equal((uint)9, layer["verticalTiling"]);
        var layerTint = Object(layer, "tint");
        Assert.Equal((byte)19, layerTint["r"]);
        Assert.Equal((byte)22, layerTint["a"]);

        var plane = Assert.Single(
            Assert.IsType<Dictionary<string, object?>[]>(projected["planes"]));
        Assert.Equal(
            new[]
            {
                "name", "colorTexture", "thicknessTexture", "normalTexture", "opacityTexture",
                "elevationKm", "fadeStartKm", "fadeDistanceKm", "thickness",
                "textureShadowOffset", "textureShadowStrength", "normalShadowStrength",
                "tilingPerKm", "windScale", "density", "coverage", "alphaAdd",
                "alphaMultiply", "tint"
            },
            plane.Keys);
        Assert.Equal(8f, plane["tilingPerKm"]);
        var planeTint = Object(plane, "tint");
        Assert.Equal((byte)23, planeTint["r"]);
        Assert.Equal((byte)26, planeTint["a"]);
    }

    [Fact]
    public void CloudForm_MissingInvalidAndZeroTargetsFailClosed()
    {
        const uint formId = 0x00117A28;
        var missing = StarfieldEnvironmentCaptureTelemetry.ProjectCloudForm(
            formId,
            new Dictionary<uint, StarfieldCloudFormRecord>());
        Assert.False(Assert.IsType<bool>(missing["found"]));
        Assert.Equal("target-missing", missing["decodeStatus"]);
        Assert.Null(missing["definition"]);

        var invalid = StarfieldEnvironmentCaptureTelemetry.ProjectCloudForm(
            formId,
            new Dictionary<uint, StarfieldCloudFormRecord>
            {
                [formId] = new()
                {
                    FormId = formId,
                    EditorId = "RejectedClouds",
                    Definition = CloudDefinition(),
                    DecodeFailure = "retail schema rejected"
                }
            });
        Assert.True(Assert.IsType<bool>(invalid["found"]));
        Assert.Equal("decode-failed", invalid["decodeStatus"]);
        Assert.Equal("retail schema rejected", invalid["decodeFailure"]);
        Assert.Null(invalid["definition"]);

        var zero = StarfieldEnvironmentCaptureTelemetry.ProjectCloudForm(
            0,
            new Dictionary<uint, StarfieldCloudFormRecord>
            {
                [0] = new() { FormId = 0, Definition = CloudDefinition() }
            });
        Assert.False(Assert.IsType<bool>(zero["found"]));
        Assert.Equal("0x00000000", zero["referencedFormId"]);
        Assert.Equal("not-referenced", zero["decodeStatus"]);
        Assert.Null(zero["definition"]);
    }

    [Fact]
    public void Atmosphere_ResolvesInheritanceAndLabelsTheNonRenderingBoundary()
    {
        const uint rootFormId = 0x0020CDD3;
        const uint earthFormId = 0x0000C9D1;
        var records = new Dictionary<uint, StarfieldAtmosphereRecord>
        {
            [rootFormId] = new()
            {
                FormId = rootFormId,
                EditorId = "AtmosphereMedium",
                PayloadKind = StarfieldAtmospherePayloadKind.FullObject,
                Patch = new StarfieldAtmospherePatch
                {
                    ParentFormId = 0,
                    SunPresetOverrideFormId = 0,
                    ClimateOverrideFormId = 0
                }
            },
            [earthFormId] = new()
            {
                FormId = earthFormId,
                EditorId = "EarthAtmosphere",
                ParentFormId = rootFormId,
                PayloadKind = StarfieldAtmospherePayloadKind.Diff,
                Patch = new StarfieldAtmospherePatch
                {
                    ParentFormId = rootFormId,
                    ClimateOverrideFormId = 0x00064D14
                }
            }
        };

        var telemetry = StarfieldEnvironmentCaptureTelemetry.ProjectAtmosphere(earthFormId, records);

        Assert.Equal("0x0000C9D1", telemetry["referencedFormId"]);
        Assert.True(Assert.IsType<bool>(telemetry["found"]));
        Assert.Equal("EarthAtmosphere", telemetry["editorId"]);
        Assert.Equal("Resolved", telemetry["resolutionStatus"]);
        Assert.Null(telemetry["resolutionFailure"]);
        Assert.Equal(
            new[] { "0x0020CDD3", "0x0000C9D1" },
            Assert.IsType<string[]>(telemetry["inheritanceChain"]));
        var references = Object(telemetry, "structuralReferences");
        Assert.Equal("0x0020CDD3", references["parentFormId"]);
        Assert.Equal("0x00000000", references["sunPresetOverrideFormId"]);
        Assert.Equal("0x00064D14", references["climateOverrideFormId"]);
        Assert.Equal(
            "decoded/resolved source data only; no CE2 equations applied; no runtime weather-selection claim",
            telemetry["dataScope"]);
        Assert.False(Assert.IsType<bool>(telemetry["ce2EquationsApplied"]));
        Assert.False(Assert.IsType<bool>(telemetry["runtimeWeatherSelectionClaimed"]));
    }

    [Fact]
    public void Atmosphere_MissingCycleAndAbsentSelectionFailClosedWithoutStructuralValues()
    {
        const uint targetFormId = 0x100;
        var missing = StarfieldEnvironmentCaptureTelemetry.ProjectAtmosphere(
            targetFormId,
            new Dictionary<uint, StarfieldAtmosphereRecord>());
        Assert.False(Assert.IsType<bool>(missing["found"]));
        Assert.Equal("TargetNotFound", missing["resolutionStatus"]);
        Assert.Null(missing["structuralReferences"]);

        var cyclic = new Dictionary<uint, StarfieldAtmosphereRecord>
        {
            [targetFormId] = new()
            {
                FormId = targetFormId,
                ParentFormId = 0x101,
                PayloadKind = StarfieldAtmospherePayloadKind.Diff,
                Patch = new StarfieldAtmospherePatch()
            },
            [0x101] = new()
            {
                FormId = 0x101,
                ParentFormId = targetFormId,
                PayloadKind = StarfieldAtmospherePayloadKind.Diff,
                Patch = new StarfieldAtmospherePatch()
            }
        };
        var cycle = StarfieldEnvironmentCaptureTelemetry.ProjectAtmosphere(targetFormId, cyclic);
        Assert.True(Assert.IsType<bool>(cycle["found"]));
        Assert.Equal("InheritanceCycle", cycle["resolutionStatus"]);
        Assert.Null(cycle["structuralReferences"]);
        Assert.Contains("revisits", Assert.IsType<string>(cycle["resolutionFailure"]),
            StringComparison.OrdinalIgnoreCase);

        var absentSelection = StarfieldEnvironmentCaptureTelemetry.ProjectAtmosphere(null, cyclic);
        Assert.Equal("not-referenced", absentSelection["resolutionStatus"]);
        Assert.Contains("does not perform runtime weather selection",
            Assert.IsType<string>(absentSelection["resolutionFailure"]),
            StringComparison.Ordinal);
        Assert.Null(absentSelection["structuralReferences"]);
    }

    [Fact]
    public void SceneCapture_UsesTheLiveStaticRouteAndLabelsTheApproximationBoundary()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        var block = SourceContract.Extract(
            source,
            "var effectiveWeatherSettings = weatherSettingsResolution?.EffectivePatch;",
            "fields[\"climateWeatherSettingsDecodeStatus\"]");

        SourceContract.AssertOrder(
            block,
            "var volumetricLightingTelemetry = weatherSettingsResolution?.IsResolved == true",
            "StarfieldEnvironmentCaptureTelemetry.ProjectVolumetricLighting(",
            "_data?.VolumetricLightingByFormId",
            "var cloudFormTelemetry = weatherSettingsResolution?.IsResolved == true",
            "StarfieldEnvironmentCaptureTelemetry.ProjectCloudForm(",
            "_data?.CloudFormsByFormId",
            "var atmosphereFormId = starfieldEnvironmentRoute?.PlanetCandidate?.Planet.Body.Atmosphere.AtmosphereFormId;",
            "var atmosphereTelemetry = game == Core.Games.BethesdaGame.Starfield",
            "StarfieldEnvironmentCaptureTelemetry.ProjectAtmosphere(",
            "atmosphereFormId",
            "_data?.AtmospheresByFormId",
            "fields[\"starfieldAtmosphereSourceData\"]",
            "fields[\"starfieldEnvironmentRoute\"]",
            "fields[\"starfieldCelestialRoute\"]",
            "fields[\"starfieldEnvironmentRendering\"]",
            "[\"selectionScope\"]",
            "[\"volumetricLighting\"] = volumetricLightingTelemetry",
            "[\"cloudForm\"] = cloudFormTelemetry",
            "[\"renderingStatus\"]");
        Assert.Contains(
            "active unique PNDT→ATMO→CLMT WSLT choice;",
            block,
            StringComparison.Ordinal);
        Assert.Contains(
            "weighted/global/runtime transitions fail closed",
            block,
            StringComparison.Ordinal);
        Assert.Contains(
            "listed WTHS Set channels were projected into viewer atmosphere state",
            block,
            StringComparison.Ordinal);
        Assert.Contains("StarfieldEnvironmentApproximationResult.Name", block, StringComparison.Ordinal);
        Assert.Contains("[\"drawVisibilityProven\"] = false", block, StringComparison.Ordinal);
        Assert.Contains("[\"ce2ParityClaimed\"] = false", block, StringComparison.Ordinal);
        Assert.Contains("weatherSettingsResolution.IsResolved != true", block, StringComparison.Ordinal);
        Assert.Contains("WTHS resolution failed closed", block, StringComparison.Ordinal);
        Assert.Contains("rejectedWeatherChannels != StarfieldEnvironmentApproximationChannels.None", block,
            StringComparison.Ordinal);
        var telemetrySource = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "WorldData",
            "StarfieldEnvironmentCaptureTelemetry.cs");
        Assert.Contains(
            "no active PNDT→ATMO target was supplied; capture telemetry does not perform runtime weather selection",
            telemetrySource,
            StringComparison.Ordinal);
        Assert.Contains(
            "decoded/resolved source data only; no CE2 equations applied; no runtime weather-selection claim",
            telemetrySource,
            StringComparison.Ordinal);

        var atmosphereSource = SourceContract.ReadAppSource("WorldView3DControl.Atmosphere.cs");
        SourceContract.AssertOrder(
            atmosphereSource,
            "private void RefreshStarfieldEnvironmentRoutes(ResolvedSkySceneContext skyContext)",
            "StarfieldEnvironmentRouteResolver.Resolve(",
            "StarfieldCelestialRouteResolver.Resolve(",
            "private static StarfieldSunPresetPatch? StarfieldSunPresetForRenderingApproximation(",
            "SunPresetOverrideFormId is",
            "celestial.PrimaryStar?.EffectiveSunPreset",
            "StarfieldEnvironmentRenderingApproximation.Apply(",
            "StarfieldSunPresetForRenderingApproximation(environment, celestial));",
            "RefreshStarfieldEnvironmentRoutes(skyContext);",
            "Sun: starfieldSunDisk ?? climate?.SunTexture");
    }

    [Fact]
    public void LiveRoute_RejectsStaleDatasetsAndUnknownAtmosphereOverrides()
    {
        var atmosphere = SourceContract.ReadAppSource("WorldView3DControl.Atmosphere.cs");
        var currentEnvironment = SourceContract.Extract(
            atmosphere,
            "private StarfieldEnvironmentRoute? CurrentStarfieldEnvironmentRoute(",
            "private StarfieldCelestialRoute? CurrentStarfieldCelestialRoute(");
        Assert.Contains("route.WorldspaceFormId == worldspace.FormId", currentEnvironment,
            StringComparison.Ordinal);

        var currentCelestial = SourceContract.Extract(
            atmosphere,
            "private StarfieldCelestialRoute? CurrentStarfieldCelestialRoute(",
            "private ClimateRecord? ResolveClimate(");
        Assert.Contains(
            "route.PlanetCandidate.Worldspace.WorldspaceFormId == worldspace.FormId",
            currentCelestial,
            StringComparison.Ordinal);

        var sunSelection = SourceContract.Extract(
            atmosphere,
            "private static StarfieldSunPresetPatch? StarfieldSunPresetForRenderingApproximation(",
            "private ResolvedWeatherTransition ResolveSelectedWeatherTransition(");
        Assert.Contains(
            "environment.Status != StarfieldEnvironmentRouteStatus.MissingAtmosphereReference",
            sunSelection,
            StringComparison.Ordinal);
        Assert.Contains("environment.Atmosphere?.IsResolved != true", sunSelection,
            StringComparison.Ordinal);
        Assert.Contains("overrideFormId != 0", sunSelection, StringComparison.Ordinal);
        Assert.Contains("return route.WeatherSettings;", atmosphere, StringComparison.Ordinal);

        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        Assert.Contains(
            "var starfieldEnvironmentRoute = CurrentStarfieldEnvironmentRoute(skyContext);",
            capture,
            StringComparison.Ordinal);
        Assert.Contains(
            "var starfieldCelestialRoute = CurrentStarfieldCelestialRoute(skyContext);",
            capture,
            StringComparison.Ordinal);
        Assert.Contains(": climate is null", capture, StringComparison.Ordinal);
        Assert.Contains(
            "no resolved CLMT for the active Starfield environment route",
            capture,
            StringComparison.Ordinal);
        Assert.Contains(
            "Resolved Starfield CLMT has no sky MODL",
            capture,
            StringComparison.Ordinal);

        var control = SourceContract.ReadAppSource("WorldView3DControl.xaml.cs");
        SourceContract.AssertOrder(
            control,
            "_starfieldEnvironmentRoute = null;",
            "_starfieldCelestialRoute = null;",
            "_data = data;");
    }

    private static StarfieldVolumetricLightingSettings VolumetricSettings() => new(
        new StarfieldVolumetricExteriorAndInteriorSettings(1f, 2f, 3f, 4f),
        new StarfieldVolumetricExteriorSettings(
            new StarfieldVolumetricFogThicknessSettings(5f, 6f, 7f, 8f),
            new StarfieldVolumetricFogDensitySettings(9f, 10f, 11f, 12f, 13f, 14f, 15f),
            new StarfieldVolumetricHorizonFogSettings(16f, 17f, 18f, 19f),
            new StarfieldVolumetricFogMapSettings(
                20f,
                21f,
                new StarfieldVolumetricFloat4(22f, 23f, 24f, 25f),
                26f,
                27f,
                28f,
                29f,
                30f)),
        new StarfieldVolumetricDistantLightingSettings(31f, 32f));

    private static StarfieldCloudFormDefinition CloudDefinition() => new(
        new StarfieldCloudShadowParams(
            true,
            "textures/clouds/shadow.dds",
            1f,
            2f,
            3f,
            4f),
        [
            new StarfieldCloudLayer(
                "Layer",
                "layer-color.dds",
                "layer-thickness.dds",
                "layer-normal.dds",
                "layer-opacity.dds",
                1f,
                2f,
                3f,
                4f,
                5f,
                6f,
                7f,
                8,
                9,
                10f,
                11f,
                12f,
                13f,
                14f,
                15f,
                16f,
                17f,
                18f,
                new StarfieldCloudTint(19, 20, 21, 22))
        ],
        [
            new StarfieldCloudPlane(
                "Plane",
                "plane-color.dds",
                "plane-thickness.dds",
                "plane-normal.dds",
                "plane-opacity.dds",
                1f,
                2f,
                3f,
                4f,
                5f,
                6f,
                7f,
                8f,
                9f,
                10f,
                11f,
                12f,
                13f,
                new StarfieldCloudTint(23, 24, 25, 26))
        ],
        0x00ABCDEF);

    private static Dictionary<string, object?> Object(
        IReadOnlyDictionary<string, object?> parent,
        string key) => Assert.IsType<Dictionary<string, object?>>(parent[key]);

    private static IEnumerable<float> FloatLeaves(object? value)
    {
        if (value is float number)
        {
            yield return number;
            yield break;
        }

        if (value is IReadOnlyDictionary<string, object?> dictionary)
        {
            foreach (var child in dictionary.Values)
            {
                foreach (var leaf in FloatLeaves(child))
                {
                    yield return leaf;
                }
            }
        }
    }
}
