using System.Numerics;
using BethesdaMultitool.Core.Formats.Esm.Models.Records.World;
using BethesdaMultitool.Core.Formats.Nif.Rendering;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Nif.Rendering;

public sealed class RuntimeWeatherAtmosphereBlendTests
{
    [Fact]
    public void Transition_BlendsSampledWeatherColorsFogAndDataSunGlareWithCurrentWeight()
    {
        var outgoing = SolidWeather(new WeatherRgba(0, 0, 255, 17), sunGlare: 0,
            fogNear: 100f, fogFar: 1_000f);
        var current = SolidWeather(new WeatherRgba(255, 0, 0, 231), sunGlare: 255,
            fogNear: 300f, fogFar: 3_000f);

        var from = AtmosphereState.Resolve(12f, outgoing, game: BethesdaGame.FalloutNewVegas);
        var to = AtmosphereState.Resolve(12f, current, game: BethesdaGame.FalloutNewVegas);
        var blended = AtmosphereState.ResolveWeatherTransition(
            12f, current, outgoing, currentWeatherWeight: 0.25f,
            game: BethesdaGame.FalloutNewVegas);

        AssertVector(Vector3.Lerp(from.SkyTopColor, to.SkyTopColor, 0.25f), blended.SkyTopColor);
        AssertVector(Vector3.Lerp(from.AmbientColor, to.AmbientColor, 0.25f), blended.AmbientColor);
        AssertVector(Vector3.Lerp(from.FogColor, to.FogColor, 0.25f), blended.FogColor);
        AssertVector4(Vector4.Lerp(from.SunDiscColor, to.SunDiscColor, 0.25f), blended.SunDiscColor);
        Assert.Equal(150f, blended.FogNear, 5);
        Assert.Equal(1_500f, blended.FogFar, 5);
        Assert.Equal(0.25f, blended.SunGlareIntensity, 6);
    }

    [Fact]
    public void Transition_WithoutOutgoingWeather_IsBitForBitAtomicCurrent()
    {
        var current = SolidWeather(new WeatherRgba(80, 120, 160, 255), sunGlare: 127,
            fogNear: 200f, fogFar: 2_000f);

        var atomic = AtmosphereState.Resolve(8f, current, game: BethesdaGame.FalloutNewVegas);
        var transition = AtmosphereState.ResolveWeatherTransition(
            8f, current, outgoingWeather: null, currentWeatherWeight: 0.01f,
            game: BethesdaGame.FalloutNewVegas);

        Assert.Equal(atomic, transition);
    }

    private static WeatherRecord SolidWeather(
        WeatherRgba color,
        byte sunGlare,
        float fogNear,
        float fogFar)
    {
        var solid = new WeatherColor(color, color, color, color, color, color);
        return new WeatherRecord
        {
            Colors = Enumerable.Repeat(solid, 10).ToArray(),
            FogDistances = [fogNear, fogFar, fogNear, fogFar, 1f, 1f],
            Data = new WeatherData { SunGlare = sunGlare },
        };
    }

    private static void AssertVector(Vector3 expected, Vector3 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
    }

    private static void AssertVector4(Vector4 expected, Vector4 actual)
    {
        Assert.Equal(expected.X, actual.X, 6);
        Assert.Equal(expected.Y, actual.Y, 6);
        Assert.Equal(expected.Z, actual.Z, 6);
        Assert.Equal(expected.W, actual.W, 6);
    }
}
