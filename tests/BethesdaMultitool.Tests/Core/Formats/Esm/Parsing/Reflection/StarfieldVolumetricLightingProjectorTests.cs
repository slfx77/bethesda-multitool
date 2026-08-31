using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Esm.Models.Reflection;
using BethesdaMultitool.Core.Formats.Esm.Parsing.Reflection;
using Xunit;
using static BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection.StarfieldReflectionTestStreamBuilder;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing.Reflection;

public sealed class StarfieldVolumetricLightingProjectorTests
{
    [Fact]
    public void Decode_FullRetailSchema_ProjectsAllThirtyTwoFiniteFloatLeaves()
    {
        var expected = Enumerable.Range(1, 32).Select(value => (float)value).ToArray();

        var ok = StarfieldVolumetricLightingDecoder.TryDecode(
            BuildValidVolumetricLightingStream(expected), out var settings, out var error);

        Assert.True(ok, error);
        Assert.NotNull(settings);
        Assert.Equal(expected, Flatten(settings));
    }

    [Fact]
    public void Decode_FullRetailSchema_RejectsMissingRequiredLeaf()
    {
        var stream = BuildValidVolumetricLightingStream(omitScatteringVolumeFar: true);

        Assert.False(StarfieldVolumetricLightingDecoder.TryDecode(
            stream, out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("ScatteringVolumeFar", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_FullRetailSchema_RejectsWrongKnownLeafType()
    {
        var stream = BuildValidVolumetricLightingStream(scatteringVolumeNearIsUInt32: true);

        Assert.False(StarfieldVolumetricLightingDecoder.TryDecode(
            stream, out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("ScatteringVolumeNear", error, StringComparison.Ordinal);
        Assert.Contains("UInt32", error, StringComparison.Ordinal);
        Assert.Contains("Float", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsDoubleMetadataThatGenericTreeWouldOtherwiseCoalesce()
    {
        var stream = BuildValidVolumetricLightingStream(scatteringVolumeFarIsDouble: true);

        Assert.False(StarfieldVolumetricLightingDecoder.TryDecode(
            stream, out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("ScatteringVolumeFar:Double", error, StringComparison.Ordinal);
        Assert.Contains("ScatteringVolumeFar:Float", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Decode_RejectsNonFiniteFloat()
    {
        var values = Enumerable.Range(1, 32).Select(value => (float)value).ToArray();
        values[28] = float.NaN;

        Assert.False(StarfieldVolumetricLightingDecoder.TryDecode(
            BuildValidVolumetricLightingStream(values), out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("non-finite", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RejectsDiffPayloadBecauseRetailVoliIsReflOnly()
    {
        Assert.False(StarfieldVolumetricLightingDecoder.TryDecode(
            BuildVolumetricLightingDiffStream(), out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("unexpected", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RejectsWrongReflectedRootType()
    {
        Assert.False(StarfieldVolumetricLightingDecoder.TryDecode(
            BuildValidVolumetricLightingStream(rootType: "NotVolumetricLighting"),
            out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("expected class", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RejectsUnprovenOutOfLineSideChunk()
    {
        var stream = BuildValidVolumetricLightingStream(appendListSideChunk: true);

        Assert.False(StarfieldVolumetricLightingDecoder.TryDecode(
            stream, out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("unsupported side chunk", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Project_RejectsAdditionalFieldOutsideExactRetailSchema()
    {
        var stream = BuildValidVolumetricLightingStream();
        Assert.True(BethesdaReflectionReader.TryReadObject(
            stream, false, "BGSVolumetricLighting", out var reflected, out var readError), readError);
        Assert.NotNull(reflected);
        var fields = reflected.Fields.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        fields.Add("Unexpected", new BethesdaReflectionFloatValue(1));
        var drifted = new BethesdaReflectionObject(reflected.TypeName, fields);

        Assert.False(StarfieldVolumetricLightingProjector.TryProject(
            drifted, out var settings, out var error));
        Assert.Null(settings);
        Assert.Contains("Unexpected", error, StringComparison.Ordinal);
    }

    private static float[] Flatten(StarfieldVolumetricLightingSettings settings)
    {
        var shared = settings.ExteriorAndInterior;
        var thickness = settings.Exterior.FogThickness;
        var density = settings.Exterior.FogDensity;
        var horizon = settings.Exterior.HorizonFog;
        var fogMap = settings.Exterior.FogMap;
        var albedo = fogMap.Albedo;
        var distant = settings.DistantLighting;
        return
        [
            shared.ScatteringVolumeNear,
            shared.ScatteringVolumeFar,
            shared.HighFrequencyNoiseScale,
            shared.HighFrequencyNoiseDensityScale,
            thickness.ThicknessNoiseScale,
            thickness.ThicknessNoiseBias,
            thickness.MinFogThickness,
            thickness.MaxFogThickness,
            density.DensityNoiseScale,
            density.DensityNoiseBias,
            density.MinFogDensity,
            density.MaxFogDensity,
            density.DensityStartDistance,
            density.DensityFullDistance,
            density.DensityDistanceExponent,
            horizon.FogThickness,
            horizon.FogDensity,
            horizon.DensityStartDistance,
            horizon.DensityFullDistance,
            fogMap.HeightAboveTerrain,
            fogMap.TerrainMatch,
            albedo.X,
            albedo.Y,
            albedo.Z,
            albedo.W,
            fogMap.Anisotropy,
            fogMap.MinMeanFreePath,
            fogMap.MaxMeanFreePath,
            fogMap.HeightFalloffExponent,
            fogMap.Span,
            distant.ScatteringTransition,
            distant.ScatteringFar
        ];
    }
}
