using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Source contracts for Skyrim's recovered BSWaterShader output ABI. The shader must resolve
///     transmission from a separate scene-color texture and then overwrite the target with opaque
///     alpha; routing the old fractional-alpha result through destination blending is not equivalent.
///     Skyrim's retail <c>TESV.map</c> independently names <c>BSWaterShader::SetupTechnique</c>
///     (0x00e6ba00), <c>SetupGeometry</c> (0x00e6bdc0), and its <c>RefractionSampler</c>,
///     <c>DepthSampler</c>, <c>ReflectionSampler</c>, <c>ShallowColor</c>, <c>DeepColor</c>,
///     <c>FresnelRI</c>, and <c>DepthControl</c> ABI alongside the recovered pixel program.
/// </summary>
public sealed class SkyrimWaterOpaqueSnapshotSourceContractTests
{
    [Fact]
    public void ProfileAndPrecompileInventorySelectDedicatedSkyrimPermutation()
    {
        var profile = WaterProfile.ForGame(BethesdaGame.Skyrim);
        Assert.Same(WaterProfile.Skyrim, profile);
        Assert.Equal(WaterShaderVariant.SkyrimWater, profile.ShaderVariant);

        var permutation = Assert.Single(ShaderPermutations.Water, candidate =>
            candidate.Purpose == "Skyrim BSWaterShader opaque-scene snapshot refraction");
        Assert.Equal("water_fnv.frag.hlsl", permutation.File);
        Assert.Equal("main", permutation.EntryPoint);
        Assert.Equal("ps_5_1", permutation.Profile);
        Assert.Equal(
            ["SKYRIM_OPAQUE_REFRACTION=1", "WATER_HARDWARE_OCCLUSION=1"],
            permutation.Macros.Select(macro => $"{macro.Name}={macro.Definition}").ToArray());
    }

    [Fact]
    public void PixelShaderSamplesSnapshotRgbAndWritesOpaqueAlphaInShader()
    {
        var shader = SourceContract.ReadShaderSource("water_fnv.frag.hlsl");
        var skyrim = SourceContract.Extract(
            shader,
            "#if defined(SKYRIM_OPAQUE_REFRACTION)",
            "#else");

        SourceContract.AssertOrder(
            skyrim,
            "uint snapshotIndex = uFnvWater001Snapshot.x;",
            "float2 refractionUv = input.Position.xy / snapshotDimensions;",
            "gWaterTextures[NonUniformResourceIndex(snapshotIndex)]",
            ".SampleLevel(gWaterClampSampler, saturate(refractionUv), 0).rgb;",
            "float3 foggedSurface = ApplyFog(",
            "return float4(lerp(refraction, foggedSurface, alpha), 1.0);");
        Assert.DoesNotContain("uFnvWater001Surface.w", skyrim, StringComparison.Ordinal);
        Assert.DoesNotContain("return float4(foggedSurface, alpha)", skyrim, StringComparison.Ordinal);
    }

    [Fact]
    public void RendererUsesBlendDisabledDepthPsoOnlyWithAValidOneShotSnapshot()
    {
        var renderer = ReadRenderer();
        var constructor = SourceContract.Extract(
            renderer,
            "var skyrimOpaqueSnapshotBlend = new D12.BlendDescription",
            "var fallout76OpticsBlend = new D12.BlendDescription");
        Assert.Contains("BlendEnable = false", constructor, StringComparison.Ordinal);
        Assert.Contains("SourceBlend = D12.Blend.One", constructor, StringComparison.Ordinal);
        Assert.Contains("DestinationBlend = D12.Blend.Zero", constructor, StringComparison.Ordinal);

        SourceContract.AssertOrder(
            renderer,
            "var psSkyrimOpaqueSnapshotBytecode = CompileEmbeddedShader(",
            "new ShaderMacro(\"SKYRIM_OPAQUE_REFRACTION\", \"1\")",
            "new ShaderMacro(\"WATER_HARDWARE_OCCLUSION\", \"1\")",
            "skyrimOpaqueSnapshotPsoDesc.BlendState = skyrimOpaqueSnapshotBlend;",
            "_psoSkyrimOpaqueSnapshotDepthSample = TrackConstructionResource(");
        Assert.Contains("_psoSkyrimOpaqueSnapshotDepthSample.Dispose();", renderer,
            StringComparison.Ordinal);

        var render = SourceContract.Extract(
            renderer,
            "private int RenderCore(",
            "private static string DescribeTechnique(");
        Assert.Contains(
            "skyrimOpaqueSceneSnapshotRequested &&\n" +
            "            _waterProfile.ShaderVariant == WaterShaderVariant.SkyrimWater &&\n" +
            "            _depthBindlessIndex != NoNormalMap && opaqueSceneSnapshot.IsValid",
            render,
            StringComparison.Ordinal);
        Assert.Contains(
            "FnvWater001SnapshotIndex = useFnvWater001 || useSkyrimOpaqueSceneSnapshot",
            render,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            render,
            "else if (useSkyrimOpaqueSceneSnapshot)",
            "SkyrimBsWaterShader-opaque-scene-snapshot-refraction-main-scene-approx",
            "else if (useSkyrimOpaqueSceneSnapshot)",
            "cmd.SetPipelineState(_psoSkyrimOpaqueSnapshotDepthSample);");
    }

    [Fact]
    public void SharedSnapshotRequestKeepsSkyrimOutOfFnvMaterialEligibility()
    {
        var renderer = ReadRenderer();
        var request = SourceContract.Extract(
            renderer,
            "public bool TryRequestWaterOpaqueSceneSnapshot(",
            "public bool HasVisibleWaterToPartition(");

        SourceContract.AssertOrder(
            request,
            "var fnvWater001Preflight = GetFnvWater001Preflight(",
            "if (fnvWater001Preflight.Candidate)",
            "_waterProfile.ShaderVariant != WaterShaderVariant.SkyrimWater",
            "!isPerspectiveProjection || _depthBindlessIndex == NoNormalMap",
            "_visibleWaterScratch.Count + GatherVisibleNifPlanes(cylinder) > 0");
        Assert.DoesNotContain("InspectFnvWater001VisibleCells", request, StringComparison.Ordinal);

        var frame = SourceContract.ReadAppSource("WorldView3DControl.Frame.cs");
        Assert.Contains("_water.TryRequestWaterOpaqueSceneSnapshot(", frame,
            StringComparison.Ordinal);
        Assert.Contains("_water.SetWaterOpaqueSceneSnapshot(", frame, StringComparison.Ordinal);

        var capture = SourceContract.ReadAppSource("WorldView3DControl.SceneCapture.cs");
        Assert.Contains("_water!.TryRequestWaterOpaqueSceneSnapshot(", capture,
            StringComparison.Ordinal);
        Assert.Contains("_water.SetWaterOpaqueSceneSnapshot(", capture, StringComparison.Ordinal);
    }

    private static string ReadRenderer()
    {
        return SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
    }
}