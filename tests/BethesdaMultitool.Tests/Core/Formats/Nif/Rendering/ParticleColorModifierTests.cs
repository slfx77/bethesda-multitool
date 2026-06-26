using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Particles;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

/// <summary>
///     Covers <see cref="ColorModifierDefinition.Sample" /> — the BSPSysSimpleColorModifier gradient + the
///     fade-in/out envelope. Pins the FXDustWhirlWind01 case: Color1 is the base dust colour, Colors[0]/[2]
///     are black with all transition percents = 0, so a naive "fall through to Color2" makes particles black
///     (invisible). The fix resolves degenerate windows to Color1, and the fade envelope dims birth/death
///     particles (the additive over-glow remedy).
/// </summary>
public sealed class ParticleColorModifierTests
{
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

        // Birth (t=0): fully faded → all channels 0.
        var birth = m.Sample(0f, Vector4.One);
        Assert.Equal(0f, birth.W, 3);
        Assert.Equal(0f, birth.X, 3);

        // Death (t=1): past FadeOut=0.35, envelope = (1-1)/(1-0.35) = 0 → faded out.
        var death = m.Sample(1f, Vector4.One);
        Assert.Equal(0f, death.W, 3);

        // Just inside fade-out (t=0.675 = midpoint of [0.35,1]) ⇒ envelope ≈ 0.5.
        var mid = m.Sample(0.675f, Vector4.One);
        Assert.InRange(mid.W, 0.25f, 0.35f); // 0.6 (Color1 alpha) × ~0.5 envelope
    }

    [Fact]
    public void Sample_NonDegenerateWindows_BlendsAcrossThreeColors()
    {
        var m = new ColorModifierDefinition
        {
            IsSimpleColor = true,
            FadeInPercent = 0f, FadeOutPercent = 1f, // no envelope, isolate the gradient
            Color1StartPercent = 0f, Color1EndPercent = 0.25f,
            Color2StartPercent = 0.75f, Color2EndPercent = 1f,
            Color0 = new Vector4(1, 0, 0, 1),
            Color1 = new Vector4(0, 1, 0, 1),
            Color2 = new Vector4(0, 0, 1, 1),
        };

        Assert.Equal(new Vector4(1, 0, 0, 1), m.Sample(0f, Vector4.One));   // start = Color0 (red)
        Assert.Equal(new Vector4(0, 1, 0, 1), m.Sample(0.5f, Vector4.One)); // middle = Color1 (green)
        Assert.Equal(new Vector4(0, 0, 1, 1), m.Sample(1f, Vector4.One));   // end = Color2 (blue)
    }
}
