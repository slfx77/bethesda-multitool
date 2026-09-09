using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     The classic game clock: a u32 minute counter over 60-minute hours, 24-hour days, 30-day
///     months and 12-month years, with minute 0 at 00:00 on 1 Morning Star 3E404.
/// </summary>
public class DaggerfallGameTimeTests
{
    /// <summary>
    ///     The retail SAVE0 vector, pinned as literals: SAVEVARS +0x3C9 holds 524,043 and the game
    ///     shows 22:03 on 4 Morning Star 3E405. Every part is written out rather than recomputed
    ///     from the constants under test, so an arithmetic slip anywhere in
    ///     <see cref="DaggerfallGameTime.FromMinutes" /> fails this.
    /// </summary>
    [Fact]
    public void FromMinutes_ReadsTheRetailSaveVector()
    {
        var time = DaggerfallGameTime.FromMinutes(524_043);

        Assert.Equal(524_043u, time.Minutes);
        Assert.Equal(405, time.Year);
        Assert.Equal(0, time.MonthIndex);
        Assert.Equal("Morning Star", time.MonthName);
        Assert.Equal(4, time.Day);
        Assert.Equal(22, time.Hour);
        Assert.Equal(3, time.Minute);
        Assert.Equal("22:03, 4 Morning Star 3E405", time.ToString());
    }

    /// <summary>A new classic game starts at 13:30 on the same day — 513 minutes before the save.</summary>
    [Fact]
    public void FromMinutes_ReadsTheClassicStartConstant()
    {
        var start = DaggerfallGameTime.FromMinutes(DaggerfallGameTime.ClassicStartMinutes);

        Assert.Equal(523_530u, start.Minutes);
        Assert.Equal("13:30, 4 Morning Star 3E405", start.ToString());
        Assert.Equal(513u, 524_043 - DaggerfallGameTime.ClassicStartMinutes);
    }

    /// <summary>Minute 0 is the epoch itself: midnight on the first day of 3E404.</summary>
    [Fact]
    public void FromMinutes_PutsZeroAtTheEpoch()
    {
        var epoch = DaggerfallGameTime.FromMinutes(0);

        Assert.Equal(404, epoch.Year);
        Assert.Equal(1, epoch.Day);
        Assert.Equal(0, epoch.MonthIndex);
        Assert.Equal("00:00, 1 Morning Star 3E404", epoch.ToString());
    }

    /// <summary>
    ///     Month, day and hour boundaries, each a literal minute count worked out by hand:
    ///     1,439 is the last minute of day 1; 1,440 rolls to day 2; 43,200 (30 days) rolls to
    ///     Sun's Dawn; 516,960 is midnight on the year's last day and 518,400 (360 days) rolls
    ///     the year.
    /// </summary>
    [Theory]
    [InlineData(1_439u, 404, 0, 1, 23, 59)]
    [InlineData(1_440u, 404, 0, 2, 0, 0)]
    [InlineData(43_200u, 404, 1, 1, 0, 0)]
    [InlineData(516_960u, 404, 11, 30, 0, 0)]
    [InlineData(518_400u, 405, 0, 1, 0, 0)]
    public void FromMinutes_RollsOnTheCalendarBoundaries(uint minutes, int year, int month, int day, int hour,
        int minute)
    {
        var time = DaggerfallGameTime.FromMinutes(minutes);

        Assert.Equal(year, time.Year);
        Assert.Equal(month, time.MonthIndex);
        Assert.Equal(day, time.Day);
        Assert.Equal(hour, time.Hour);
        Assert.Equal(minute, time.Minute);
    }

    /// <summary>The twelve months, in the order the game lists them.</summary>
    [Fact]
    public void MonthNames_AreTheTwelveClassicMonths()
    {
        Assert.Equal(12, DaggerfallGameTime.MonthNames.Count);
        Assert.Equal("Morning Star", DaggerfallGameTime.MonthNames[0]);
        Assert.Equal("Midyear", DaggerfallGameTime.MonthNames[5]);
        Assert.Equal("Evening Star", DaggerfallGameTime.MonthNames[11]);
    }
}