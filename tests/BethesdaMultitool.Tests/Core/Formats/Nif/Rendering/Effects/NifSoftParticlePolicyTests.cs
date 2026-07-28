using System.Numerics;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Effects;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Effects;

public sealed class NifSoftParticlePolicyTests
{
    [Theory]
    [InlineData(@"effects\tests\testsmokepillermesh01.nif")]
    [InlineData(@"effects\NV\NVLimestoneDustStormHalfViz.NIF")]
    [InlineData(@"effects\ambient\FXMistLow01Long.NIF")]
    [InlineData(@"meshes/effects/ambient/FXMistLow01LongHalfVis.NIF")]
    public void Resolve_NamedFalloutEffectMeshes_UsesScopedFallback(string path)
    {
        var settings = NifSoftParticlePolicy.Resolve(Candidate(path));

        Assert.True(settings.Enabled);
        Assert.Equal(NifSoftParticleSource.EffectsGeometryFallback, settings.Source);
        Assert.Equal(NifSoftParticlePolicy.DefaultFalloffDepth, settings.FalloffDepth);
        Assert.Equal(NifSoftParticleFadeTarget.Alpha, settings.FadeTarget);
    }

    [Fact]
    public void Resolve_BakedParticleCloud_UsesParticleFallbackOutsideEffectsFolder()
    {
        var settings = NifSoftParticlePolicy.Resolve(Candidate(
            @"clutter\weather\particlecloud.nif", isParticleCloud: true));

        Assert.True(settings.Enabled);
        Assert.Equal(NifSoftParticleSource.ParticleSystemFallback, settings.Source);
    }

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Resolve_NeverSoftensDepthWritingDecalOrOpaqueGeometry(
        bool depthWritingBlend,
        bool isDecal,
        bool alphaBlendOff)
    {
        var settings = NifSoftParticlePolicy.Resolve(Candidate(
            @"effects\NV\NVLimestoneDustStormHalfViz.NIF",
            !alphaBlendOff,
            depthWritingBlend,
            isDecal,
            true,
            authoredFalloffDepth: 250f));

        Assert.False(settings.Enabled);
    }

    [Fact]
    public void Resolve_OrdinaryAlphaGeometry_RemainsHardIntersecting()
    {
        var ordinaryWindow = NifSoftParticlePolicy.Resolve(Candidate(
            @"architecture\goodsprings\windowglass.nif"));
        var unmarkedEffectsMesh = NifSoftParticlePolicy.Resolve(Candidate(
            @"effects\ambient\ordinaryalphaplane.nif"));

        Assert.False(ordinaryWindow.Enabled);
        Assert.False(unmarkedEffectsMesh.Enabled);
    }

    [Fact]
    public void Resolve_EffectsGeometrySignals_StayFolderScoped()
    {
        var effectsFalloff = NifSoftParticlePolicy.Resolve(Candidate(
            @"effects\ambient\FXSmokeMed01.nif", hasEffectFalloff: true));
        var architectureFalloff = NifSoftParticlePolicy.Resolve(Candidate(
            @"architecture\fakefalloffwindow.nif", hasEffectFalloff: true));

        Assert.Equal(NifSoftParticleSource.EffectsGeometryFallback, effectsFalloff.Source);
        Assert.False(architectureFalloff.Enabled);
    }

    [Fact]
    public void Resolve_AuthoredDepth_WinsAndClampsToSafeRange()
    {
        var settings = NifSoftParticlePolicy.Resolve(Candidate(
            @"magic\authored.nif", authoredFalloffDepth: 99999f));

        Assert.Equal(NifSoftParticleSource.AuthoredSoftEffect, settings.Source);
        Assert.Equal(NifSoftParticlePolicy.MaximumFalloffDepth, settings.FalloffDepth);
    }

    [Theory]
    [InlineData(6, 7, (int)NifSoftParticleFadeTarget.Alpha)]
    [InlineData(6, 0, (int)NifSoftParticleFadeTarget.Alpha)]
    [InlineData(0, 0, (int)NifSoftParticleFadeTarget.ColorTowardZero)]
    [InlineData(1, 2, (int)NifSoftParticleFadeTarget.ColorTowardWhite)]
    [InlineData(4, 1, (int)NifSoftParticleFadeTarget.ColorTowardWhite)]
    public void ResolveFadeTarget_PreservesBlendNeutral(byte src, byte dst, int expected)
    {
        Assert.Equal((NifSoftParticleFadeTarget)expected, NifSoftParticlePolicy.ResolveFadeTarget(src, dst));
    }

    private static NifSoftParticleCandidate Candidate(
        string path,
        bool alphaBlend = true,
        bool depthWritingBlend = false,
        bool isDecal = false,
        bool isParticleCloud = false,
        bool isBillboard = false,
        bool hasEffectFalloff = false,
        float authoredFalloffDepth = 0f,
        byte srcBlendMode = 6,
        byte dstBlendMode = 7)
    {
        return new NifSoftParticleCandidate(
            path,
            alphaBlend,
            depthWritingBlend,
            isDecal,
            isParticleCloud,
            isBillboard,
            hasEffectFalloff,
            authoredFalloffDepth,
            srcBlendMode,
            dstBlendMode);
    }
}

public sealed class NifSoftParticleDepthMathTests
{
    private const float Near = 0.1f;
    private const float Far = 1000f;

    [Fact]
    public void DepthBinding_EncodesSingleSampleAndMsaaWithoutSlotZeroCollision()
    {
        var singleSample = NifSoftParticleDepthBinding.Encode(0, 1);
        var multisampled = NifSoftParticleDepthBinding.Encode(42, 4);

        Assert.Equal(1f, singleSample);
        Assert.True(NifSoftParticleDepthBinding.TryDecode(
            singleSample, out var singleSlot, out var singleIsMsaa));
        Assert.Equal(0u, singleSlot);
        Assert.False(singleIsMsaa);

        Assert.Equal(-43f, multisampled);
        Assert.True(NifSoftParticleDepthBinding.TryDecode(
            multisampled, out var msaaSlot, out var msaa));
        Assert.Equal(42u, msaaSlot);
        Assert.True(msaa);

        Assert.False(NifSoftParticleDepthBinding.TryDecode(
            NifSoftParticleDepthBinding.Disabled, out _, out _));
    }

    [Fact]
    public void LinearizeReversedDepth_MapsNearAndFarExactly()
    {
        Assert.Equal(Near, NifSoftParticleDepthMath.LinearizeReversedDepth(1f, Near, Far), 5);
        Assert.Equal(Far, NifSoftParticleDepthMath.LinearizeReversedDepth(0f, Near, Far), 2);
    }

    [Fact]
    public void Evaluate_OpaqueBehindParticle_ProducesWorldSpaceFeather()
    {
        var result = NifSoftParticleDepthMath.Evaluate(
            NdcAtDistance(60f),
            NdcAtDistance(10f),
            Near,
            Far,
            100f);

        Assert.True(result.Visible);
        Assert.Equal(50f, result.SceneGap, 2);
        Assert.Equal(0.5f, result.Feather, 3);
    }

    [Fact]
    public void Evaluate_ParticleBehindOpaqueScene_IsRejected()
    {
        var result = NifSoftParticleDepthMath.Evaluate(
            NdcAtDistance(10f),
            NdcAtDistance(60f),
            Near,
            Far,
            100f);

        Assert.False(result.Visible);
        Assert.Equal(0f, result.Feather);
    }

    [Fact]
    public void Evaluate_EqualDepth_PreservesHardTieButSoftlyFadesIntersection()
    {
        var ndc = NdcAtDistance(25f);
        var result = NifSoftParticleDepthMath.Evaluate(ndc, ndc, Near, Far, 100f);

        Assert.True(result.Visible);
        Assert.Equal(0f, result.Feather);
    }

    [Fact]
    public void ApplyFade_UsesCorrectNeutralForEachBlendFamily()
    {
        var source = new Vector4(0.4f, 0.6f, 0.8f, 0.5f);

        Assert.Equal(
            new Vector4(0.4f, 0.6f, 0.8f, 0.25f),
            NifSoftParticleDepthMath.ApplyFade(source, 0.5f, NifSoftParticleFadeTarget.Alpha));
        Assert.Equal(
            new Vector4(0.2f, 0.3f, 0.4f, 0.25f),
            NifSoftParticleDepthMath.ApplyFade(source, 0.5f, NifSoftParticleFadeTarget.ColorTowardZero));
        Assert.Equal(
            new Vector4(0.7f, 0.8f, 0.9f, 0.25f),
            NifSoftParticleDepthMath.ApplyFade(source, 0.5f, NifSoftParticleFadeTarget.ColorTowardWhite));
    }

    private static float NdcAtDistance(float distance)
    {
        return (Near * Far / distance - Near) / (Far - Near);
    }
}