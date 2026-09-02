using BethesdaMultitool.Core.Imaging;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Imaging;

/// <summary>
///     Tests for <see cref="ColorCycling" />: the pinned Fallout PAL range table and the pure
///     index-rotation helper (identity outside ranges, forward rotation with wraparound inside).
/// </summary>
public class ColorCyclingTests
{
    [Fact]
    public void FalloutRanges_PinnedTable()
    {
        Assert.Equal(6, ColorCycling.FalloutRanges.Count);

        Assert.Equal(new ColorCycleRange("Slime", 229, 232, 200), ColorCycling.Slime);
        Assert.Equal(new ColorCycleRange("Monitors", 233, 237, 100), ColorCycling.Monitors);
        Assert.Equal(new ColorCycleRange("FireSlow", 238, 242, 200), ColorCycling.FireSlow);
        Assert.Equal(new ColorCycleRange("FireFast", 243, 247, 142), ColorCycling.FireFast);
        Assert.Equal(new ColorCycleRange("Shoreline", 248, 253, 200), ColorCycling.Shoreline);
        Assert.Equal(new ColorCycleRange("Alarm", 254, 254, 33), ColorCycling.Alarm);
    }

    [Fact]
    public void FalloutRanges_AreContiguousFrom229To254()
    {
        var expectedStart = 229;
        foreach (var range in ColorCycling.FalloutRanges)
        {
            Assert.Equal(expectedStart, range.Start);
            Assert.True(range.End >= range.Start);
            expectedStart = range.End + 1;
        }

        Assert.Equal(255, expectedStart);
    }

    [Theory]
    [InlineData(0, 5)]
    [InlineData(100, 1)]
    [InlineData(228, 3)] // last static index before the animated block
    [InlineData(255, 99)] // interface colour after the animated block
    public void CycleIndex_IdentityOutsideAnimatedRanges(int index, int tick)
    {
        Assert.Equal(index, ColorCycling.CycleIndex(index, tick));
    }

    [Fact]
    public void CycleIndex_TickZero_IsIdentityEverywhere()
    {
        for (var index = 0; index < 256; index++)
        {
            Assert.Equal(index, ColorCycling.CycleIndex(index, 0));
        }
    }

    [Theory]
    [InlineData(229, 1, 230)] // slime, one step forward
    [InlineData(232, 1, 229)] // slime, wraps end -> start
    [InlineData(229, 4, 229)] // slime, full 4-step cycle
    [InlineData(230, 7, 229)] // slime, (230-229+7) % 4 = 0
    [InlineData(233, 2, 235)] // monitors
    [InlineData(237, 3, 235)] // monitors, (237-233+3) % 5 = 2
    [InlineData(253, 1, 248)] // shoreline, wraps
    [InlineData(248, 6, 248)] // shoreline, full 6-step cycle
    [InlineData(238, 5, 238)] // fire-slow, full 5-step cycle
    [InlineData(247, 2, 244)] // fire-fast, (247-243+2) % 5 = 1
    public void CycleIndex_RotatesForwardWithWraparound(int index, int tick, int expected)
    {
        Assert.Equal(expected, ColorCycling.CycleIndex(index, tick));
    }

    [Theory]
    [InlineData(229, -1, 232)] // one step backward wraps start -> end
    [InlineData(231, -6, 229)] // (231-229-6) % 4 = -4 -> 0
    public void CycleIndex_NegativeTick_RotatesBackward(int index, int tick, int expected)
    {
        Assert.Equal(expected, ColorCycling.CycleIndex(index, tick));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(12345)]
    [InlineData(-3)]
    public void CycleIndex_AlarmSingleEntryRange_StaysFixed(int tick)
    {
        Assert.Equal(254, ColorCycling.CycleIndex(254, tick));
    }
}
