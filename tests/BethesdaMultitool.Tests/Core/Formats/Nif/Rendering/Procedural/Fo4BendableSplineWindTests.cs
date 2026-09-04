using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Procedural;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Procedural;

public sealed class Fo4BendableSplineWindTests
{
    private static readonly Vector2 HostFallback = Vector2.Normalize(new Vector2(0.82f, 0.57f));

    [Fact]
    public void CommonwealthClear_ProducesRecoveredWeatherAndShaderConstants()
    {
        var weather = new WeatherData
        {
            WindSpeed = 120,
            WindDirection = 15,
            WindDirectionRange = 43,
            WindTurbulence = 48
        };

        var resolved = Fo4BendableSplineWind.ResolveWeather(
            weather,
            outgoing: null,
            currentWeight: 1f,
            isInterior: false,
            HostFallback);
        var constants = Fo4BendableSplineWind.BuildConstants(
            resolved.Direction,
            resolved.NormalizedSpeed,
            resolved.NormalizedTurbulence,
            flexibility: 0.8f,
            animationSeconds: 6d,
            animationsEnabled: true);

        Assert.Equal(120f / 255f, resolved.NormalizedSpeed, 7);
        Assert.Equal(48f / 255f, resolved.NormalizedTurbulence, 7);
        Assert.Equal(15f / 255f * MathF.Tau, resolved.DirectionRadians, 7);
        Assert.Equal(43f / 255f * 180f, resolved.DirectionRangeDegrees, 6);
        Assert.Equal(0.932472229f, resolved.Direction.X, 6);
        Assert.Equal(0.361241666f, resolved.Direction.Y, 6);
        Assert.Equal(
            Fo4BendableSplineWindDirectionSelection.CurrentWeatherCenter,
            resolved.DirectionSelection);

        Assert.Equal(Fo4BendableSplineWind.LowestFrequency, 0.5f);
        Assert.Equal(Fo4BendableSplineWind.HighestFrequency, 4f);
        Assert.Equal(Fo4BendableSplineWind.LowestSpeedLowMultiplier, 0f);
        Assert.Equal(Fo4BendableSplineWind.HighestSpeedHighMultiplier, 1.5f);
        Assert.Equal(1.158823529f, constants.WindVectorEx.Z, 6);
        Assert.Equal(114.602076125f, constants.WindVectorEx.X, 5);
        Assert.Equal(154.463667820f, constants.WindVectorEx.Y, 5);
        Assert.Equal(resolved.DirectionRadians, constants.WindVector.X, 6);
        Assert.Equal(0.8f, constants.WindVector.Y);
        Assert.Equal(constants.WindVector.Z, constants.WindVector.W);
    }

    [Fact]
    public void PackTimer_UsesRecoveredTwoFloatOperations()
    {
        var packed = Fo4BendableSplineWind.PackTimer(6d);

        Assert.Equal(unchecked((int)0x3D80ADF6), BitConverter.SingleToInt32Bits(packed));
        Assert.Equal(0.0628318041563034f, packed);
    }

    [Fact]
    public void DeformWorldPosition_MatchesNumericEquationFixture()
    {
        var constants = new Fo4BendableSplineWindConstants(
            new Vector4(0.75f, 0.8f, 0.062831804f, 0.062831804f),
            new Vector4(114.602076f, 154.463668f, 1.158823529f, 0f));

        var deformed = Fo4BendableSplineWind.DeformWorldPosition(
            new Vector3(100f, 200f, 300f),
            new Vector3(12.5f, -4f, 8.25f),
            packedAlpha: 0.6f,
            constants);

        Assert.Equal(113.9203f, deformed.X, 3);
        Assert.Equal(212.9681f, deformed.Y, 3);
        Assert.Equal(300f, deformed.Z);
    }

    [Fact]
    public void ZeroAlpha_AnchorsVertexAndDirectionRotatesOnlyWorldXyOffset()
    {
        var rest = new Vector3(10f, 20f, 30f);
        var placement = new Vector3(100f, 200f, 300f);
        var east = new Fo4BendableSplineWindConstants(
            new Vector4(0f, 0.8f, 0f, 0f),
            new Vector4(100f, 100f, 0.5f, 0f));
        var north = east with { WindVector = east.WindVector with { X = MathF.PI * 0.5f } };

        Assert.Equal(rest, Fo4BendableSplineWind.DeformWorldPosition(
            rest, placement, packedAlpha: 0f, east));
        var eastResult = Fo4BendableSplineWind.DeformWorldPosition(
            rest, placement, packedAlpha: 1f, east);
        var northResult = Fo4BendableSplineWind.DeformWorldPosition(
            rest, placement, packedAlpha: 1f, north);

        Assert.Equal(rest.Y, eastResult.Y, 4);
        Assert.Equal(rest.X, northResult.X, 4);
        Assert.Equal(eastResult.X - rest.X, northResult.Y - rest.Y, 4);
        Assert.Equal(rest.Z, northResult.Z);
    }

    [Fact]
    public void InvalidOrDisabledInputs_FailClosedToRestPose()
    {
        var rest = new Vector3(10f, 20f, 30f);
        var placement = new Vector3(100f, 200f, 300f);
        var zeroFrequency = new Fo4BendableSplineWindConstants(
            new Vector4(0f, 1f, 0f, 0f),
            new Vector4(10f, 20f, 0f, 0f));
        var nonFinite = zeroFrequency with
        {
            WindVectorEx = new Vector4(10f, float.NaN, 1f, 0f)
        };
        var disabled = Fo4BendableSplineWind.BuildConstants(
            Vector2.UnitX,
            normalizedSpeed: 1f,
            normalizedTurbulence: 1f,
            flexibility: 1f,
            animationSeconds: 6d,
            animationsEnabled: false);

        Assert.Equal(rest, Fo4BendableSplineWind.DeformWorldPosition(
            rest, placement, packedAlpha: 1f, zeroFrequency));
        Assert.Equal(rest, Fo4BendableSplineWind.DeformWorldPosition(
            rest, placement, packedAlpha: 1f, nonFinite));
        Assert.Equal(Vector4.Zero, disabled.WindVectorEx);
        Assert.Equal(rest, Fo4BendableSplineWind.DeformWorldPosition(
            rest, placement, packedAlpha: 1f, disabled));
    }

    [Fact]
    public void WeatherTransition_BlendsSpeedAndTurbulenceButPinsCurrentCenter()
    {
        var current = new WeatherData
        {
            WindSpeed = 200,
            WindTurbulence = 100,
            WindDirection = 64,
            WindDirectionRange = 32
        };
        var outgoing = new WeatherData
        {
            WindSpeed = 40,
            WindTurbulence = 20,
            WindDirection = 192,
            WindDirectionRange = 16
        };

        var resolved = Fo4BendableSplineWind.ResolveWeather(
            current, outgoing, currentWeight: 0.25f, isInterior: false, HostFallback);

        Assert.Equal(80f / 255f, resolved.NormalizedSpeed, 7);
        Assert.Equal(40f / 255f, resolved.NormalizedTurbulence, 7);
        Assert.Equal(64f / 255f * MathF.Tau, resolved.DirectionRadians, 7);
        Assert.Equal(32f / 255f * 180f, resolved.DirectionRangeDegrees, 6);
        Assert.Equal(
            Fo4BendableSplineWindDirectionSelection.CurrentWeatherCenter,
            resolved.DirectionSelection);
    }

    [Fact]
    public void MissingTailAndInterior_HaveExplicitDeterministicFallbacks()
    {
        var legacy = Fo4BendableSplineWind.ResolveWeather(
            new WeatherData { WindSpeed = 255 },
            outgoing: null,
            currentWeight: 1f,
            isInterior: false,
            HostFallback);
        var outgoingFallback = Fo4BendableSplineWind.ResolveWeather(
            new WeatherData { WindSpeed = 255 },
            new WeatherData { WindDirection = 128, WindDirectionRange = 64 },
            currentWeight: 1f,
            isInterior: false,
            HostFallback);
        var interior = Fo4BendableSplineWind.ResolveWeather(
            new WeatherData
            {
                WindSpeed = 255,
                WindTurbulence = 255,
                WindDirection = 128,
                WindDirectionRange = 255
            },
            outgoing: null,
            currentWeight: 1f,
            isInterior: true,
            HostFallback);

        Assert.Equal(HostFallback, legacy.Direction);
        Assert.Equal(Fo4BendableSplineWindDirectionSelection.HostFallback,
            legacy.DirectionSelection);
        Assert.Equal(Fo4BendableSplineWindDirectionSelection.OutgoingWeatherCenterFallback,
            outgoingFallback.DirectionSelection);
        Assert.Equal(Vector2.UnitX, interior.Direction);
        Assert.Equal(0f, interior.NormalizedSpeed);
        Assert.Equal(0f, interior.NormalizedTurbulence);
        Assert.Equal(Fo4BendableSplineWindDirectionSelection.InteriorZero,
            interior.DirectionSelection);
    }

    [Fact]
    public void BoundsExpansion_UsesPackedAlphaAndGlobalCompiledDefaultLimit()
    {
        Assert.Equal(405f, Fo4BendableSplineWind.MaximumDefaultDisplacement);
        Assert.Equal(202.5f, Fo4BendableSplineWind.BoundsExpansion(0.5f));
        Assert.Equal(405f, Fo4BendableSplineWind.BoundsExpansion(2f));
        Assert.Equal(0f, Fo4BendableSplineWind.BoundsExpansion(float.NaN));
    }
}
