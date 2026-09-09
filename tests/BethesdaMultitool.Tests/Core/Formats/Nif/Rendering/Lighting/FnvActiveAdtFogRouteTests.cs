using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Materials;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

public sealed class FnvActiveAdtFogRouteTests
{
    private static readonly float[] ValidWeatherFog = [10f, 30f, 5f, 25f, 2f, 1f];
    private static readonly float[] ValidOutgoingFog = [20f, 60f, 10f, 50f, 4f, 2f];
    private static readonly float[] SignedWeatherFog = [-10f, 200000f, -30f, 100000f, 0.6f, 0.5f];

    [Fact]
    public void TryPack_PreservesTheObservedEndRangeAndPowerWithoutApplyingResolverFloor()
    {
        Assert.True(FnvActiveAdtFog.TryPack(10f, 30f, 0.001f, out var packed));
        Assert.Equal(new Vector3(30f, 20f, 0.001f), packed);
    }

    [Theory]
    [InlineData(0f, 0f, 1f)]
    [InlineData(10f, 10f, 1f)]
    [InlineData(20f, 10f, 1f)]
    [InlineData(0f, 10f, 0f)]
    [InlineData(0f, 10f, -1f)]
    [InlineData(float.NaN, 10f, 1f)]
    [InlineData(0f, float.PositiveInfinity, 1f)]
    [InlineData(0f, 10f, float.NaN)]
    [InlineData(0f, float.Epsilon, 1f)]
    [InlineData(0f, 5.87747175E-39f, 1f)]
    [InlineData(0f, float.MaxValue, 1f)]
    [InlineData(5.87747175E-39f, 1.7632415E-38f, 1f)]
    [InlineData(0f, 10f, float.Epsilon)]
    [InlineData(-float.Epsilon, 10f, 1f)]
    [InlineData(-5.87747175E-39f, 10f, 1f)]
    [InlineData(-1.17549435E-38f, -float.Epsilon, 1f)]
    [InlineData(-1.17549435E-38f, -5.87747175E-39f, 1f)]
    [InlineData(-1.17549435E-38f, float.Epsilon, 1f)]
    [InlineData(-float.MaxValue, float.MaxValue, 1f)]
    [InlineData(float.NegativeInfinity, 10f, 1f)]
    [InlineData(-10f, float.NegativeInfinity, 1f)]
    public void TryPack_RejectsInvalidOrUnsupportedInputWithoutInventingParameters(float start, float end, float power)
    {
        Assert.False(FnvActiveAdtFog.TryPack(start, end, power, out var packed));
        Assert.Equal(Vector3.Zero, packed);
        Assert.False(FnvActiveAdtFog.IsSupported(true, start, end, power, Vector3.Zero, Vector3.Zero, 1f));
    }

    [Fact]
    public void TryPack_AcceptsTheNormalRangeAndReciprocalBoundary()
    {
        const float minimumNormal = 1.17549435E-38f;
        Assert.True(FnvActiveAdtFog.TryPack(0f, minimumNormal, 1f, out var small));
        Assert.Equal(minimumNormal, small.Y);
        Assert.True(FnvActiveAdtFog.TryPack(0f, 1f / minimumNormal, 1f, out var large));
        Assert.Equal(minimumNormal, 1f / large.Y);
        Assert.True(FnvActiveAdtFog.TryPack(minimumNormal, 2f * minimumNormal, minimumNormal, out var normal));
        Assert.Equal(new Vector3(2f * minimumNormal, minimumNormal, minimumNormal), normal);
        Assert.True(FnvActiveAdtFog.TryPack(-minimumNormal, 0f, 1f, out var signedZero));
        Assert.Equal(new Vector3(0f, minimumNormal, 1f), signedZero);
        Assert.True(FnvActiveAdtFog.TryPack(-2f * minimumNormal, -minimumNormal, 1f, out var negative));
        Assert.Equal(new Vector3(-minimumNormal, minimumNormal, 1f), negative);
        Assert.True(FnvActiveAdtFog.TryPack(-1f / minimumNormal, 0f, 1f, out var negativeLarge));
        Assert.Equal(minimumNormal, 1f / negativeLarge.Y);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Skyrim)]
    [InlineData(BethesdaGame.Fallout4)]
    [InlineData(BethesdaGame.Unknown)]
    public void WeatherSource_RemainsGameSpecific(BethesdaGame game)
    {
        Assert.False(FnvActiveAdtFog.HasUnmodifiedWeatherSource(game, ValidWeatherFog));
        Assert.False(AtmosphereState.Resolve(12f, new WeatherRecord { FogDistances = ValidWeatherFog },
            game: game).HasUnmodifiedFnvAdtFogSource);
    }

    [Theory]
    [InlineData("zero-day-range")]
    [InlineData("zero-night-range")]
    [InlineData("reversed-range")]
    [InlineData("zero-power")]
    [InlineData("clamped-positive-power")]
    [InlineData("nonfinite")]
    [InlineData("partial-two")]
    [InlineData("partial-four")]
    [InlineData("extra-modern-fields")]
    public void WeatherResolution_DoesNotAdmitARepairedOrUnprovedSource(string scenario)
    {
        var distances = ValidWeatherFog.ToArray();
        switch (scenario)
        {
            case "zero-day-range":
                distances[0] = 0f;
                distances[1] = 0f;
                break;
            case "zero-night-range":
                distances[2] = 0f;
                distances[3] = 0f;
                break;
            case "reversed-range": distances[1] = 1f; break;
            case "zero-power": distances[4] = 0f; break;
            case "clamped-positive-power": distances[4] = 0.001f; break;
            case "nonfinite": distances[3] = float.NaN; break;
            case "partial-two": distances = distances[..2]; break;
            case "partial-four": distances = distances[..4]; break;
            case "extra-modern-fields": distances = [.. distances, 1f, 1f]; break;
            default: throw new InvalidOperationException(scenario);
        }

        Assert.False(FnvActiveAdtFog.HasUnmodifiedWeatherSource(BethesdaGame.FalloutNewVegas, distances));
        var resolved = AtmosphereState.Resolve(12f, new WeatherRecord { FogDistances = distances },
            game: BethesdaGame.FalloutNewVegas);
        Assert.False(resolved.HasUnmodifiedFnvAdtFogSource);
        Assert.False(IsSupported(resolved));
        if (scenario == "zero-day-range")
        {
            Assert.Equal(1f, resolved.FogFar); // Positive generic repair must not reopen admission.
        }

        if (scenario is "zero-power" or "clamped-positive-power")
        {
            Assert.Equal(0.01f, resolved.FogPower);
        }
    }

    [Fact]
    public void WeatherResolution_AdmitsExactFiniteExteriorAndKeepsMissingSourceClosed()
    {
        var resolved = AtmosphereState.Resolve(12f, new WeatherRecord { FogDistances = ValidWeatherFog },
            game: BethesdaGame.FalloutNewVegas);
        Assert.True(resolved.HasUnmodifiedFnvAdtFogSource);
        Assert.True(IsSupported(resolved));
        Assert.False(AtmosphereState.Resolve(12f, game: BethesdaGame.FalloutNewVegas).HasUnmodifiedFnvAdtFogSource);
        Assert.False(default(AtmosphereState.Resolved).HasUnmodifiedFnvAdtFogSource);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void WeatherTransition_RequiresBothSuppliedSourcesAndKeepsValidInterpolatedParameters(float weight)
    {
        var current = new WeatherRecord { FogDistances = ValidWeatherFog };
        var outgoing = new WeatherRecord { FogDistances = ValidOutgoingFog };
        var resolved = AtmosphereState.ResolveWeatherTransition(12f, current, outgoing, weight,
            game: BethesdaGame.FalloutNewVegas);
        Assert.True(IsSupported(resolved));
        Assert.Equal(60f + (30f - 60f) * weight, resolved.FogFar);
        Assert.Equal(4f + (2f - 4f) * weight, resolved.FogPower);

        var unknown = new WeatherRecord();
        Assert.False(AtmosphereState.ResolveWeatherTransition(12f, current, unknown, weight,
            game: BethesdaGame.FalloutNewVegas).HasUnmodifiedFnvAdtFogSource);
        Assert.False(AtmosphereState.ResolveWeatherTransition(12f, unknown, outgoing, weight,
            game: BethesdaGame.FalloutNewVegas).HasUnmodifiedFnvAdtFogSource);
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(1f)]
    [InlineData(float.NaN)]
    public void WeatherTransition_WithoutOutgoingWeatherPreservesCurrentAndIgnoresUnusedWeight(float weight)
    {
        var current = new WeatherRecord { FogDistances = ValidWeatherFog };
        var expected = AtmosphereState.Resolve(12f, current, game: BethesdaGame.FalloutNewVegas);
        var actual = AtmosphereState.ResolveWeatherTransition(12f, current, null, weight,
            game: BethesdaGame.FalloutNewVegas);
        Assert.Equal(expected, actual);
        Assert.True(IsSupported(actual));
    }

    [Theory]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    [InlineData(float.NegativeInfinity)]
    public void WeatherTransition_WithOutgoingWeatherRejectsNonfiniteAuthoredWeight(float weight)
    {
        var current = new WeatherRecord { FogDistances = ValidWeatherFog };
        var outgoing = new WeatherRecord { FogDistances = ValidOutgoingFog };
        var actual = AtmosphereState.ResolveWeatherTransition(12f, current, outgoing, weight,
            game: BethesdaGame.FalloutNewVegas);
        Assert.False(actual.HasUnmodifiedFnvAdtFogSource);
        Assert.False(IsSupported(actual));
    }

    [Theory]
    [InlineData("missing-source")]
    [InlineData("range-changed")]
    [InlineData("power-changed")]
    [InlineData("dual-color")]
    [InlineData("capped-opacity")]
    [InlineData("invalid-color")]
    public void EffectiveUpload_IsRecheckedAfterSourceAdmission(string scenario)
    {
        var source = true;
        var end = 30f;
        var power = 2f;
        var color = new Vector3(0.2f, 0.3f, 0.4f);
        var farColor = color;
        var max = 1f;
        switch (scenario)
        {
            case "missing-source": source = false; break;
            case "range-changed": end = 10f; break;
            case "power-changed": power = 0f; break;
            case "dual-color": farColor = Vector3.One; break;
            case "capped-opacity": max = 0.5f; break;
            case "invalid-color":
                color = new Vector3(float.NaN);
                farColor = color;
                break;
            default: throw new InvalidOperationException(scenario);
        }

        Assert.False(FnvActiveAdtFog.IsSupported(source, 10f, end, power, color, farColor, max));
    }

    [Theory]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public void Eligibility_RequiresProofOnlyWhenFogIsEnabled(bool enabled, bool supported, bool expected)
    {
        var eligibility = Eligible(enabled, supported);
        Assert.Equal(expected, FnvActiveAdtBasePolicy.IsEligible(eligibility));
        var flags = FnvActiveAdtBasePolicy.ApplyRuntimeFlags(eligibility, 16u);
        Assert.Equal(expected, (flags & FnvActiveAdtBasePolicy.RuntimeActiveAdtFlag) != 0);
        Assert.Equal(16u, flags & 16u);
    }

    [Fact]
    public void SupportedFog_DoesNotAdmitProjectedShadowsLocalLightsOrAnotherGame()
    {
        var supported = Eligible(true, true);
        Assert.False(FnvActiveAdtBasePolicy.IsEligible(supported with { HasProjectedSunShadow = true }));
        Assert.False(FnvActiveAdtBasePolicy.IsEligible(supported with { PlacedLightCount = 1 }));
        Assert.False(FnvActiveAdtBasePolicy.IsEligible(supported with { Game = BethesdaGame.Fallout3 }));
        Assert.False(FnvActiveAdtBasePolicy.IsEligible(supported with { HasAlphaBlend = true }));
    }

    [Fact]
    public void UploadedSourceParameters_KeepVertexInterpolationAndSingleHdrCompositeObservable()
    {
        Assert.True(FnvActiveAdtFog.TryPack(0f, 20f, 2f, out var parameters));
        var a = FnvActiveAdtFog.RecoverForwardClipPosition(new Vector4(0, 0, 1, 1));
        var b = FnvActiveAdtFog.RecoverForwardClipPosition(new Vector4(0, 0, -19, 1));
        var amount = (FnvActiveAdtFog.EvaluateAmount(a, parameters) +
                      FnvActiveAdtFog.EvaluateAmount(b, parameters)) * 0.5f;
        Assert.Equal(0.5f, amount);
        Assert.Equal(0.25f, FnvActiveAdtFog.EvaluateAmount(Vector4.Lerp(a, b, 0.5f), parameters));
        var lit = new Vector3(3f, 1f, 0.5f);
        var fog = new Vector3(0.2f, 0.4f, 0.6f);
        var once = FnvActiveAdtFog.Composite(lit, fog, amount, 1f);
        Assert.Equal(Vector3.Lerp(lit, fog, 0.5f), once);
        Assert.True(once.X > 1f);
        Assert.NotEqual(once, FnvActiveAdtFog.Composite(once, fog, amount, 1f));
    }

    [Theory]
    [InlineData(-1f, 10f, 1f)]
    [InlineData(-10f, 200000f, 0.6f)]
    [InlineData(-30f, -10f, 2f)]
    [InlineData(-10f, 0f, 1f)]
    public void TryPack_AcceptsSignedNormalEndpointsWithoutClamping(float start, float end, float power)
    {
        Assert.True(FnvActiveAdtFog.TryPack(start, end, power, out var packed));
        Assert.Equal(new Vector3(end, end - start, power), packed);
        Assert.True(FnvActiveAdtFog.IsSupported(true, start, end, power, Vector3.Zero, Vector3.Zero, 1f));
    }

    [Fact]
    public void NegativeStart_PreservesNonzeroSourceFogAtZeroProjectedDistance()
    {
        Assert.True(FnvActiveAdtFog.TryPack(-10f, 200000f, 0.6f, out var packed));
        Assert.Equal(new Vector3(200000f, 200010f, 0.6f), packed);
        var amount = FnvActiveAdtFog.EvaluateAmount(Vector4.Zero, packed);
        // Independent real-arithmetic limit is (10/200010)^.6. Allow FP32 subtraction rounding.
        Assert.InRange(amount, 0.00262f, 0.00264f);
        Assert.True(FnvActiveAdtFog.TryPack(0f, 200000f, 0.6f, out var clamped));
        Assert.Equal(0f, FnvActiveAdtFog.EvaluateAmount(Vector4.Zero, clamped));
        Assert.True(FnvActiveAdtFog.TryPack(-30f, -10f, 2f, out var negativeEnd));
        Assert.Equal(1f, FnvActiveAdtFog.EvaluateAmount(Vector4.Zero, negativeEnd));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(9f)]
    [InlineData(12f)]
    [InlineData(23f)]
    public void WeatherResolution_PreservesSignedDayAndNightPairs(float hour)
    {
        var weather = new WeatherRecord { FogDistances = SignedWeatherFog };
        Assert.True(FnvActiveAdtFog.HasUnmodifiedWeatherSource(BethesdaGame.FalloutNewVegas, weather.FogDistances));
        var resolved = AtmosphereState.Resolve(hour, weather, game: BethesdaGame.FalloutNewVegas);
        Assert.InRange(resolved.FogNear, -30f, -10f);
        Assert.InRange(resolved.FogFar, 100000f, 200000f);
        Assert.True(IsSupported(resolved));
        Assert.True(FnvActiveAdtBasePolicy.IsEligible(Eligible(true, IsSupported(resolved))));
    }

    [Theory]
    [InlineData(0f)]
    [InlineData(0.5f)]
    [InlineData(1f)]
    public void WeatherTransition_PreservesSignedSourceAtBothEndpointsAndInterior(float weight)
    {
        var signed = new WeatherRecord { FogDistances = SignedWeatherFog };
        var positive = new WeatherRecord { FogDistances = ValidWeatherFog };
        var resolved = AtmosphereState.ResolveWeatherTransition(12f, signed, positive, weight,
            game: BethesdaGame.FalloutNewVegas);
        Assert.Equal(10f - 20f * weight, resolved.FogNear);
        Assert.True(IsSupported(resolved));
        Assert.False(AtmosphereState.ResolveWeatherTransition(12f, signed, new WeatherRecord(), weight,
            game: BethesdaGame.FalloutNewVegas).HasUnmodifiedFnvAdtFogSource);
    }

    private static bool IsSupported(AtmosphereState.Resolved resolved)
    {
        return FnvActiveAdtFog.IsSupported(
            resolved.HasUnmodifiedFnvAdtFogSource, resolved.FogNear, resolved.FogFar, resolved.FogPower,
            resolved.FogColor, resolved.FogFarColor, resolved.FogMaxOpacity);
    }

    private static FnvActiveAdtBaseEligibility Eligible(bool enabled, bool supported)
    {
        return new FnvActiveAdtBaseEligibility(BethesdaGame.FalloutNewVegas, true, 0, false, enabled, false, false, 1f,
            false,
            FnvClassicBasicShaderMode.Sls1009, HasSupportedFog: supported);
    }
}