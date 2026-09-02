using System;
using System.Linq;
using System.Text;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Vectors for <see cref="ArenaCityDataFile" />. The layout is entirely fixed-size, so these
///     pin the offsets and the positional slot-to-kind mapping. Shapes follow the retail table
///     (surveyed 2026-09-01: 11,052 bytes, nine provinces, 34 named locations each except the
///     Imperial Province's one).
/// </summary>
public class ArenaCityDataFileTests
{
    private static void WriteName(byte[] file, int offset, string name)
    {
        Encoding.Latin1.GetBytes(name).CopyTo(file, offset);
    }

    private static void WriteU16(byte[] file, int offset, int value)
    {
        file[offset] = (byte)(value & 0xFF);
        file[offset + 1] = (byte)((value >> 8) & 0xFF);
    }

    private static int LocationOffset(int province, int slot)
    {
        return (province * ArenaCityDataFile.ProvinceRecordLength)
               + ArenaCityDataFile.NameLength + 8
               + (slot * ArenaCityDataFile.LocationRecordLength);
    }

    private static byte[] BuildTable()
    {
        var file = new byte[ArenaCityDataFile.FileLength];
        for (var p = 0; p < ArenaCityDataFile.ProvinceCount; p++)
        {
            var baseOffset = p * ArenaCityDataFile.ProvinceRecordLength;
            WriteName(file, baseOffset, $"Province{p}");
            WriteU16(file, baseOffset + ArenaCityDataFile.NameLength, 10 + p);
            WriteU16(file, baseOffset + ArenaCityDataFile.NameLength + 2, 20 + p);
            WriteU16(file, baseOffset + ArenaCityDataFile.NameLength + 4, 30 + p);
            WriteU16(file, baseOffset + ArenaCityDataFile.NameLength + 6, 40 + p);
        }

        return file;
    }

    [Fact]
    public void Constants_MatchTheFixedLayout()
    {
        Assert.Equal(9, ArenaCityDataFile.ProvinceCount);
        Assert.Equal(48, ArenaCityDataFile.LocationsPerProvince);
        Assert.Equal(25, ArenaCityDataFile.LocationRecordLength);

        // 20-byte name + four u16s + 48 x 25 = 1228.
        Assert.Equal(1228, ArenaCityDataFile.ProvinceRecordLength);
        Assert.Equal(
            ArenaCityDataFile.NameLength + 8
            + (ArenaCityDataFile.LocationsPerProvince * ArenaCityDataFile.LocationRecordLength),
            ArenaCityDataFile.ProvinceRecordLength);

        // The retail file's exact size.
        Assert.Equal(11052, ArenaCityDataFile.FileLength);
    }

    [Fact]
    public void Parse_WrongLength_Throws()
    {
        Assert.Throws<InvalidDataException>(
            () => ArenaCityDataFile.Parse(new byte[ArenaCityDataFile.FileLength - 1], "BAD"));
        Assert.Throws<InvalidDataException>(
            () => ArenaCityDataFile.Parse(new byte[ArenaCityDataFile.FileLength + 1], "BAD"));
    }

    [Fact]
    public void Parse_ReadsEachProvinceHeader()
    {
        var table = ArenaCityDataFile.Parse(BuildTable(), "CITYDATA.00");

        Assert.Equal(ArenaCityDataFile.ProvinceCount, table.Provinces.Count);

        var third = table.Provinces[2];
        Assert.Equal("Province2", third.Name);
        Assert.Equal(2, third.Index);
        Assert.Equal(12, third.GlobalX);
        Assert.Equal(22, third.GlobalY);
        Assert.Equal(32, third.GlobalWidth);
        Assert.Equal(42, third.GlobalHeight);
    }

    [Fact]
    public void Parse_ReadsLocationNamePositionAndVisibility()
    {
        var file = BuildTable();
        var offset = LocationOffset(1, 5);
        WriteName(file, offset, "Sentinel");
        WriteU16(file, offset + ArenaCityDataFile.NameLength, 46);
        WriteU16(file, offset + ArenaCityDataFile.NameLength + 2, 78);
        file[offset + ArenaCityDataFile.NameLength + 4] = ArenaLocation.VisibleFlag;

        var location = ArenaCityDataFile.Parse(file, "CITYDATA.00").Provinces[1].Locations[5];

        Assert.Equal("Sentinel", location.Name);
        Assert.Equal(46, location.X);
        Assert.Equal(78, location.Y);
        Assert.True(location.IsVisible);
        Assert.True(location.IsNamed);
    }

    [Fact]
    public void Parse_UnnamedSlot_IsNotCountedAsNamed()
    {
        var table = ArenaCityDataFile.Parse(BuildTable(), "CITYDATA.00");

        Assert.All(table.Provinces, p => Assert.Empty(p.NamedLocations));
        Assert.Empty(table.NamedLocations);
    }

    // The expected kind travels as an int because ArenaLocationKind is internal and a public
    // theory parameter of an internal type is a CS0051 accessibility error.
    [Theory]
    // Slots are positional: 8 city-states, 8 towns, 16 villages, then the two main-quest
    // dungeons (staff dungeon FIRST), then 14 random-dungeon slots.
    [InlineData(0, (int)ArenaLocationKind.CityState)]
    [InlineData(7, (int)ArenaLocationKind.CityState)]
    [InlineData(8, (int)ArenaLocationKind.Town)]
    [InlineData(15, (int)ArenaLocationKind.Town)]
    [InlineData(16, (int)ArenaLocationKind.Village)]
    [InlineData(31, (int)ArenaLocationKind.Village)]
    [InlineData(32, (int)ArenaLocationKind.StaffDungeon)]
    [InlineData(33, (int)ArenaLocationKind.StaffMapDungeon)]
    [InlineData(34, (int)ArenaLocationKind.RandomDungeon)]
    [InlineData(47, (int)ArenaLocationKind.RandomDungeon)]
    public void KindOfSlot_MapsThePositionalLayout(int slot, int expected)
    {
        Assert.Equal((ArenaLocationKind)expected, ArenaCityDataFile.KindOfSlot(slot));
    }

    [Fact]
    public void KindOfSlot_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ArenaCityDataFile.KindOfSlot(ArenaCityDataFile.LocationsPerProvince));
    }

    [Fact]
    public void KindCounts_MatchTheDocumentedProvinceComposition()
    {
        var kinds = Enumerable.Range(0, ArenaCityDataFile.LocationsPerProvince)
            .Select(ArenaCityDataFile.KindOfSlot)
            .GroupBy(k => k)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(8, kinds[ArenaLocationKind.CityState]);
        Assert.Equal(8, kinds[ArenaLocationKind.Town]);
        Assert.Equal(16, kinds[ArenaLocationKind.Village]);
        Assert.Equal(1, kinds[ArenaLocationKind.StaffDungeon]);
        Assert.Equal(1, kinds[ArenaLocationKind.StaffMapDungeon]);
        Assert.Equal(14, kinds[ArenaLocationKind.RandomDungeon]);
    }

    [Fact]
    public void Parse_NameStopsAtItsTerminator()
    {
        var file = BuildTable();
        var offset = LocationOffset(0, 0);
        WriteName(file, offset, "Daggerfall");

        // Junk past the terminator must not leak into the name.
        file[offset + 11] = (byte)'X';

        Assert.Equal("Daggerfall", ArenaCityDataFile.Parse(file, "CITYDATA.00").Provinces[0].Locations[0].Name);
    }

    [Fact]
    public void Parse_VisibilityBit_IsCheckedNotComparedWhole()
    {
        var file = BuildTable();
        var offset = LocationOffset(0, 32);
        WriteName(file, offset, "Dungeon");

        // Other bits set alongside the visible flag must still read as visible.
        file[offset + ArenaCityDataFile.NameLength + 4] = ArenaLocation.VisibleFlag | 0x40;

        Assert.True(ArenaCityDataFile.Parse(file, "CITYDATA.00").Provinces[0].Locations[32].IsVisible);
    }
}
