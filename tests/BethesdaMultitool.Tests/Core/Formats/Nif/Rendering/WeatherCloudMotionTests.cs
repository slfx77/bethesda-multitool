using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class WeatherCloudMotionTests
{
    [Fact]
    public void Resolve_SemanticAuthoredZero_RemainsStill()
    {
        var weather = new WeatherRecord
        {
            CloudSpeedsX = [0.75f],
            CloudSpeedsY = [-0.5f],
        };
        var layer = new WeatherCloudLayer { SourceIndex = 0, SpeedU = 0f, SpeedV = 0f };

        Assert.Equal(Vector2.Zero, WeatherCloudMotion.Resolve(weather, layer, 0));
    }

    [Fact]
    public void Resolve_SemanticSparseLayer_IsAuthoritativeWhenLegacyArraysDisagree()
    {
        var weather = new WeatherRecord
        {
            CloudSpeedsX = [1f],
            CloudSpeedsY = [1f],
        };
        var layer = new WeatherCloudLayer
        {
            SourceIndex = 15,
            SpeedU = 15f / 127f,
            SpeedV = -13f / 127f,
        };

        var resolved = WeatherCloudMotion.Resolve(weather, layer, 15);

        Assert.Equal((15f / 127f) * 0.01f, resolved.X, 7);
        Assert.Equal((-13f / 127f) * 0.01f, resolved.Y, 7);
    }

    [Fact]
    public void Resolve_ComposesWithSparseWeatherLayerLookup()
    {
        var weather = new WeatherRecord
        {
            CloudLayers =
            [
                new WeatherCloudLayer { SourceIndex = 0, SpeedU = 1f, SpeedV = 1f },
                new WeatherCloudLayer { SourceIndex = 19, SpeedU = 0.25f, SpeedV = -0.5f },
            ],
        };

        var layer = weather.FindCloudLayerBySourceIndex(19);
        var resolved = WeatherCloudMotion.Resolve(weather, layer, 19);

        Assert.NotNull(layer);
        Assert.Equal(0.0025f, resolved.X, 7);
        Assert.Equal(-0.005f, resolved.Y, 7);
    }

    [Fact]
    public void Resolve_TexturedLayerWithNoSpeed_RemainsStill()
    {
        var layer = new WeatherCloudLayer
        {
            SourceIndex = 15,
            Texture = @"textures\sky\clouds.dds",
        };

        Assert.Equal(Vector2.Zero, WeatherCloudMotion.Resolve(new WeatherRecord(), layer, 15));
    }

    [Fact]
    public void Resolve_LegacyProjection_UsesBothAuthoredAxesAtRecoveredScale()
    {
        var weather = new WeatherRecord
        {
            CloudSpeedsX = [0f, 0.5f],
            CloudSpeedsY = [0f, -0.25f],
        };

        var resolved = WeatherCloudMotion.Resolve(weather, semanticLayer: null, sourceLayerIndex: 1);

        Assert.Equal(0.005f, resolved.X, 6);
        Assert.Equal(-0.0025f, resolved.Y, 6);
    }

    [Fact]
    public void Resolve_MissingWeatherOrOutOfRangeLayer_RemainsStill()
    {
        Assert.Equal(Vector2.Zero, WeatherCloudMotion.Resolve(null, null, 0));
        Assert.Equal(Vector2.Zero, WeatherCloudMotion.Resolve(new WeatherRecord(), null, 4));
        Assert.Equal(Vector2.Zero, WeatherCloudMotion.Resolve(new WeatherRecord(), null, -1));
    }
}
