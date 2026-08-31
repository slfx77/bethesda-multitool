using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;

namespace BethesdaMultitool.Core.WorldData;

/// <summary>
///     Fail-closed capture projection for Starfield environment records, including WTHS-referenced
///     VOLI/CLDF data and explicitly targeted ATMO inheritance. These helpers expose decoded/resolved
///     source values for diagnostics only; they do not select active planet weather or evaluate any
///     Creation Engine 2 rendering equations.
/// </summary>
internal static class StarfieldEnvironmentCaptureTelemetry
{
    internal static Dictionary<string, object?> ProjectAtmosphere(
        uint? referencedFormId,
        IReadOnlyDictionary<uint, StarfieldAtmosphereRecord>? records)
    {
        if (referencedFormId is not uint formId || formId == 0)
        {
            return AtmosphereEnvelope(
                referencedFormId,
                false,
                null,
                "not-referenced",
                "no active PNDT→ATMO target was supplied; capture telemetry does not perform runtime weather selection",
                [],
                null);
        }

        if (records is null)
        {
            return AtmosphereEnvelope(
                formId,
                false,
                null,
                "index-unavailable",
                "the Starfield ATMO load-order index is unavailable",
                [],
                null);
        }

        records.TryGetValue(formId, out var target);
        var resolution = StarfieldAtmosphereResolver.Resolve(formId, records);
        if (!resolution.IsResolved)
        {
            return AtmosphereEnvelope(
                formId,
                target is not null,
                target?.EditorId,
                resolution.Status.ToString(),
                resolution.FailureDetail,
                resolution.InheritanceChain,
                null,
                resolution.FailureFormId);
        }

        return AtmosphereEnvelope(
            formId,
            true,
            target!.EditorId,
            resolution.Status.ToString(),
            null,
            resolution.InheritanceChain,
            AtmosphereReferences(resolution.EffectivePatch!));
    }

    internal static Dictionary<string, object?> ProjectVolumetricLighting(
        uint? referencedFormId,
        IReadOnlyDictionary<uint, StarfieldVolumetricLightingRecord>? records)
    {
        if (referencedFormId is not uint formId || formId == 0)
        {
            return VolumetricEnvelope(referencedFormId, false, null, "not-referenced", null, null);
        }

        if (records is null)
        {
            return VolumetricEnvelope(
                formId,
                false,
                null,
                "index-unavailable",
                "the Starfield VOLI load-order index is unavailable",
                null);
        }

        if (!records.TryGetValue(formId, out var record))
        {
            return VolumetricEnvelope(
                formId,
                false,
                null,
                "target-missing",
                $"referenced VOLI 0x{formId:X8} is absent from the load-order index",
                null);
        }

        if (record.Settings is null || !string.IsNullOrWhiteSpace(record.DecodeFailure))
        {
            return VolumetricEnvelope(
                formId,
                true,
                record.EditorId,
                "decode-failed",
                string.IsNullOrWhiteSpace(record.DecodeFailure)
                    ? "the VOLI record has no typed settings"
                    : record.DecodeFailure,
                null);
        }

        return VolumetricEnvelope(
            formId,
            true,
            record.EditorId,
            "decoded",
            null,
            VolumetricSettings(record.Settings));
    }

    internal static Dictionary<string, object?> ProjectCloudForm(
        uint? referencedFormId,
        IReadOnlyDictionary<uint, StarfieldCloudFormRecord>? records)
    {
        if (referencedFormId is not uint formId || formId == 0)
        {
            return CloudEnvelope(referencedFormId, false, null, "not-referenced", null, null);
        }

        if (records is null)
        {
            return CloudEnvelope(
                formId,
                false,
                null,
                "index-unavailable",
                "the Starfield CLDF load-order index is unavailable",
                null);
        }

        if (!records.TryGetValue(formId, out var record))
        {
            return CloudEnvelope(
                formId,
                false,
                null,
                "target-missing",
                $"referenced CLDF 0x{formId:X8} is absent from the load-order index",
                null);
        }

        if (record.Definition is null || !string.IsNullOrWhiteSpace(record.DecodeFailure))
        {
            return CloudEnvelope(
                formId,
                true,
                record.EditorId,
                "decode-failed",
                string.IsNullOrWhiteSpace(record.DecodeFailure)
                    ? "the CLDF record has no typed definition"
                    : record.DecodeFailure,
                null);
        }

        return CloudEnvelope(
            formId,
            true,
            record.EditorId,
            "decoded",
            null,
            CloudDefinition(record.Definition));
    }

    private static Dictionary<string, object?> VolumetricEnvelope(
        uint? referencedFormId,
        bool found,
        string? editorId,
        string decodeStatus,
        string? decodeFailure,
        Dictionary<string, object?>? settings) => new()
    {
        ["referencedFormId"] = FormId(referencedFormId),
        ["found"] = found,
        ["editorId"] = editorId,
        ["decodeStatus"] = decodeStatus,
        ["decodeFailure"] = decodeFailure,
        ["settings"] = settings
    };

    private static Dictionary<string, object?> CloudEnvelope(
        uint? referencedFormId,
        bool found,
        string? editorId,
        string decodeStatus,
        string? decodeFailure,
        Dictionary<string, object?>? definition) => new()
    {
        ["referencedFormId"] = FormId(referencedFormId),
        ["found"] = found,
        ["editorId"] = editorId,
        ["decodeStatus"] = decodeStatus,
        ["decodeFailure"] = decodeFailure,
        ["definition"] = definition
    };

    private static Dictionary<string, object?> AtmosphereEnvelope(
        uint? referencedFormId,
        bool found,
        string? editorId,
        string resolutionStatus,
        string? resolutionFailure,
        IReadOnlyList<uint> inheritanceChain,
        Dictionary<string, object?>? structuralReferences,
        uint? failureFormId = null) => new()
    {
        ["referencedFormId"] = FormId(referencedFormId),
        ["found"] = found,
        ["editorId"] = editorId,
        ["resolutionStatus"] = resolutionStatus,
        ["resolutionFailure"] = resolutionFailure,
        ["failureFormId"] = FormId(failureFormId),
        ["inheritanceChain"] = inheritanceChain.Select(formId => FormId(formId)).ToArray(),
        ["structuralReferences"] = structuralReferences,
        ["dataScope"] =
            "decoded/resolved source data only; no CE2 equations applied; no runtime weather-selection claim",
        ["ce2EquationsApplied"] = false,
        ["runtimeWeatherSelectionClaimed"] = false
    };

    private static Dictionary<string, object?> AtmosphereReferences(
        StarfieldAtmospherePatch patch) => new()
    {
        ["parentFormId"] = FormId(patch.ParentFormId),
        ["sunPresetOverrideFormId"] = FormId(patch.SunPresetOverrideFormId),
        ["climateOverrideFormId"] = FormId(patch.ClimateOverrideFormId)
    };

    private static Dictionary<string, object?> VolumetricSettings(
        StarfieldVolumetricLightingSettings settings) => new()
    {
        ["exteriorAndInterior"] = new Dictionary<string, object?>
        {
            ["scatteringVolumeNear"] = settings.ExteriorAndInterior.ScatteringVolumeNear,
            ["scatteringVolumeFar"] = settings.ExteriorAndInterior.ScatteringVolumeFar,
            ["highFrequencyNoiseScale"] = settings.ExteriorAndInterior.HighFrequencyNoiseScale,
            ["highFrequencyNoiseDensityScale"] = settings.ExteriorAndInterior.HighFrequencyNoiseDensityScale
        },
        ["exterior"] = new Dictionary<string, object?>
        {
            ["fogThickness"] = new Dictionary<string, object?>
            {
                ["thicknessNoiseScale"] = settings.Exterior.FogThickness.ThicknessNoiseScale,
                ["thicknessNoiseBias"] = settings.Exterior.FogThickness.ThicknessNoiseBias,
                ["minFogThickness"] = settings.Exterior.FogThickness.MinFogThickness,
                ["maxFogThickness"] = settings.Exterior.FogThickness.MaxFogThickness
            },
            ["fogDensity"] = new Dictionary<string, object?>
            {
                ["densityNoiseScale"] = settings.Exterior.FogDensity.DensityNoiseScale,
                ["densityNoiseBias"] = settings.Exterior.FogDensity.DensityNoiseBias,
                ["minFogDensity"] = settings.Exterior.FogDensity.MinFogDensity,
                ["maxFogDensity"] = settings.Exterior.FogDensity.MaxFogDensity,
                ["densityStartDistance"] = settings.Exterior.FogDensity.DensityStartDistance,
                ["densityFullDistance"] = settings.Exterior.FogDensity.DensityFullDistance,
                ["densityDistanceExponent"] = settings.Exterior.FogDensity.DensityDistanceExponent
            },
            ["horizonFog"] = new Dictionary<string, object?>
            {
                ["fogThickness"] = settings.Exterior.HorizonFog.FogThickness,
                ["fogDensity"] = settings.Exterior.HorizonFog.FogDensity,
                ["densityStartDistance"] = settings.Exterior.HorizonFog.DensityStartDistance,
                ["densityFullDistance"] = settings.Exterior.HorizonFog.DensityFullDistance
            },
            ["fogMap"] = new Dictionary<string, object?>
            {
                ["heightAboveTerrain"] = settings.Exterior.FogMap.HeightAboveTerrain,
                ["terrainMatch"] = settings.Exterior.FogMap.TerrainMatch,
                ["albedo"] = Float4(settings.Exterior.FogMap.Albedo),
                ["anisotropy"] = settings.Exterior.FogMap.Anisotropy,
                ["minMeanFreePath"] = settings.Exterior.FogMap.MinMeanFreePath,
                ["maxMeanFreePath"] = settings.Exterior.FogMap.MaxMeanFreePath,
                ["heightFalloffExponent"] = settings.Exterior.FogMap.HeightFalloffExponent,
                ["span"] = settings.Exterior.FogMap.Span
            }
        },
        ["distantLighting"] = new Dictionary<string, object?>
        {
            ["scatteringTransition"] = settings.DistantLighting.ScatteringTransition,
            ["scatteringFar"] = settings.DistantLighting.ScatteringFar
        }
    };

    private static Dictionary<string, object?> CloudDefinition(StarfieldCloudFormDefinition definition) => new()
    {
        ["shadows"] = new Dictionary<string, object?>
        {
            ["enabled"] = definition.Shadows.Enabled,
            ["opacityTexture"] = definition.Shadows.OpacityTexture,
            ["tilingPerKm"] = definition.Shadows.TilingPerKm,
            ["elevationKm"] = definition.Shadows.ElevationKm,
            ["strength"] = definition.Shadows.Strength,
            ["windScale"] = definition.Shadows.WindScale
        },
        ["layerCount"] = definition.Layers.Count,
        ["planeCount"] = definition.Planes.Count,
        ["layers"] = definition.Layers.Select(CloudLayer).ToArray(),
        ["planes"] = definition.Planes.Select(CloudPlane).ToArray(),
        ["cloudCardSequenceFormId"] = FormId(definition.CloudCardSequenceFormId)
    };

    private static Dictionary<string, object?> CloudLayer(StarfieldCloudLayer layer) => new()
    {
        ["name"] = layer.Name,
        ["colorTexture"] = layer.ColorTexture,
        ["thicknessTexture"] = layer.ThicknessTexture,
        ["normalTexture"] = layer.NormalTexture,
        ["opacityTexture"] = layer.OpacityTexture,
        ["elevationKm"] = layer.ElevationKm,
        ["heightKm"] = layer.HeightKm,
        ["distanceKm"] = layer.DistanceKm,
        ["thickness"] = layer.Thickness,
        ["textureShadowOffset"] = layer.TextureShadowOffset,
        ["textureShadowStrength"] = layer.TextureShadowStrength,
        ["normalShadowStrength"] = layer.NormalShadowStrength,
        ["tiling"] = layer.Tiling,
        ["verticalTiling"] = layer.VerticalTiling,
        ["topBlendDistanceKm"] = layer.TopBlendDistanceKm,
        ["topBlendStartKm"] = layer.TopBlendStartKm,
        ["bottomBlendDistanceKm"] = layer.BottomBlendDistanceKm,
        ["bottomBlendStartKm"] = layer.BottomBlendStartKm,
        ["windScale"] = layer.WindScale,
        ["density"] = layer.Density,
        ["coverage"] = layer.Coverage,
        ["alphaAdd"] = layer.AlphaAdd,
        ["alphaMultiply"] = layer.AlphaMultiply,
        ["tint"] = CloudTint(layer.Tint)
    };

    private static Dictionary<string, object?> CloudPlane(StarfieldCloudPlane plane) => new()
    {
        ["name"] = plane.Name,
        ["colorTexture"] = plane.ColorTexture,
        ["thicknessTexture"] = plane.ThicknessTexture,
        ["normalTexture"] = plane.NormalTexture,
        ["opacityTexture"] = plane.OpacityTexture,
        ["elevationKm"] = plane.ElevationKm,
        ["fadeStartKm"] = plane.FadeStartKm,
        ["fadeDistanceKm"] = plane.FadeDistanceKm,
        ["thickness"] = plane.Thickness,
        ["textureShadowOffset"] = plane.TextureShadowOffset,
        ["textureShadowStrength"] = plane.TextureShadowStrength,
        ["normalShadowStrength"] = plane.NormalShadowStrength,
        ["tilingPerKm"] = plane.TilingPerKm,
        ["windScale"] = plane.WindScale,
        ["density"] = plane.Density,
        ["coverage"] = plane.Coverage,
        ["alphaAdd"] = plane.AlphaAdd,
        ["alphaMultiply"] = plane.AlphaMultiply,
        ["tint"] = CloudTint(plane.Tint)
    };

    private static Dictionary<string, object?> Float4(StarfieldVolumetricFloat4 value) => new()
    {
        ["x"] = value.X,
        ["y"] = value.Y,
        ["z"] = value.Z,
        ["w"] = value.W
    };

    private static Dictionary<string, object?> CloudTint(StarfieldCloudTint tint) => new()
    {
        ["r"] = tint.R,
        ["g"] = tint.G,
        ["b"] = tint.B,
        ["a"] = tint.A
    };

    private static string? FormId(uint? formId) =>
        formId is uint value ? $"0x{value:X8}" : null;
}

