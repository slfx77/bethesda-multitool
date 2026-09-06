using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Water;
using BethesdaMultitool.Core.Games;
using Xunit;
using ProbeMode = BethesdaMultitool.Core.Formats.Nif.Rendering.Water.OblivionWaterDisplacementComposition.ProbeMode;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Water;

public sealed class OblivionWaterDisplacementCompositionTests
{
    [Theory]
    [InlineData(null, 0)]
    [InlineData("", 0)]
    [InlineData("1", 0)]
    [InlineData("retail", 0)]
    [InlineData("neutral-zero", 1)]
    [InlineData("NEUTRAL-ZERO", 1)]
    [InlineData("radial-impulse", 2)]
    public void OnlyExplicitDiagnosticNamesEnableAProbe(string? raw, int expected)
    {
        Assert.Equal((ProbeMode)expected, OblivionWaterDisplacementComposition.ParseProbeMode(raw));
    }

    [Fact]
    public void ProbeIdentityAndAmountDistinguishDisabledNeutralAndImpulse()
    {
        Assert.Equal("disabled", OblivionWaterDisplacementComposition.GetProbeKey(ProbeMode.Disabled));
        Assert.Equal("neutral-zero", OblivionWaterDisplacementComposition.GetProbeKey(ProbeMode.NeutralZeroBlend));
        Assert.Equal("radial-impulse", OblivionWaterDisplacementComposition.GetProbeKey(ProbeMode.RadialImpulse));
        Assert.Equal(0f, OblivionWaterDisplacementComposition.GetProbeBlendAmount(ProbeMode.Disabled));
        Assert.Equal(0f, OblivionWaterDisplacementComposition.GetProbeBlendAmount(ProbeMode.NeutralZeroBlend));
        Assert.Equal(1f, OblivionWaterDisplacementComposition.GetProbeBlendAmount(ProbeMode.RadialImpulse));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            OblivionWaterDisplacementComposition.GenerateProbeTexture(ProbeMode.Disabled));
    }

    [Fact]
    public void WadingQuadUsesCameraRelative1024WorldUnitsAndNotGlobalNormalUv()
    {
        var camera = new Vector2(6161f, 45749f);
        Assert.Equal(new Vector2(0.5f),
            OblivionWaterDisplacementComposition.GetWadingUv(camera, camera));
        Assert.Equal(Vector2.Zero,
            OblivionWaterDisplacementComposition.GetWadingUv(camera - new Vector2(512f), camera));
        Assert.Equal(Vector2.One,
            OblivionWaterDisplacementComposition.GetWadingUv(camera + new Vector2(512f), camera));
        Assert.Equal(new Vector2(0.75f, 0.25f),
            OblivionWaterDisplacementComposition.GetWadingUv(camera + new Vector2(256f, -256f), camera));
    }

    [Theory]
    [InlineData(0.5f, 0.5f, 1f, 1f, 0.9f)]
    [InlineData(0.525f, 0.5f, 1f, 1f, 0.9f)]
    [InlineData(0.75f, 0.5f, 1f, 1f, 0.5f)]
    [InlineData(0.75f, 0.5f, 1f, 0.4f, 0.2f)]
    [InlineData(1f, 0.5f, 1f, 1f, 0f)]
    [InlineData(1.25f, 0.5f, 1f, 1f, 0f)]
    [InlineData(0.75f, 0.5f, 0.5f, 1f, 0f)]
    [InlineData(0.5f, 0.5f, 0f, 1f, 0f)]
    [InlineData(0.5f, 0.5f, 1f, 0f, 0f)]
    public void BlendWeightMatchesRetailFloorRadiusAndAmount(
        float u, float v, float radius, float amount, float expected)
    {
        Assert.Equal(expected,
            OblivionWaterDisplacementComposition.GetBlendWeight(new Vector2(u, v), radius, amount), 6);
    }

    [Fact]
    public void CompositionDecodesSignedNormalAndNormalizesOnlyAfterLerp()
    {
        Assert.Equal(new Vector3(0f, -1f, 1f),
            OblivionWaterDisplacementComposition.DecodeNormal(new Vector3(0.5f, 0f, 1f)));

        var result = OblivionWaterDisplacementComposition.ComposeNormal(
            Vector3.UnitZ, new Vector3(1f, 0.5f, 0.5f), 0.5f);
        Assert.Equal(0.70710678f, result.X, 6);
        Assert.Equal(0f, result.Y);
        Assert.Equal(0.70710678f, result.Z, 6);

        // The incoming global normal can be non-unit after retail's XY distance attenuation.
        // Prematurely normalizing it would instead produce a 45-degree direction here.
        result = OblivionWaterDisplacementComposition.ComposeNormal(
            new Vector3(0f, 0f, 2f), new Vector3(1f, 0.5f, 0.5f), 0.5f);
        Assert.Equal(0.44721360f, result.X, 6);
        Assert.Equal(0.89442719f, result.Z, 6);
    }

    [Fact]
    public void ZeroBlendPreservesTheGlobalDirection()
    {
        var result = OblivionWaterDisplacementComposition.ComposeNormal(
            new Vector3(3f, 0f, 4f), new Vector3(0.5f, 0.5f, 1f), 0f);
        Assert.Equal(0.6f, result.X, 6);
        Assert.Equal(0f, result.Y);
        Assert.Equal(0.8f, result.Z, 6);
    }

    [Fact]
    public void OnlyOblivionCanBindTheDiagnosticSource()
    {
        foreach (var game in Enum.GetValues<BethesdaGame>())
        {
            Assert.Equal(game == BethesdaGame.Oblivion,
                OblivionWaterDisplacementComposition.IsSourceBound(game, 0u));
            Assert.Equal(game == BethesdaGame.Oblivion,
                OblivionWaterDisplacementComposition.IsRouteEnabled(game, true, 0u, 1f));
            Assert.False(OblivionWaterDisplacementComposition.IsSourceBound(game, uint.MaxValue));
        }
    }

    [Theory]
    [InlineData(true, 23u, 1f, true)]
    [InlineData(false, 23u, 1f, false)]
    [InlineData(true, uint.MaxValue, 1f, false)]
    [InlineData(true, 23u, 0f, false)]
    public void RouteGatesOnRipplesSourceAndRadius(bool ripples, uint index, float radius, bool expected)
    {
        Assert.Equal(expected,
            OblivionWaterDisplacementComposition.IsRouteEnabled(BethesdaGame.Oblivion, ripples, index, radius));
    }

    [Fact]
    public void ZeroBlendProbeRetainsAValidRouteButHasZeroWeight()
    {
        Assert.True(OblivionWaterDisplacementComposition.IsRouteEnabled(BethesdaGame.Oblivion, true, 23u, 1f));
        Assert.Equal(0f, OblivionWaterDisplacementComposition.GetBlendWeight(
            new Vector2(0.5f), 1f,
            OblivionWaterDisplacementComposition.GetProbeBlendAmount(ProbeMode.NeutralZeroBlend)));
    }

    [Fact]
    public void NeutralProbeIsACompleteOpaqueRgba8FlatNormalTexture()
    {
        var pixels = OblivionWaterDisplacementComposition.GenerateProbeTexture(ProbeMode.NeutralZeroBlend);
        Assert.Equal(256 * 256 * 4, pixels.Length);
        for (var i = 0; i < pixels.Length; i += 4)
        {
            Assert.Equal(128, pixels[i]);
            Assert.Equal(128, pixels[i + 1]);
            Assert.Equal(255, pixels[i + 2]);
            Assert.Equal(255, pixels[i + 3]);
        }
    }

    [Fact]
    public void RadialProbeIsDeterministicWithOppositeCornerSlopesAndFlatCenter()
    {
        var pixels = OblivionWaterDisplacementComposition.GenerateProbeTexture(ProbeMode.RadialImpulse);
        Assert.Equal(256 * 256 * 4, pixels.Length);
        Assert.Equal(pixels, OblivionWaterDisplacementComposition.GenerateProbeTexture(ProbeMode.RadialImpulse));
        Assert.Equal(new byte[] { 54, 54, 201, 255 }, pixels[..4]);
        Assert.Equal(new byte[] { 201, 201, 201, 255 }, pixels[^4..]);
        var center = (128 * 256 + 128) * 4;
        Assert.Equal(new byte[] { 128, 128, 255, 255 }, pixels[center..(center + 4)]);
    }
}
