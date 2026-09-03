using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

/// <summary>
///     Compiler-free regression contracts for Skyrim's direct three-normal water route. The generic
///     NoiseIndex toggle is insufficient because this shader branch consumes uNormalIndices.xyz.
/// </summary>
public sealed class SkyrimWaterRippleToggleSourceContractTests
{
    [Fact]
    public void RipplesOffSubstitutesEveryOrderedSkyrimNormalBeforeUniformUpload()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "WaterRenderer12.cs");
        var bindingStart = renderer.IndexOf(
            "var normalIndex1 = _normalBindlessIndices[0];",
            StringComparison.Ordinal);
        var uploadStart = renderer.IndexOf(
            "*(WaterFrameUniforms*)perFrameAlloc.CpuPtr",
            bindingStart,
            StringComparison.Ordinal);

        Assert.True(bindingStart >= 0);
        Assert.True(uploadStart > bindingStart);
        var binding = renderer[bindingStart..uploadStart];
        Assert.Contains("_game == BethesdaGame.Skyrim", binding, StringComparison.Ordinal);
        Assert.Contains(
            "_waterProfile.ShaderVariant == WaterShaderVariant.StarfieldWaterApprox",
            binding,
            StringComparison.Ordinal);
        Assert.Equal(3, binding.Split("= ApplyRippleToggle(").Length - 1);
        Assert.Contains("normalIndex1 = ApplyRippleToggle(normalIndex1);", binding, StringComparison.Ordinal);
        Assert.Contains("normalIndex2 = ApplyRippleToggle(normalIndex2);", binding, StringComparison.Ordinal);
        Assert.Contains("normalIndex3 = ApplyRippleToggle(normalIndex3);", binding, StringComparison.Ordinal);
    }

    [Fact]
    public void SkyrimDirectLayerBranchConsumesAllThreeSubstitutedUniformIndices()
    {
        var shader = SourceContract.ReadShaderSource("water_fnv.frag.hlsl");
        var directStart = shader.IndexOf(
            "// Skyrim keeps three independently-authored normal inputs",
            StringComparison.Ordinal);
        var directEnd = shader.IndexOf("pert = macro + detail;", directStart, StringComparison.Ordinal);

        Assert.True(directStart >= 0);
        Assert.True(directEnd > directStart);
        var directBranch = shader[directStart..directEnd];
        Assert.Contains("uNormalIndices.x", directBranch, StringComparison.Ordinal);
        Assert.Contains("uNormalIndices.y", directBranch, StringComparison.Ordinal);
        Assert.Contains("uNormalIndices.z", directBranch, StringComparison.Ordinal);
        Assert.DoesNotContain("uNoiseParams.x", directBranch, StringComparison.Ordinal);
    }
}
