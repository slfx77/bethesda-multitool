using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

/// <summary>Focused math pins for the retail Oblivion SKIN2000 light equation.</summary>
public sealed class ClassicSkin2000LightingTests
{
    [Fact]
    public void BackFacingLightHasNoWrappedDiffuseContribution()
    {
        var shade = ClassicSkin2000Lighting.Compute(
            normalDotLight: -0.1f,
            normalDotView: 1f,
            lightIntensity: 0.8f,
            ambient: 0.2f);

        Assert.Equal(0.2f, shade, 6);
    }

    [Fact]
    public void ViewRimUsesTheRetailCubicFalloffAndHalfLightScale()
    {
        var shade = ClassicSkin2000Lighting.Compute(
            normalDotLight: 0f,
            normalDotView: 0.5f,
            lightIntensity: 0.8f,
            ambient: 0.2f);

        // 0.2 + 0.5 * 0.8 * (1 - 0.5)^3 = 0.25.
        Assert.Equal(0.25f, shade, 6);
    }

    [Fact]
    public void ViewDotIsClampedBeforeTheCubicRim()
    {
        var shade = ClassicSkin2000Lighting.Compute(
            normalDotLight: 0f,
            normalDotView: -0.25f,
            lightIntensity: 0.8f,
            ambient: 0.2f);

        Assert.Equal(0.6f, shade, 6);
    }

    [Fact]
    public void LightingAggregateIsNotClampedAtOne()
    {
        var shade = ClassicSkin2000Lighting.Compute(
            normalDotLight: 1f,
            normalDotView: 1f,
            lightIntensity: 0.8f,
            ambient: 0.4f);

        Assert.Equal(1.2f, shade, 6);
    }
}
