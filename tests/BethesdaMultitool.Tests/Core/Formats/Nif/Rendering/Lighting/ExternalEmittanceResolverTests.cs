using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Atmosphere;
using BethesdaMultitool.Core.Formats.Nif.Rendering.Lighting;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering.Lighting;

public sealed class ExternalEmittanceResolverTests
{
    [Fact]
    public void BuildIndex_UsesRuntimeRegionAndLittleEndianPackedLightRgb()
    {
        var index = ExternalEmittanceResolver.BuildIndex(
            [
                new RegionRecord
                {
                    FormId = 0x100,
                    EmittanceColorR = 255,
                    EmittanceColorG = 128,
                    EmittanceColorB = 0,
                    IsRuntimeOnly = true
                }
            ],
            [new LightRecord { FormId = 0x200, Color = 0x7f3f1fu }],
            new Dictionary<uint, WeatherRecord>());

        Assert.Equal(new Vector3(1f, 128f / 255f, 0f), Resolve(index[0x100]));
        Assert.Equal(new Vector3(31f / 255f, 63f / 255f, 127f / 255f), Resolve(index[0x200]));
        Assert.True(TryResolve(index[0x100], BethesdaGame.Skyrim, out _));
        Assert.True(TryResolve(index[0x200], BethesdaGame.Skyrim, out _));
    }

    [Fact]
    public void BuildIndex_PluginRegionUsesLinkedWeatherEffectLightingByTime()
    {
        const uint weatherFormId = 0x300;
        var colors = Enumerable.Repeat(Solid(0, 0, 0), 10).ToArray();
        colors[(int)WeatherColorType.EffectLighting] = new WeatherColor(
            new WeatherRgba(128, 95, 49, 0),
            new WeatherRgba(206, 252, 255, 0),
            new WeatherRgba(101, 71, 39, 0),
            new WeatherRgba(41, 63, 75, 0));
        var weather = new WeatherRecord { FormId = weatherFormId, Colors = colors };

        var index = ExternalEmittanceResolver.BuildIndex(
            [
                new RegionRecord
                {
                    FormId = 0x100,
                    EmittanceColorR = 53,
                    EmittanceColorG = 210,
                    EmittanceColorB = 0,
                    WeatherTypes = [new RegionWeatherType(weatherFormId, 100, 0)]
                }
            ],
            [],
            new Dictionary<uint, WeatherRecord> { [weatherFormId] = weather });

        Assert.True(index[0x100].IsWeatherDriven);
        Assert.Equal(new Vector3(206f / 255f, 252f / 255f, 1f), Resolve(index[0x100], 12f));
        Assert.Equal(new Vector3(41f / 255f, 63f / 255f, 75f / 255f), Resolve(index[0x100], 23f));
    }

    [Theory]
    [InlineData(false, 100u, 1u)]
    [InlineData(false, 50u, 0u)]
    public void BuildIndex_PluginRegionWithoutOneResolvableDeterministicWeatherIsNeutral(
        bool includeWeather,
        uint chance,
        uint globalFormId)
    {
        const uint weatherFormId = 0x300;
        var weathers = includeWeather
            ? new Dictionary<uint, WeatherRecord> { [weatherFormId] = new() { FormId = weatherFormId } }
            : new Dictionary<uint, WeatherRecord>();
        var index = ExternalEmittanceResolver.BuildIndex(
            [
                new RegionRecord
                {
                    FormId = 0x100,
                    EmittanceColorR = 53,
                    EmittanceColorG = 210,
                    EmittanceColorB = 0,
                    WeatherTypes = [new RegionWeatherType(weatherFormId, chance, globalFormId)]
                }
            ],
            [],
            weathers);

        Assert.False(index[0x100].IsWeatherDriven);
        Assert.Equal(Vector3.One, Resolve(index[0x100]));
        Assert.False(TryResolve(index[0x100], BethesdaGame.Skyrim, out _));
    }

    [Fact]
    public void BuildIndex_PluginRegionWithMultipleDeterministicWeathersIsNeutral()
    {
        var weathers = new Dictionary<uint, WeatherRecord>
        {
            [0x300] = new() { FormId = 0x300 },
            [0x301] = new() { FormId = 0x301 }
        };
        var index = ExternalEmittanceResolver.BuildIndex(
            [
                new RegionRecord
                {
                    FormId = 0x100,
                    WeatherTypes =
                    [
                        new RegionWeatherType(0x300, 100, 0),
                        new RegionWeatherType(0x301, 100, 0)
                    ]
                }
            ],
            [],
            weathers);

        Assert.False(index[0x100].IsWeatherDriven);
        Assert.Equal(Vector3.One, Resolve(index[0x100]));
        Assert.False(TryResolve(index[0x100], BethesdaGame.Skyrim, out _));
    }

    [Fact]
    public void Resolve_WeatherWithoutEffectLightingRowIsNeutral()
    {
        const uint weatherFormId = 0x300;
        var weather = new WeatherRecord { FormId = weatherFormId };
        var index = ExternalEmittanceResolver.BuildIndex(
            [
                new RegionRecord
                {
                    FormId = 0x100,
                    WeatherTypes = [new RegionWeatherType(weatherFormId, 100, 0)]
                }
            ],
            [],
            new Dictionary<uint, WeatherRecord> { [weatherFormId] = weather });

        Assert.True(index[0x100].IsWeatherDriven);
        Assert.Equal(Vector3.One, Resolve(index[0x100]));
        Assert.False(TryResolve(index[0x100], BethesdaGame.Skyrim, out _));
    }

    [Fact]
    public void Resolve_WeatherEffectLightingIsNotAppliedToLegacyUnusedRowNine()
    {
        var colors = Enumerable.Repeat(Solid(0, 0, 0), 10).ToArray();
        colors[(int)WeatherColorType.EffectLighting] = Solid(12, 200, 32);
        var source = ExternalEmittanceSource.FromWeather(new WeatherRecord { Colors = colors });

        foreach (var game in new[]
                 {
                     BethesdaGame.Unknown,
                     BethesdaGame.Morrowind,
                     BethesdaGame.Oblivion,
                     BethesdaGame.Fallout3,
                     BethesdaGame.FalloutNewVegas
                 })
        {
            Assert.False(TryResolve(source, game, out var color));
            Assert.Equal(Vector3.One, color);
        }
    }

    [Fact]
    public void Modulation_UsesIndependentClampedLightingInfluence()
    {
        var color = new Vector3(0.2f, 0.4f, 0.8f);

        Assert.Equal(Vector3.One, ExternalEmittanceResolver.Modulation(color, 0f));
        Assert.Equal(color, ExternalEmittanceResolver.Modulation(color, 1f));
        Assert.Equal(new Vector3(0.6f, 0.7f, 0.9f),
            ExternalEmittanceResolver.Modulation(color, 0.5f));
        Assert.Equal(color, ExternalEmittanceResolver.Modulation(color, 2f));
    }

    private static Vector3 Resolve(ExternalEmittanceSource source, float hour = 12f)
    {
        return source.Resolve(hour, AtmosphereState.ClimateTiming.Default, BethesdaGame.Skyrim);
    }

    private static bool TryResolve(
        ExternalEmittanceSource source,
        BethesdaGame game,
        out Vector3 color)
    {
        return source.TryResolve(12f, AtmosphereState.ClimateTiming.Default, game, out color);
    }

    private static WeatherColor Solid(byte r, byte g, byte b)
    {
        var color = new WeatherRgba(r, g, b, 0);
        return new WeatherColor(color, color, color, color);
    }
}
