using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Pins the dungeon block-name mapping measured over all 40,263 retail block references
///     (2026-09-06). The retail oracle itself lives in <c>DaggerfallMapsRetailTests</c>.
/// </summary>
public sealed class DaggerfallDungeonBlockNameTests
{
    [Theory]
    [InlineData(0, 2, "N0000002.RDB")]
    [InlineData(1, 29, "W0000029.RDB")]
    [InlineData(3, 999, "S0000999.RDB")]
    [InlineData(4, 14, "B0000014.RDB")]
    [InlineData(5, 8, "M0000008.RDB")]
    public void Resolve_BuildsTheEntryNameFromTheIndexAndNumber(int index, int number, string expected)
    {
        Assert.Equal(expected, DaggerfallDungeonBlockName.Resolve((byte)index, (ushort)number));
    }

    [Fact]
    public void Letters_KeepTheUnusedLSlotSoTheLaterFamiliesDoNotShift()
    {
        // ⚠ Index 2 is 'L' and retail never uses it — no L*.RDB exists and no dungeon references
        // one. Dropping it as "dead" would shift S, B and M down one and mis-resolve three whole
        // families while still producing plausible-looking names.
        Assert.Equal("NWLSBM", DaggerfallDungeonBlockName.Letters);
        Assert.Equal('L', DaggerfallDungeonBlockName.Letters[2]);
        Assert.Equal("S0000040.RDB", DaggerfallDungeonBlockName.Resolve(3, 40));
        Assert.Equal("B0000000.RDB", DaggerfallDungeonBlockName.Resolve(4, 0));
    }

    [Fact]
    public void Resolve_PadsToSevenDigitsBecauseTheEntryStemIsEightCharacters()
    {
        // All 187 retail stems are exactly 8 characters: one letter plus seven digits.
        var name = DaggerfallDungeonBlockName.Resolve(0, 0);

        Assert.Equal("N0000000.RDB", name);
        Assert.Equal(8, name!.Split('.')[0].Length);
    }

    [Fact]
    public void Resolve_ReturnsNullForAnIndexThatNamesNoFamily()
    {
        Assert.Null(DaggerfallDungeonBlockName.Resolve(6, 0));
        Assert.Null(DaggerfallDungeonBlockName.Resolve(31, 0));
    }
}