using BethesdaMultitool.Core.Formats.Nif.Rendering.D3D12;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.D3D12;

/// <summary>
///     Pins the FO3/FNV sky pass order recovered from SkyShaderProperty::GetRenderPasses and the
///     corresponding base/glare blend state. The gate is deliberately limited to those two games;
///     Skyrim and Oblivion have separate parity work and must retain their established route.
/// </summary>
public sealed class FnvSunRenderPassOrderTests
{
    [Fact]
    public void StagedSunOrder_IsEnabledOnlyForFallout3AndNewVegas()
    {
        var stagedGames = Enum.GetValues<BethesdaGame>()
            .Where(SkyRenderPassPolicy12.UsesFallout3NewVegasSunOrder)
            .ToHashSet();

        Assert.Equal(2, stagedGames.Count);
        Assert.Contains(BethesdaGame.Fallout3, stagedGames);
        Assert.Contains(BethesdaGame.FalloutNewVegas, stagedGames);
        Assert.False(SkyRenderPassPolicy12.UsesFallout3NewVegasSunOrder(BethesdaGame.Oblivion));
        Assert.False(SkyRenderPassPolicy12.UsesFallout3NewVegasSunOrder(BethesdaGame.Skyrim));
    }

    [Fact]
    public void Host_SubmitsRecoveredFalloutStagesInRetailOrder_WithoutDoubleAdvancingClouds()
    {
        var source = SourceContract.ReadAppSource("WorldView3DControl.Atmosphere.cs");
        var renderSky = SourceContract.Extract(
            source,
            "private void RenderSky(",
            "private void RenderSkyBillboards(");

        Assert.Contains(
            "if (exterior && SkyRenderPassPolicy12.UsesFallout3NewVegasSunOrder(game))",
            renderSky,
            StringComparison.Ordinal);
        SourceContract.AssertOrder(
            renderSky,
            "RenderGeometry(SkyGeometryPass12.AtmosphereAndStars, advanceCloudScroll);",
            "billboardBasis, SkyBillboardPass12.SunBase);",
            "RenderGeometry(SkyGeometryPass12.Clouds, advanceScroll: false);",
            "billboardBasis, SkyBillboardPass12.SunGlareAndMoons);",
            "RenderGeometry(SkyGeometryPass12.All, advanceCloudScroll);");
    }

    [Fact]
    public void GeometryStages_SeparateCloudsFromAtmosphereAndStars()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "SkyGeometryRenderer12.cs");

        Assert.Contains(
            "pass != SkyGeometryPass12.Clouds && !_layers.Any(static layer => layer.Mode == 0)",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "pass == SkyGeometryPass12.AtmosphereAndStars && layer.Mode == 2",
            source,
            StringComparison.Ordinal);
        Assert.Contains(
            "pass == SkyGeometryPass12.Clouds && layer.Mode != 2",
            source,
            StringComparison.Ordinal);
    }

    [Fact]
    public void FalloutSunBase_IsAlphaBlended_AndGlareIsAdditive()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "D3D12",
            "SkyBillboardRenderer12.cs");
        var render = SourceContract.Extract(source, "public void Render(", "private void Draw(");
        var sunBase = SourceContract.Extract(
            render,
            "if (pass == SkyBillboardPass12.SunBase",
            "// The separate glare pass");
        var sunGlare = SourceContract.Extract(
            render,
            "if (pass == SkyBillboardPass12.SunGlareAndMoons",
            "// Moon(s) (night)");

        Assert.Contains("Draw(_psoAlpha", sunBase, StringComparison.Ordinal);
        Assert.DoesNotContain("Draw(_psoAdditive", sunBase, StringComparison.Ordinal);
        Assert.Contains("Draw(_psoAdditive", sunGlare, StringComparison.Ordinal);
        Assert.DoesNotContain("Draw(_psoAlpha", sunGlare, StringComparison.Ordinal);

        Assert.Contains("SourceBlend = D12.Blend.SourceAlpha", source, StringComparison.Ordinal);
        Assert.Contains(
            "DestinationBlend = additive ? D12.Blend.One : D12.Blend.InverseSourceAlpha",
            source,
            StringComparison.Ordinal);
    }
}