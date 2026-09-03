using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     MAPS.BSA parsing on synthetic regions: the packed map-table bits, the exterior record walk,
///     dungeon attachment by exterior location id, the fixed 32-slot block table, and the
///     authored-empty region shape.
/// </summary>
public class DaggerfallMapsFileTests
{
    private static DaggerfallRegion Parse(int regionIndex, params DaggerfallMapsFixture.Location[] locations)
    {
        return DaggerfallMapsFile.ParseRegion(
            regionIndex,
            DaggerfallMapsFixture.Names(locations),
            DaggerfallMapsFixture.Table(locations),
            DaggerfallMapsFixture.Exteriors(locations),
            DaggerfallMapsFixture.Dungeons(locations));
    }

    [Fact]
    public void ParseRegion_UnpacksTheBitPackedMapTable()
    {
        // Longitude 78504 / latitude 15016 are the retail values of region 0's first location.
        var region = Parse(0,
            DaggerfallMapsFixture.Make("The Hawkston Cemetery", 78504, 15016, locationType: 12, discovered: false, dungeonType: 18, key: 0xDEADBEEF),
            DaggerfallMapsFixture.Make("Daggerfall", 26504, 36664, locationType: 0, locationId: 50026, discovered: true, dungeonType: 2));

        Assert.Equal("Alik'r Desert", region.Name);
        Assert.Equal(2, region.Locations.Count);

        var cemetery = region.Locations[0];
        Assert.Equal("The Hawkston Cemetery", cemetery.Name);
        Assert.Equal(78504, cemetery.Longitude);
        Assert.Equal(15016, cemetery.Latitude);
        Assert.Equal(613, cemetery.MapPixelX);
        Assert.Equal(382, cemetery.MapPixelY);
        Assert.Equal(382 * 1000 + 613, cemetery.WorldPixelId);
        Assert.Equal(DaggerfallLocationType.Graveyard, cemetery.LocationType);
        Assert.Equal(DaggerfallDungeonType.Cemetery, cemetery.DungeonType);
        Assert.False(cemetery.Discovered);
        Assert.Equal(0xDEADBEEFu, cemetery.Key);

        var city = region.Locations[1];
        Assert.Equal(207, city.MapPixelX);
        Assert.Equal(213, city.MapPixelY);
        Assert.Equal(DaggerfallLocationType.TownCity, city.LocationType);
        Assert.Equal(DaggerfallDungeonType.HumanStronghold, city.DungeonType);
        Assert.True(city.Discovered);
        Assert.Equal(50026, city.LocationId);
        Assert.Null(city.Dungeon);
    }

    [Fact]
    public void ParseRegion_ReadsExteriorGeometryBuildingsAndTheHeaderLocationId()
    {
        var region = Parse(17,
            DaggerfallMapsFixture.Make("Wayrest", 90000, 30000, locationId: 4242, width: 8, height: 7, portByte: 0x10, doors: 3,
                buildings:
                [
                    new DaggerfallMapsFixture.Building(0x0F, 9, 510),
                    new DaggerfallMapsFixture.Building(0x0F, 3, 511),
                    new DaggerfallMapsFixture.Building(0x0E, 20, 82)
                ]));

        var city = region.Locations[0];
        Assert.Equal(4242, city.LocationId);
        Assert.Equal(90000 * 256, city.WorldX);
        Assert.Equal(30000 * 256, city.WorldY);
        Assert.Equal(3, city.DoorCount);
        Assert.Equal(8, city.BlocksWide);
        Assert.Equal(7, city.BlocksHigh);
        Assert.Equal(0x10, city.PortTownAndUnknown);

        Assert.Equal(3, city.Buildings.Count);
        Assert.Equal(DaggerfallBuildingType.Tavern, city.Buildings[0].BuildingType);
        Assert.Equal(9, city.Buildings[0].Quality);
        Assert.Equal(510, city.Buildings[0].FactionId);
        Assert.Equal(DaggerfallBuildingType.Temple, city.Buildings[2].BuildingType);
        Assert.Equal(82, city.Buildings[2].FactionId);

        // The three 64-entry block tables are carried intact for the RMB milestone.
        Assert.Equal(64, city.BlockIndices.Length);
        Assert.Equal(5, city.BlockIndices.Span[5]);
        Assert.Equal(10, city.BlockNumbers.Span[5]);
        Assert.Equal((byte)'F', city.BlockCharacters.Span[5]);
    }

    [Fact]
    public void ParseRegion_AttachesDungeonsByExteriorLocationId_AndReadsOnlyTheUsedSlots()
    {
        var blocks = new List<DaggerfallMapsFixture.DungeonBlock>
        {
            new(0, 0, 0x123, 1, true),
            new(-2, 3, 0x3FF, 31, false),
            new(5, -5, 7, 0, false)
        };

        var region = Parse(16,
            DaggerfallMapsFixture.Make("Plain Farm", 1000, 1000, locationType: 3, locationId: 10),
            DaggerfallMapsFixture.Make("Castle Wroth", 2000, 2000, locationType: 7, locationId: 20, dungeonType: 2, dungeon: blocks, dungeonDoors: 4));

        Assert.Null(region.Locations[0].Dungeon);

        var dungeon = region.Locations[1].Dungeon;
        Assert.NotNull(dungeon);
        Assert.Equal("Castle Wroth", dungeon.Name);
        Assert.Equal(20 ^ 0x8000, dungeon.LocationId);
        Assert.Equal(4, dungeon.DoorCount);
        Assert.Equal(3, dungeon.Blocks.Count);

        Assert.Equal(new DaggerfallDungeonBlock(0, 0, 0x123, 1, true), dungeon.Blocks[0]);
        Assert.Equal(new DaggerfallDungeonBlock(-2, 3, 0x3FF, 31, false), dungeon.Blocks[1]);
        Assert.Equal(new DaggerfallDungeonBlock(5, -5, 7, 0, false), dungeon.Blocks[2]);
    }

    [Fact]
    public void ParseRegion_EmptyRegion_HasNoLocations()
    {
        // Retail's 17 empty regions: nothing at all except a 4-byte zero dungeon count.
        var region = DaggerfallMapsFile.ParseRegion(2, [], [], [], new byte[4]);

        Assert.Equal("Glenpoint Foothills", region.Name);
        Assert.Empty(region.Locations);

        // ...and a fully absent dungeon entry is tolerated too.
        Assert.Empty(DaggerfallMapsFile.ParseRegion(3, [], [], [], []).Locations);
    }

    [Fact]
    public void ParseRegion_RejectsATableThatDoesNotTileTheNameCount()
    {
        var locations = new[] { DaggerfallMapsFixture.Make("A", 128, 128), DaggerfallMapsFixture.Make("B", 256, 256) };
        var table = DaggerfallMapsFixture.Table(locations);

        var error = Assert.Throws<InvalidDataException>(() => DaggerfallMapsFile.ParseRegion(
            0, DaggerfallMapsFixture.Names(locations), table.AsSpan(0, 17), DaggerfallMapsFixture.Exteriors(locations), new byte[4]));

        Assert.Contains("MAPTABLE", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRegion_RejectsADungeonRecordShorterThanTheFixedSlotTable()
    {
        var locations = new[]
        {
            DaggerfallMapsFixture.Make("Crypt", 128, 128, locationId: 9, dungeonType: 0,
                dungeon: [new DaggerfallMapsFixture.DungeonBlock(0, 0, 1, 0, true)])
        };
        var dungeons = DaggerfallMapsFixture.Dungeons(locations);

        // Drop the last unused slot: the reference would not notice, the fixed-size rule does.
        var error = Assert.Throws<InvalidDataException>(() => DaggerfallMapsFile.ParseRegion(
            0, DaggerfallMapsFixture.Names(locations), DaggerfallMapsFixture.Table(locations),
            DaggerfallMapsFixture.Exteriors(locations), dungeons.AsSpan(0, dungeons.Length - 4)));

        Assert.Contains("32-slot", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseRegion_RejectsAnExteriorWhoseMapIdDisagreesWithTheTable()
    {
        var locations = new[] { DaggerfallMapsFixture.Make("A", 128, 128) };
        var exteriors = DaggerfallMapsFixture.Exteriors(locations);

        // The exterior map id sits 32 bytes into the 416-byte exterior data, which is the record's
        // tail: offset table (4) + doors (4) + header (112) + building count/padding (7) + 32.
        exteriors[4 + 4 + 112 + 7 + 32] ^= 0x01;

        Assert.Throws<InvalidDataException>(() => DaggerfallMapsFile.ParseRegion(
            0, DaggerfallMapsFixture.Names(locations), DaggerfallMapsFixture.Table(locations), exteriors, new byte[4]));
    }

    [Fact]
    public void RegionNames_HasSixtyTwoEntries_WithDaggerfallAtSeventeen()
    {
        Assert.Equal(62, DaggerfallMapsFile.RegionNames.Count);
        Assert.Equal(DaggerfallMapsFile.RegionCount, DaggerfallMapsFile.RegionNames.Count);
        Assert.Equal("Daggerfall", DaggerfallMapsFile.RegionNames[17]);
        Assert.Equal("Wrothgarian Mountains", DaggerfallMapsFile.RegionNames[16]);
        Assert.Equal("Cybiades", DaggerfallMapsFile.RegionNames[61]);
        Assert.Equal(62, DaggerfallMapsFile.RegionNames.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Open_ReadsAllSixtyTwoRegionsFromAnXnGineArchive()
    {
        var regions = new Dictionary<int, IReadOnlyList<DaggerfallMapsFixture.Location>>
        {
            [0] = [DaggerfallMapsFixture.Make("Alpha", 1280, 1280, locationId: 1)],
            [17] =
            [
                DaggerfallMapsFixture.Make("Daggerfall", 26504, 36664, locationId: 50026, width: 8, height: 8, dungeonType: 2,
                    dungeon: [new DaggerfallMapsFixture.DungeonBlock(0, 0, 5, 0, true)]),
                DaggerfallMapsFixture.Make("Betony Farm", 26600, 36000, locationType: 3, locationId: 7)
            ]
        };

        var directory = Path.Combine(Path.GetTempPath(), "bmt-maps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "MAPS.BSA");
            File.WriteAllBytes(path, DaggerfallMapsFixture.Archive(regions));

            var maps = DaggerfallMapsFile.Open(path);

            Assert.Equal(62, maps.Regions.Count);
            Assert.Equal(3, maps.LocationCount);
            Assert.Equal(60, maps.Regions.Count(r => r.Locations.Count == 0));
            Assert.Equal("Alpha", maps.Regions[0].Locations[0].Name);
            Assert.Equal("Daggerfall", maps.Regions[17].Locations[0].Name);
            Assert.NotNull(maps.Regions[17].Locations[0].Dungeon);
            Assert.Null(maps.Regions[17].Locations[1].Dungeon);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void FromEntries_RequiresEveryEntry()
    {
        var error = Assert.Throws<InvalidDataException>(() => DaggerfallMapsFile.FromEntries(
            name => name == "MAPDITEM.005" ? null : []));

        Assert.Contains("MAPDITEM.005", error.Message, StringComparison.Ordinal);
    }
}
