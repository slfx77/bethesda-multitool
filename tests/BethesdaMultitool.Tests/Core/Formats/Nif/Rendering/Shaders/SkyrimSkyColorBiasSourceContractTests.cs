using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Shaders;

/// <summary>
///     Locks TESV 1.9.32's recovered HNAM[7] scene consumer. BSSkyShader::SetupGeometry sends the
///     value to PParams.y, and the shipped atmosphere/cloud/star programs add it after their authored
///     color modulation. SunGlare explicitly receives zero and remains in the separate billboard path.
/// </summary>
public sealed class SkyrimSkyColorBiasSourceContractTests
{
    [Fact]
    public void SkyrimHnam7_IsAnUnclampedPostModulationBias_NotAColorMultiplier()
    {
        var settings = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Gpu", "D3D12",
            "GpuTonemapSettings.cs");
        var shader = SourceContract.ReadShaderSource("sky_geo.frag.hlsl");

        Assert.Contains(
            "return new SceneSkyColorTransform(1f, settings.SkyScale);",
            settings, StringComparison.Ordinal);
        Assert.Contains(
            "SkyScale = family == ImageSpaceModernFamily.Skyrim ? 0f : 1f,",
            settings, StringComparison.Ordinal);

        SourceContract.AssertOrder(
            shader,
            "float3 weighted =",
            "float3 atmosphere = lerp(uSkyHorizon.rgb, weighted, input.vColor.a)",
            "+ uSkyColorBias.xxx;",
            "return float4(atmosphere, 1.0);");
        Assert.Contains(
            "return float4((tex.rgb * uTintParam.rgb * vertexWeight) + uSkyColorBias.xxx,",
            shader, StringComparison.Ordinal);
        Assert.DoesNotContain("* uSkyColorBias", shader, StringComparison.Ordinal);
        Assert.DoesNotContain("saturate(uSkyColorBias", shader, StringComparison.Ordinal);
    }

    [Fact]
    public void SkyrimStars_ApplyRecoveredOnePointFiveAfterTheBias_WithoutChangingOtherGames()
    {
        var renderer = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "SkyGeometryRenderer12.cs");
        var shader = SourceContract.ReadShaderSource("sky_geo.frag.hlsl");

        Assert.Contains(
            "var starColorScale = game == BethesdaGame.Skyrim ? 1.5f : 1f;",
            renderer, StringComparison.Ordinal);
        SourceContract.AssertOrder(
            shader,
            "float3 stars = ((tex.rgb * uTintParam.rgb * vertexWeight) + uSkyColorBias.xxx)",
            "* uStarColorScale;",
            "return float4(stars,");
    }

    [Fact]
    public void SunGlareBillboard_DoesNotReceiveTheSkyBias()
    {
        var billboard = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "SkyBillboardRenderer12.cs");

        Assert.DoesNotContain("SkyColorBias", billboard, StringComparison.Ordinal);
        Assert.DoesNotContain("skyColorBias", billboard, StringComparison.Ordinal);
    }
}