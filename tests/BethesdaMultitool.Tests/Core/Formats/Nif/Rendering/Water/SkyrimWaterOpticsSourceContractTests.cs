using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class SkyrimWaterOpticsSourceContractTests
{
    private static readonly string[] ExpectedSkyrimMacros =
        ["SKYRIM_OPAQUE_REFRACTION=1", "WATER_HARDWARE_OCCLUSION=1"];

    [Fact]
    public void ExistingUnionRegistersCarryTypedDataOnlyOnTheSkyrimSnapshotPath()
    {
        var renderer = SourceContract.ReadSource("src", "BethesdaMultitool", "Core", "Formats", "Nif",
            "Rendering", "D3D12", "WaterRenderer12.cs");
        Assert.Contains("_game, useSkyrimOpaqueSceneSnapshot, _appearance?.SkyrimOptics", renderer,
            StringComparison.Ordinal);
        Assert.Contains("Fo4Spec = useSkyrimOpaqueSceneSnapshot\n                    ? skyrimOptics.DepthControl",
            renderer, StringComparison.Ordinal);
        Assert.Contains("Fo4Ranges = useSkyrimOpaqueSceneSnapshot\n                    ? skyrimOptics.Fog",
            renderer, StringComparison.Ordinal);
        Assert.Contains(": new Vector4(surface.SunSpecularMagnitude, surface.SiltAmount,", renderer,
            StringComparison.Ordinal);
        Assert.Contains(": new Vector4(surface.ColorShallowRange, surface.ColorDeepRange,", renderer,
            StringComparison.Ordinal);
        // ABI remains the pre-existing shared layout; Skyrim must not add a tail counted by only one side.
        Assert.DoesNotContain("SkyrimWaterUniformByteSize", renderer, StringComparison.Ordinal);
        var layout = SourceContract.Extract(renderer, "private struct WaterFrameUniforms", "\n    }\n}");
        SourceContract.AssertOrder(layout, "public Vector4 Fo4Spec;", "public Vector4 Fo4Ranges;",
            "public Vector4 Fo4DarkSilt;", "public uint NormalIndex1;");
        var common = SourceContract.ReadShaderSource("water_common.hlsli");
        SourceContract.AssertOrder(common, "float4 uFo4Spec;", "float4 uFo4Ranges;",
            "float4 uFo4DarkSilt;", "uint4 uNormalIndices;");
    }

    [Fact]
    public void DepthAndFogConsumerRetainsTheRetailNormalizationOrderAndIndependentLanes()
    {
        var helper = SourceContract.ReadShaderSource("water_skyrim_optics.hlsli");
        Assert.Contains("saturate(columns.xxyy / uFo4Ranges.x)", helper, StringComparison.Ordinal);
        Assert.Contains("1.0 + uFo4Spec * (depthFractions - 1.0)", helper, StringComparison.Ordinal);
        Assert.Contains("normalize(up + normalFactor * (normalizedBaseNormal - up))", helper,
            StringComparison.Ordinal);
        Assert.Contains("uFo4Ranges.x * (1.0 - slantDepth) / uFo4Ranges.y", helper, StringComparison.Ordinal);
        Assert.Contains("1.0 - pow(clearFraction, uFo4Ranges.z)", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("uSurface1", helper, StringComparison.Ordinal);
        Assert.DoesNotContain("uFnvWater001Surface", helper, StringComparison.Ordinal);

        var shader = SourceContract.ReadShaderSource("water_fnv.frag.hlsl");
        Assert.Contains("#if SKYRIM_OPAQUE_REFRACTION\n#include \"water_skyrim_optics.hlsli\"\n#endif", shader,
            StringComparison.Ordinal);
        Assert.Contains("uCamPosTime.z >= input.vWorldPos.z", shader, StringComparison.Ordinal);
        SourceContract.AssertOrder(shader, "float3 skyrimBaseNormal = normalize(",
            "N = SkyrimApplyNormalDepth(skyrimBaseNormal, skyrimDepthFactors.z);");
        Assert.Contains("SkyrimSlantDepthFraction(skyrimColumns.x)", shader, StringComparison.Ordinal);
        Assert.Contains("spec *= skyrimDepthFactors.w;", shader, StringComparison.Ordinal);
        Assert.Contains("W = SkyrimBodyFogWeight(skyrimColumns.x);", shader, StringComparison.Ordinal);
        // No invented projected distortion or replacement Fresnel-distance constant in this slice.
        Assert.DoesNotContain("refractionUv +=", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("fresneled = saturate(F * skyrimDepthFactors.x)", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void ShippedInventoryKeepsTheSameSkyrimTechniqueAndResolvesItsNewInclude()
    {
        var permutation = Assert.Single(ShaderPermutations.Water, candidate =>
            candidate.Purpose == "Skyrim BSWaterShader opaque-scene snapshot refraction");
        Assert.Equal("water_fnv.frag.hlsl", permutation.File);
        Assert.Equal(ExpectedSkyrimMacros,
            permutation.Macros.Select(macro => $"{macro.Name}={macro.Definition}"));
        Assert.NotEmpty(SourceContract.ReadShaderSource("water_skyrim_optics.hlsli"));
    }
}
