using BethesdaMultitool.Core.Formats.Esm.Parsing.Handlers;
using BethesdaMultitool.Core.Games;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Esm.Parsing;

public sealed class WeatherWindDataParsingTests
{
    private static readonly byte[] CommonwealthClearData =
    [
        0x78, 0x00, 0x00, 0x19, 0x7f, 0xff, 0x80, 0xff, 0x80, 0xff,
        0x89, 0x01, 0xdb, 0xdc, 0xee, 0x00, 0x00, 0x0f, 0x2b, 0x30
    ];

    [Fact]
    public void Fallout4CommonwealthClear_RetainsExactWindTail()
    {
        var data = MiscEnvironmentHandler.ReadWeatherData(
            CommonwealthClearData,
            BethesdaGame.Fallout4);

        Assert.Equal((byte)120, data.WindSpeed);
        Assert.Equal((byte)15, data.WindDirection);
        Assert.Equal((byte)43, data.WindDirectionRange);
        Assert.Equal((byte)48, data.WindTurbulence);
    }

    [Fact]
    public void Fallout4LegacyPrefix_LeavesAbsentTailNull()
    {
        var data = MiscEnvironmentHandler.ReadWeatherData(
            CommonwealthClearData.AsSpan(0, 15),
            BethesdaGame.Fallout4);

        Assert.Null(data.WindDirection);
        Assert.Null(data.WindDirectionRange);
        Assert.Null(data.WindTurbulence);
    }

    [Theory]
    [InlineData(BethesdaGame.Fallout76)]
    [InlineData(BethesdaGame.Starfield)]
    [InlineData(BethesdaGame.Skyrim)]
    public void UnprovenGames_DoNotInterpretSameSizedTail(BethesdaGame game)
    {
        var data = MiscEnvironmentHandler.ReadWeatherData(CommonwealthClearData, game);

        Assert.Null(data.WindDirection);
        Assert.Null(data.WindDirectionRange);
        Assert.Null(data.WindTurbulence);
    }
}