using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Parser;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using BethesdaMultitool.Tests.Helpers;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers <see cref="ColorModifierDefinition.Sample" /> — the BSPSysSimpleColorModifier RGB gradient and
///     independent fade-in/out alpha curve. Pins the FXDustWhirlWind01 case: Color1 is the base dust colour, Colors[0]/[2]
///     are black with all transition percents = 0, so a naive "fall through to Color2" makes particles black
///     (invisible). Degenerate windows resolve to Color1, while FadeIn/FadeOut independently interpolate
///     the authored endpoint alpha values; RGB remains on the authored three-key curve.
/// </summary>
[Collection(SequentialIntegrationGroup.Name)]
public sealed class ParticleColorModifierTests
{
    private const string RetailSandDust02 =
        @"TestOutput\fnv-opacity-audit\meshes\effects\nv\sanddust\sanddust02.nif";

    private static readonly Vector4 Tan = new(0.48f, 0.46f, 0.44f, 0.6f);

    private static ColorModifierDefinition FxDustLike() => new()
    {
        Kind = ParticleModifierKind.Color,
        IsSimpleColor = true,
        FadeInPercent = 0.05f,
        FadeOutPercent = 0.35f,
        Color1StartPercent = 0f,
        Color1EndPercent = 0f,
        Color2StartPercent = 0f,
        Color2EndPercent = 0f,
        Color0 = Vector4.Zero,
        Color1 = Tan,
        Color2 = Vector4.Zero,
    };

    [Fact]
    public void Sample_DegenerateWindows_ResolvesToBaseColorNotBlack()
    {
        // Mid-life, past fade-in and before fade-out (t=0.2 ∈ [0.05, 0.35]) ⇒ full Color1 (tan), not black.
        var c = FxDustLike().Sample(0.2f, Vector4.One);
        Assert.Equal(Tan.X, c.X, 3);
        Assert.Equal(Tan.Y, c.Y, 3);
        Assert.Equal(Tan.Z, c.Z, 3);
        Assert.Equal(Tan.W, c.W, 3);
    }

    [Fact]
    public void Sample_FadesInAtBirthAndOutAtDeath()
    {
        var m = FxDustLike();

        // Birth (t=0): alpha starts at Color0.W, but RGB stays on the authored color curve.
        var birth = m.Sample(0f, Vector4.One);
        Assert.Equal(0f, birth.W, 3);
        Assert.Equal(Tan.X, birth.X, 3);
        Assert.Equal(Tan.Y, birth.Y, 3);
        Assert.Equal(Tan.Z, birth.Z, 3);

        // Death (t=1): past FadeOut=0.35, alpha reaches Color2.W = 0.
        var death = m.Sample(1f, Vector4.One);
        Assert.Equal(0f, death.W, 3);

        // Just inside fade-out (t=0.675 = midpoint of [0.35,1]) ⇒ halfway from Color1.W to Color2.W.
        var mid = m.Sample(0.675f, Vector4.One);
        Assert.InRange(mid.W, 0.25f, 0.35f); // halfway from 0.6 to 0
    }

    [Fact]
    public void Sample_NonDegenerateWindows_BlendsAcrossThreeColors()
    {
        var m = new ColorModifierDefinition
        {
            IsSimpleColor = true,
            FadeInPercent = 0f, FadeOutPercent = 1f, // no envelope, isolate the gradient
            // The on-disk order is End-before-Start; these are transition windows [0,.25] and [.75,1].
            Color1EndPercent = 0f, Color1StartPercent = 0.25f,
            Color2EndPercent = 0.75f, Color2StartPercent = 1f,
            Color0 = new Vector4(1, 0, 0, 1),
            Color1 = new Vector4(0, 1, 0, 1),
            Color2 = new Vector4(0, 0, 1, 1),
        };

        Assert.Equal(new Vector4(1, 0, 0, 1), m.Sample(0f, Vector4.One));   // start = Color0 (red)
        Assert.Equal(new Vector4(0, 1, 0, 1), m.Sample(0.5f, Vector4.One)); // middle = Color1 (green)
        Assert.Equal(new Vector4(0, 0, 1, 1), m.Sample(1f, Vector4.One));   // end = Color2 (blue)
    }

    [Theory]
    [InlineData(0.05f, 1f, 0f, 0f, 0.5f)]
    [InlineData(0.30f, 0.5f, 0.5f, 0f, 1f)]
    [InlineData(0.70f, 0f, 0.5f, 0.5f, 1f)]
    [InlineData(0.95f, 0f, 0f, 1f, 0.5f)]
    public void Sample_RetailSandDustEndBeforeStartWindows_MatchEngineRgbAndAlpha(
        float t, float expectedR, float expectedG, float expectedB, float expectedA)
    {
        // SandDust02's retail modifier uses these exact percentages and endpoint-alpha shape (0 -> 1 -> 0).
        // Sentinel RGB values make both transition windows observable without depending on texture sampling.
        var m = new ColorModifierDefinition
        {
            IsSimpleColor = true,
            FadeInPercent = 0.1f,
            FadeOutPercent = 0.9f,
            Color1EndPercent = 0.2f,
            Color1StartPercent = 0.4f,
            Color2EndPercent = 0.6f,
            Color2StartPercent = 0.8f,
            Color0 = new Vector4(1, 0, 0, 0),
            Color1 = new Vector4(0, 1, 0, 1),
            Color2 = new Vector4(0, 0, 1, 0),
        };

        var actual = m.Sample(t, Vector4.One);

        Assert.Equal(expectedR, actual.X, 3);
        Assert.Equal(expectedG, actual.Y, 3);
        Assert.Equal(expectedB, actual.Z, 3);
        Assert.Equal(expectedA, actual.W, 3);
    }

    [Fact]
    public void RetailSandDust02_ParsesAndSamplesAuthoredSimpleColorWindows()
    {
        BucketBTestGuard.SkipUnlessEnabled();
        var path = SampleFileFixture.FindSamplePath(RetailSandDust02);
        Assert.SkipUnless(path is not null,
            "Extracted FNV SandDust02 NIF not present (dev-machine-only asset).");
        var data = File.ReadAllBytes(path!);
        var nif = NifParser.Parse(data);
        Assert.NotNull(nif);

        var definition = Assert.Single(nif!.Blocks.Select((block, index) => (block, index))
            .Where(x => NifParticleSystemParser.IsParticleSystem(x.block.TypeName))
            .Select(x => NifParticleSystemParser.Parse(data, nif, x.index))
            .OfType<ParticleSystemDefinition>());
        var modifier = Assert.IsType<ColorModifierDefinition>(
            Assert.Single(definition.Modifiers, candidate => candidate.Kind == ParticleModifierKind.Color));

        Assert.Equal("BSShaderNoLightingProperty", definition.ShaderPropertyType);
        Assert.True(definition.UsesUnlitShader);
        Assert.Equal(0.1f, modifier.FadeInPercent, 4);
        Assert.Equal(0.9f, modifier.FadeOutPercent, 4);
        Assert.Equal(0.2f, modifier.Color1EndPercent, 4);
        Assert.Equal(0.4f, modifier.Color1StartPercent, 4);
        Assert.Equal(0.6f, modifier.Color2EndPercent, 4);
        Assert.Equal(0.8f, modifier.Color2StartPercent, 4);
        Assert.Equal(0f, modifier.Color0.W, 4);
        Assert.Equal(1f, modifier.Color1.W, 4);
        Assert.Equal(0f, modifier.Color2.W, 4);

        var firstRgbMidpoint = modifier.Sample(0.3f, Vector4.One);
        Assert.Equal((modifier.Color0.X + modifier.Color1.X) * 0.5f, firstRgbMidpoint.X, 3);
        Assert.Equal((modifier.Color0.Y + modifier.Color1.Y) * 0.5f, firstRgbMidpoint.Y, 3);
        Assert.Equal((modifier.Color0.Z + modifier.Color1.Z) * 0.5f, firstRgbMidpoint.Z, 3);
        Assert.Equal(1f, firstRgbMidpoint.W, 3);

        var secondRgbMidpoint = modifier.Sample(0.7f, Vector4.One);
        Assert.Equal((modifier.Color1.X + modifier.Color2.X) * 0.5f, secondRgbMidpoint.X, 3);
        Assert.Equal((modifier.Color1.Y + modifier.Color2.Y) * 0.5f, secondRgbMidpoint.Y, 3);
        Assert.Equal((modifier.Color1.Z + modifier.Color2.Z) * 0.5f, secondRgbMidpoint.Z, 3);
        Assert.Equal(1f, secondRgbMidpoint.W, 3);

        var model = NifGeometryExtractor.Extract(
            data, nif, treatRootsAsIdentity: true, collectBillboards: true);
        Assert.NotNull(model);
        var particleCloud = Assert.Single(model.Submeshes, submesh => submesh.ShapeName == "ParticleCloud");
        Assert.True(particleCloud.IsEmissive);
        Assert.True(particleCloud.HasAlphaBlend);
        Assert.Equal((byte)6, particleCloud.SrcBlendMode);
        Assert.Equal((byte)7, particleCloud.DstBlendMode);
    }

    [Theory]
    [InlineData("BSShaderNoLightingProperty", true)]
    [InlineData("BSEffectShaderProperty", true)]
    [InlineData("BSShaderPPLightingProperty", false)]
    [InlineData("BSLightingShaderProperty", false)]
    [InlineData(null, false)]
    public void UsesUnlitShader_IsScopedToTheAttachedShaderProperty(string? propertyType, bool expected)
    {
        var definition = new ParticleSystemDefinition { ShaderPropertyType = propertyType };

        Assert.Equal(expected, definition.UsesUnlitShader);
    }
}
