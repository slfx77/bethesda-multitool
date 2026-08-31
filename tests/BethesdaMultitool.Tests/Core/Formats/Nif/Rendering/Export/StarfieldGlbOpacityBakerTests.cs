using BethesdaMultitool.Core.Formats.Dds;
using BethesdaMultitool.Core.Formats.Nif.Materials;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Export;
using BethesdaMultitool.Tests.Helpers;
using System.Numerics;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Export;

public sealed class StarfieldGlbOpacityBakerTests
{
    [Fact]
    public void Bake_CopiesOpacityRedAndPreservesBaseRgb()
    {
        var baseColor = Texture(2, 1,
            [10, 20, 30, 40, 50, 60, 70, 80]);
        var opacity = Texture(2, 1,
            [7, 99, 98, 97, 211, 96, 95, 94]);

        var result = StarfieldGlbOpacityBaker.Bake(baseColor, opacity);

        Assert.True(result.Applied);
        Assert.Equal(
            new byte[] { 10, 20, 30, 7, 50, 60, 70, 211 },
            Assert.IsType<DecodedTexture>(result.Texture).Pixels);
    }

    [Fact]
    public void Bake_MissingBaseSynthesizesWhiteRgbAtOpacityDimensions()
    {
        var opacity = Texture(2, 1,
            [0, 10, 20, 30, 255, 40, 50, 60]);

        var result = StarfieldGlbOpacityBaker.Bake(null, opacity);

        Assert.True(result.Applied);
        Assert.Equal(
            new byte[] { 255, 255, 255, 0, 255, 255, 255, 255 },
            Assert.IsType<DecodedTexture>(result.Texture).Pixels);
    }

    [Fact]
    public void Bake_MismatchedNonConstantBaseFailsClosed()
    {
        var baseColor = Texture(2, 1,
            [10, 20, 30, 40, 50, 60, 70, 80]);
        var opacity = Texture(1, 2,
            [7, 0, 0, 0, 211, 0, 0, 0]);

        var result = StarfieldGlbOpacityBaker.Bake(baseColor, opacity);

        Assert.False(result.Applied);
        Assert.Same(baseColor, result.Texture);
    }

    [Fact]
    public void MissingBase_CombinesOpacityTextureWithTheConstantLerpFactor()
    {
        var state = new StarfieldMaterialColorRenderState(
            StarfieldMaterialColorRenderMode.ConstantLerp,
            new Vector4(0.2f, 0.4f, 0.6f, 0.25f));
        var opacity = Texture(1, 1, [17, 0, 0, 0]);

        var lerpBakedTexture = StarfieldGlbColorLerpBaker.BakeDiffuseTexture(null, state);
        var opacityBake = StarfieldGlbOpacityBaker.Bake(lerpBakedTexture, opacity);
        var factor = StarfieldGlbColorLerpBaker.BuildBaseColor(
            Vector4.One,
            hasDiffuseTexture: lerpBakedTexture is not null,
            state);

        Assert.True(opacityBake.Applied);
        Assert.Equal(new byte[] { 255, 255, 255, 17 }, opacityBake.Texture!.Pixels);
        Assert.Equal(new Vector4(0.8f, 0.85f, 0.9f, 1f), factor);
    }

    [Fact]
    public void GltfMaskCutoff_PreservesAuthoredFloatGreaterPredicate()
    {
        const float authoredThreshold = 1f / 3f;

        var cutoff = GlbWriter.ToGltfGreaterCutoff(authoredThreshold);

        Assert.Equal(MathF.BitIncrement(authoredThreshold), cutoff);
        Assert.True(cutoff > authoredThreshold);
        Assert.Equal(authoredThreshold, MathF.BitDecrement(cutoff));
    }

    [Fact]
    public void GlbWriter_UsesFloatDomainCutoffForAppliedStarfieldOpacityBake()
    {
        var source = SourceContract.ReadSource(
            "src", "BethesdaMultitool", "Core", "Formats", "Nif", "Rendering", "Export",
            "GlbWriter.cs");

        SourceContract.AssertOrder(
            source,
            "var alphaCutoff = opacityBake.Applied && starfieldAlpha.IsLayer0OpacityCutout",
            "? ToGltfGreaterCutoff(starfieldAlpha.AlphaTestThreshold)",
            ": preparedAlpha.AlphaThreshold / 255f;",
            "material.WithAlpha(AlphaMode.MASK, alphaCutoff);");
    }

    private static DecodedTexture Texture(int width, int height, byte[] pixels)
    {
        return DecodedTexture.FromBaseLevel(pixels, width, height, false);
    }
}
