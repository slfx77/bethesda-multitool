using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Games;
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

    [Theory]
    [InlineData(BethesdaGame.Oblivion)]
    [InlineData(BethesdaGame.Fallout3)]
    [InlineData(BethesdaGame.FalloutNewVegas)]
    public void Resolve_LegacyScalarUsesUnsignedSpeedAndAuthoredWind(BethesdaGame game)
    {
        // FNV MemDebug Clouds::Update: (ONAM byte / 255) * fWeatherCloudSpeedMax(.1)
        // * Sky.fWindSpeed * dt. Byte 51 is exactly .2 for both speed and wind.
        var weather = new WeatherRecord
        {
            Data = new WeatherData { WindSpeed = 51 },
            CloudLayers =
            [
                new WeatherCloudLayer
                {
                    SourceIndex = 0,
                    SpeedU = 51f / 255f,
                },
            ],
        };

        var resolved = WeatherCloudMotion.Resolve(
            weather, weather.CloudLayers[0], sourceLayerIndex: 0, game: game);

        Assert.Equal(0.004f, resolved.X, 7);
        Assert.Equal(0f, resolved.Y);
    }

    [Fact]
    public void Resolve_MissingWeatherOrOutOfRangeLayer_RemainsStill()
    {
        Assert.Equal(Vector2.Zero, WeatherCloudMotion.Resolve(null, null, 0));
        Assert.Equal(Vector2.Zero, WeatherCloudMotion.Resolve(new WeatherRecord(), null, 4));
        Assert.Equal(Vector2.Zero, WeatherCloudMotion.Resolve(new WeatherRecord(), null, -1));
    }
}
