using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class WeatherCloudTransitionTests
{
    [Fact]
    public void Resolve_BlendsOneRecoveredVelocityBeforeOffsetIntegration()
    {
        var current = Weather(15, @"textures\sky\current.dds", 0.5f, -0.25f);
        var outgoing = Weather(15, @"textures\sky\outgoing.dds", -1f, 0.75f);

        var result = WeatherCloudTransitionResolver.Resolve(current, outgoing, 15, 0.25f);

        // Independent Skyrim oracle: (outgoing * .75 + current * .25) * .01 UV/s.
        Assert.Equal(-0.00625f, result.ScrollVelocity.X, 6);
        Assert.Equal(0.005f, result.ScrollVelocity.Y, 6);
        Assert.Equal(0.25f, result.CurrentWeatherWeight);
        Assert.Equal(0.75f, result.OutgoingWeatherWeight);
        Assert.False(result.UsesSameTexture);
    }

    [Theory]
    [InlineData(0f, -0.01f, 0.0075f)]
    [InlineData(0.25f, -0.00625f, 0.005f)]
    [InlineData(1f, 0.005f, -0.0025f)]
    public void Resolve_EndpointAndMidpointVelocitiesAdvanceOneWrappedOffset(
        float currentWeight,
        float expectedU,
        float expectedV)
    {
        var current = Weather(15, @"textures\sky\current.dds", 0.5f, -0.25f);
        var outgoing = Weather(15, @"textures\sky\outgoing.dds", -1f, 0.75f);

        var result = WeatherCloudTransitionResolver.Resolve(
            current, outgoing, 15, currentWeight);
        var offset = WeatherCloudTransitionResolver.AdvanceOffset(
            new Vector2(0.998f, 0.999f), result.ScrollVelocity, 2f);

        Assert.Equal(expectedU, result.ScrollVelocity.X, 6);
        Assert.Equal(expectedV, result.ScrollVelocity.Y, 6);
        Assert.Equal(
            new Vector2(
                Wrap(0.998f + expectedU * 2f),
                Wrap(0.999f + expectedV * 2f)),
            offset);
    }

    [Fact]
    public void Resolve_RecognizesEquivalentTexturePathsForSingleDrawCoalescing()
    {
        var current = Weather(3, @"textures\sky\Clouds.dds", 0.4f, 0.2f);
        var outgoing = Weather(3, @"SKY/Clouds.DDS", -0.2f, 0.6f);

        var result = WeatherCloudTransitionResolver.Resolve(current, outgoing, 3, 0.5f);

        Assert.True(result.UsesSameTexture);
        Assert.Equal(new Vector2(0.001f, 0.004f), result.ScrollVelocity);
        Assert.Equal(1f, result.CurrentTextureWeight);
        Assert.Equal(0f, result.OutgoingTextureWeight);
    }

    [Fact]
    public void SameTextureSingleSample_OpaqueHalfBlendDoesNotLeakBackground()
    {
        var current = new Vector4(1f, 0f, 0f, 1f);
        var outgoing = new Vector4(0f, 0f, 1f, 1f);
        var background = new Vector3(0f, 1f, 0f);

        var transitioned = WeatherCloudTransitionResolver.BlendSample(current, outgoing, 0.5f);
        var composited = new Vector3(transitioned.X, transitioned.Y, transitioned.Z) * transitioned.W
                         + background * (1f - transitioned.W);

        // Independent single-property oracle: .5 current + .5 outgoing, alpha 1; no background term.
        Assert.Equal(new Vector4(0.5f, 0f, 0.5f, 1f), transitioned);
        Assert.Equal(new Vector3(0.5f, 0f, 0.5f), composited);
    }

    [Fact]
    public void Resolve_WithoutOutgoingWeather_IsAtomic()
    {
        var current = Weather(0, @"sky\clouds.dds", 0.25f, -0.5f);

        var result = WeatherCloudTransitionResolver.Resolve(current, null, 0, 0.1f);

        Assert.Equal(1f, result.CurrentWeatherWeight);
        Assert.Equal(0f, result.OutgoingWeatherWeight);
        Assert.Equal(new Vector2(0.0025f, -0.005f), result.ScrollVelocity);
        Assert.Null(result.OutgoingLayer);
    }

    [Fact]
    public void Resolve_OutgoingWeatherWithoutLayerStillFadesCurrentTexture()
    {
        var current = Weather(2, @"sky\clouds.dds", 0.25f, 0f);

        var result = WeatherCloudTransitionResolver.Resolve(
            current, new WeatherRecord(), 2, 0.4f);

        Assert.True(result.HasOutgoingWeather);
        Assert.Null(result.OutgoingLayer);
        Assert.Equal(0.4f, result.CurrentTextureWeight);
        Assert.Equal(0.6f, result.OutgoingTextureWeight);
        // Missing authored motion is still, so only the current weather's 40% contribution remains.
        Assert.Equal(0.001f, result.ScrollVelocity.X, 6);
        Assert.Equal(0f, result.ScrollVelocity.Y);
    }

    [Fact]
    public void AdvanceOffset_IntegratesChangingWeightsAcrossHiddenFrames()
    {
        var current = Weather(5, @"sky\current.dds", 0.8f, -0.2f);
        var outgoing = Weather(5, @"sky\outgoing.dds", -0.4f, 0.6f);
        var offset = Vector2.Zero;

        // Independent Clouds::Update oracle: state advances every frame even when no draw is visible.
        foreach (var weight in new[] { 0f, 0.5f, 1f })
        {
            var transition = WeatherCloudTransitionResolver.Resolve(
                current, outgoing, 5, weight);
            offset = WeatherCloudTransitionResolver.AdvanceOffset(
                offset, transition.ScrollVelocity, 2f);
        }

        // Sum of (-.004,.006), (.002,.002), (.008,-.002), each integrated for two seconds.
        Assert.Equal(0.012f, offset.X, 6);
        Assert.Equal(0.012f, offset.Y, 6);
    }

    [Fact]
    public void Resolve_SparseSourceIndexUsesSemanticLayersOnBothSides()
    {
        var current = Weather(19, @"sky\clouds.dds", 0.8f, 0f);
        var outgoing = Weather(19, @"sky\clouds.dds", 0f, -0.4f);

        var result = WeatherCloudTransitionResolver.Resolve(current, outgoing, 19, 0.75f);

        Assert.Equal(19, result.CurrentLayer?.SourceIndex);
        Assert.Equal(19, result.OutgoingLayer?.SourceIndex);
        Assert.Equal(new Vector2(0.006f, -0.001f), result.ScrollVelocity);
    }

    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.5f, 0.025f)]
    [InlineData(1f, 0.1f)]
    public void Resolve_FnvBlendsOnamAndWindBeforeMultiplication(
        float currentWeatherWeight,
        float expectedVelocity)
    {
        var current = LegacyWeather(255, 255);
        var outgoing = LegacyWeather(0, 0);

        var result = WeatherCloudTransitionResolver.Resolve(
            current,
            outgoing,
            0,
            currentWeatherWeight,
            BethesdaGame.FalloutNewVegas);

        // PC retail Clouds::Update / Sky::UpdateWind oracle:
        // .1 * lerp(0, 1, w) * lerp(0, 1, w). At w=.5 this is .025 UV/s;
        // the old blend-of-products was .05 UV/s and therefore moved 2x too fast.
        Assert.Equal(expectedVelocity, result.ScrollVelocity.X, 7);
        Assert.Equal(0f, result.ScrollVelocity.Y);
    }

    [Fact]
    public void Resolve_FnvStaticWeatherKeepsRecoveredEndpointRate()
    {
        var current = LegacyWeather(65, 50);

        var result = WeatherCloudTransitionResolver.Resolve(
            current,
            null,
            0,
            0.25f,
            BethesdaGame.FalloutNewVegas);

        Assert.Equal(0.004998078f, result.ScrollVelocity.X, 7);
        Assert.Equal(1f, result.CurrentWeatherWeight);
    }

    private static WeatherRecord Weather(int sourceIndex, string texture, float u, float v)
    {
        return new WeatherRecord
        {
            CloudLayers =
            [
                new WeatherCloudLayer
                {
                    SourceIndex = sourceIndex,
                    Texture = texture,
                    SpeedU = u,
                    SpeedV = v
                }
            ]
        };
    }

    private static WeatherRecord LegacyWeather(byte speedByte, byte windByte)
    {
        return new WeatherRecord
        {
            Data = new WeatherData { WindSpeed = windByte },
            CloudSpeedsX = [speedByte / 255f],
            CloudLayers =
            [
                new WeatherCloudLayer
                {
                    SourceIndex = 0,
                    Texture = @"sky\clouds.dds",
                    SpeedU = speedByte / 255f
                }
            ]
        };
    }

    private static float Wrap(float value)
    {
        return value - MathF.Floor(value);
    }
}