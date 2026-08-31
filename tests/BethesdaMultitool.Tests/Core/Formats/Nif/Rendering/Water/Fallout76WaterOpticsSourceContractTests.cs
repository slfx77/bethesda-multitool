using BethesdaMultitool.Core.Formats.Nif.Rendering.Gpu.D3D12;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Compiler-free contracts for the Windows-only FO76 water path. Shader compilation separately
///     walks <see cref="ShaderPermutations.All" /> when its opt-in gate is enabled.
/// </summary>
public sealed class Fallout76WaterOpticsSourceContractTests
{
    [Fact]
    public void StrictTypedPayloadRoutesExactFloatOpticsAndFailsClosedOtherwise()
    {
        var source = ReadRenderer();

        Assert.Contains("var useFallout76Optics = !useModernPipeline && fallout76Visual.HasValue;",
            source, StringComparison.Ordinal);
        Assert.Contains("NormalizedRgbToVector4(fallout76Visual!.Value.BaseColor",
            source, StringComparison.Ordinal);
        Assert.Contains("fallout76Visual!.Value.ChannelOpacity",
            source, StringComparison.Ordinal);
        Assert.Contains("fallout76Visual.Value.NormalMagnitude",
            source, StringComparison.Ordinal);
        Assert.Contains("depthSample ? _psoFo76OpticsDepthSample : _psoFo76Optics",
            source, StringComparison.Ordinal);
        Assert.Contains(
            "retail FO76 BSWaterShader unrecovered; fo76utils transmission-only reference approximation",
            source, StringComparison.Ordinal);

        // The exact float lanes must not replace FO4's byte-colour constants globally. They are
        // conditional expressions whose fallback arms retain DarkSilt and LightSilt verbatim.
        Assert.Contains(": ColorToVector4(_appearance?.DarkSilt, Vector3.Zero) with",
            source, StringComparison.Ordinal);
        Assert.Contains(": ColorToVector4(_appearance?.LightSilt, Vector3.Zero) with",
            source, StringComparison.Ordinal);
    }

    [Fact]
    public void DedicatedPsoUsesSingleTargetDualSourceTransmissionAndPreservesAlpha()
    {
        var source = ReadRenderer();
        var standardBlendStart = source.IndexOf("var blend = new D12.BlendDescription",
            StringComparison.Ordinal);
        var blendStart = source.IndexOf(
            "var fallout76OpticsBlend = new D12.BlendDescription",
            StringComparison.Ordinal);
        var compileStart = source.IndexOf("var psOblivionBytecode", blendStart, StringComparison.Ordinal);

        Assert.True(standardBlendStart >= 0);
        Assert.True(blendStart > standardBlendStart);
        Assert.True(compileStart > blendStart);
        var standardBlend = source[standardBlendStart..blendStart];
        Assert.Contains("SourceBlend = D12.Blend.SourceAlpha", standardBlend, StringComparison.Ordinal);
        Assert.Contains("DestinationBlend = D12.Blend.InverseSourceAlpha", standardBlend,
            StringComparison.Ordinal);
        var blend = source[blendStart..compileStart];
        Assert.Contains("IndependentBlendEnable = false", blend, StringComparison.Ordinal);
        Assert.Contains("SourceBlend = D12.Blend.One", blend, StringComparison.Ordinal);
        Assert.Contains("DestinationBlend = D12.Blend.Source1Color", blend, StringComparison.Ordinal);
        Assert.Contains("SourceBlendAlpha = D12.Blend.One", blend, StringComparison.Ordinal);
        Assert.Contains("DestinationBlendAlpha = D12.Blend.One", blend, StringComparison.Ordinal);
        Assert.Contains("BlendOperationAlpha = D12.BlendOperation.Max", blend, StringComparison.Ordinal);

        var psoStart = source.IndexOf("var psoDesc = new GraphicsPipelineStateDescription",
            compileStart, StringComparison.Ordinal);
        var psoEnd = source.IndexOf("_depthPsoTemplate = psoDesc;", psoStart,
            StringComparison.Ordinal);
        Assert.True(psoStart > compileStart);
        Assert.True(psoEnd > psoStart);
        var pso = source[psoStart..psoEnd];
        Assert.Contains(
            "RenderTargetFormats = new[] { Gpu.D3D12.GpuSceneFormats.SceneColor }",
            pso, StringComparison.Ordinal);

        Assert.Contains("new ShaderMacro(\"FO76_WATER_OPTICS\", \"1\")", source,
            StringComparison.Ordinal);
        Assert.Contains("_psoFo76Optics = gpu.Device.CreateGraphicsPipelineState", source,
            StringComparison.Ordinal);
        Assert.Contains("_psoFo76OpticsDepthSample = gpu.Device.CreateGraphicsPipelineState", source,
            StringComparison.Ordinal);
        Assert.Contains("_psoFo76Optics.Dispose();", source, StringComparison.Ordinal);
        Assert.Contains("_psoFo76OpticsDepthSample.Dispose();", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderSamplesThreeAuthoredNormalsOnceAndUsesOnlyGroundedRelativeInputs()
    {
        var source = SourceContract.ReadShaderSource("water_fo4.frag.hlsl");
        var branchStart = source.IndexOf("#elif FO76_WATER_OPTICS", StringComparison.Ordinal);
        var branchEnd = source.IndexOf("#else", branchStart, StringComparison.Ordinal);

        Assert.True(branchStart >= 0);
        Assert.True(branchEnd > branchStart);
        var branch = source[branchStart..branchEnd];
        Assert.Equal(3, branch.Split("SampleFo76AuthoredNormal(").Length - 1);
        Assert.Contains("uNormalIndices.x", branch, StringComparison.Ordinal);
        Assert.Contains("uNormalIndices.y", branch, StringComparison.Ordinal);
        Assert.Contains("uNormalIndices.z", branch, StringComparison.Ordinal);
        Assert.Contains("Fo76RelativeLayerScale(uLayer1)", branch, StringComparison.Ordinal);
        Assert.Contains("Fo76LayerScroll(uLayer2, t)", branch, StringComparison.Ordinal);
        Assert.Contains("fo76Normal3 * uLayer3.w", branch, StringComparison.Ordinal);
        Assert.Contains("uModernLightSilt.w", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("SampleNoiseLayer", branch, StringComparison.Ordinal);
        Assert.DoesNotContain("fDetail", branch, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderEmitsPerChannelFo76utilsReferenceApproximationNotAParityClaim()
    {
        var source = SourceContract.ReadShaderSource("water_fo4.frag.hlsl");

        Assert.Contains("float4 SourceContribution : SV_Target0;", source, StringComparison.Ordinal);
        Assert.Contains("float4 DestinationTransmission : SV_Target1;", source, StringComparison.Ordinal);
        Assert.Contains("fo76utils transmission-only reference approximation (NOT Bethesda shader parity)", source,
            StringComparison.Ordinal);
        Assert.Contains("this is NOT full fo76utils lighting parity either", source,
            StringComparison.Ordinal);
        Assert.Contains("exp2(-3.0 * fo76DepthFraction - 0.75)", source, StringComparison.Ordinal);
        Assert.Contains("fo76Transparency * (1.0 - uModernLightSilt.rgb)", source,
            StringComparison.Ordinal);
        Assert.Contains("body * lighting * (1.0 - destinationTransmission)", source,
            StringComparison.Ordinal);
        Assert.Contains("saturate(destinationTransmission)", source, StringComparison.Ordinal);
    }

    [Fact]
    public void ShaderInventoryCoversPlainAndHardwareOcclusionOpticsPermutations()
    {
        var permutations = ShaderPermutations.Water
            .Where(permutation => permutation.File == "water_fo4.frag.hlsl")
            .Where(permutation => permutation.Macros.Any(
                macro => macro.Name == "FO76_WATER_OPTICS" && macro.Definition == "1"))
            .ToArray();

        Assert.Equal(2, permutations.Length);
        Assert.Single(permutations, permutation => permutation.Macros.All(
            macro => macro.Name != "WATER_HARDWARE_OCCLUSION"));
        Assert.Single(permutations, permutation => permutation.Macros.Any(
            macro => macro.Name == "WATER_HARDWARE_OCCLUSION" && macro.Definition == "1"));
    }

    private static string ReadRenderer() => SourceContract.ReadSource(
        "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
        "WaterRenderer12.cs");
}
