using BethesdaMultitool.Core.Media;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Media;

/// <summary>
///     Parsing of ffmpeg's stderr status line. The sample lines are the shapes ffmpeg actually
///     emits, and every expected value is computed by hand rather than from the parser.
/// </summary>
public class FfmpegProgressParserTests
{
    /// <summary>A real status line: 1 minute 2.5 seconds in.</summary>
    [Fact]
    public void TryReadElapsedSeconds_ReadsAStatusLine()
    {
        const string line =
            "frame=  150 fps= 30 q=28.0 size=    1024kB time=00:01:02.50 bitrate= 134.2kbits/s speed=1.2x";

        Assert.Equal(62.5, FfmpegProgressParser.TryReadElapsedSeconds(line));
    }

    [Fact]
    public void TryReadElapsedSeconds_ReadsHoursMinutesAndSeconds()
    {
        Assert.Equal(2 * 3600 + 3 * 60 + 4.25, FfmpegProgressParser.TryReadElapsedSeconds("time=02:03:04.25"));
    }

    /// <summary>
    ///     The fractional field is not always two digits, so it has to be scaled by its own width.
    ///     Reading ".5" as hundredths would report 0.05s instead of 0.5s.
    /// </summary>
    [Theory]
    [InlineData("time=00:00:01.5", 1.5)]
    [InlineData("time=00:00:01.50", 1.5)]
    [InlineData("time=00:00:01.500", 1.5)]
    [InlineData("time=00:00:01.05", 1.05)]
    public void TryReadElapsedSeconds_ScalesTheFractionByItsWidth(string line, double expected)
    {
        Assert.Equal(expected, FfmpegProgressParser.TryReadElapsedSeconds(line));
    }

    /// <summary>Long encodes push the hour field past two digits.</summary>
    [Fact]
    public void TryReadElapsedSeconds_AcceptsMoreThanTwoHourDigits()
    {
        Assert.Equal(100 * 3600d, FfmpegProgressParser.TryReadElapsedSeconds("time=100:00:00.00"));
    }

    /// <summary>Most stderr lines carry no time marker at all and must not be misread.</summary>
    [Theory]
    [InlineData("ffmpeg version 6.1.1 Copyright (c) 2000-2023 the FFmpeg developers")]
    [InlineData("  Stream #0:0: Video: bink (BIKi / 0x694B4942), yuv420p, 640x480")]
    [InlineData("[bink @ 000001] Unsupported feature")]
    [InlineData("")]
    [InlineData(null)]
    public void TryReadElapsedSeconds_ReturnsNullWithoutATimeMarker(string? line)
    {
        Assert.Null(FfmpegProgressParser.TryReadElapsedSeconds(line));
    }

    [Fact]
    public void ToFraction_DividesByTheTotal()
    {
        Assert.Equal(0.25, FfmpegProgressParser.ToFraction(15, 60));
    }

    /// <summary>
    ///     ffmpeg's reported time can run slightly past a container's declared duration, which
    ///     would drive a progress bar above full.
    /// </summary>
    [Fact]
    public void ToFraction_ClampsToOne()
    {
        Assert.Equal(1d, FfmpegProgressParser.ToFraction(61, 60));
    }

    [Fact]
    public void ToFraction_ClampsToZero()
    {
        Assert.Equal(0d, FfmpegProgressParser.ToFraction(-5, 60));
    }

    /// <summary>An unknown duration reports nothing rather than dividing by zero.</summary>
    [Theory]
    [InlineData(0d)]
    [InlineData(-1d)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void ToFraction_ReturnsNullForAnUnusableTotal(double total)
    {
        Assert.Null(FfmpegProgressParser.ToFraction(10, total));
    }
}