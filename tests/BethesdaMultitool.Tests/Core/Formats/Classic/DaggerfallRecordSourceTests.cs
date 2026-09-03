using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Classic;

/// <summary>
///     Record synthesis for the Daggerfall vertical: MAPS.BSA regions and locations become
///     <c>DREG</c>/<c>DLOC</c> generic records with <see cref="ClassicFormIdScheme" /> ids derived
///     from region + table index, never enumeration order.
/// </summary>
public class DaggerfallRecordSourceTests
{
    private static DaggerfallRegion Region(int index, params DaggerfallMapsFixture.Location[] locations)
    {
        return DaggerfallMapsFile.ParseRegion(
            index,
            DaggerfallMapsFixture.Names(locations),
            DaggerfallMapsFixture.Table(locations),
            DaggerfallMapsFixture.Exteriors(locations),
            DaggerfallMapsFixture.Dungeons(locations));
    }

    [Fact]
    public void BuildLocationRecord_CarriesIdentityCoordinatesAndSummaries()
    {
        var region = Region(17,
            DaggerfallMapsFixture.Make("Daggerfall", 26504, 36664, locationId: 50026, width: 8, height: 8, portByte: 0x10, doors: 424,
                dungeonType: 2, key: 31981328,
                buildings:
                [
                    new DaggerfallMapsFixture.Building(0x0F, 9, 1),
                    new DaggerfallMapsFixture.Building(0x0F, 9, 1),
                    new DaggerfallMapsFixture.Building(0x0E, 9, 1),
                    new DaggerfallMapsFixture.Building(0x10, 9, 1)
                ],
                dungeon:
                [
                    new DaggerfallMapsFixture.DungeonBlock(0, 0, 1, 0, true),
                    new DaggerfallMapsFixture.DungeonBlock(1, 0, 2, 0, false)
                ],
                dungeonDoors: 6));

        var record = DaggerfallRecordSource.BuildLocationRecord(region, region.Locations[0]);

        Assert.Equal("DLOC", record.RecordType);
        Assert.Equal("Daggerfall_Daggerfall", record.EditorId);
        Assert.Equal("Daggerfall", record.FullName);
        Assert.Equal(DaggerfallRecordSource.LocationDomain, ClassicFormIdScheme.DomainOf(record.FormId));
        Assert.Equal(17u << 16, ClassicFormIdScheme.IndexOf(record.FormId));

        Assert.Equal("Daggerfall", record.Fields["Region"]);
        Assert.Equal("TownCity", record.Fields["Type"]);
        Assert.Equal(207, record.Fields["MapX"]);
        Assert.Equal(213, record.Fields["MapY"]);
        Assert.Equal(213207, record.Fields["WorldPixelId"]);
        Assert.Equal(50026, record.Fields["LocationId"]);
        Assert.Equal(true, record.Fields["Discovered"]);
        Assert.Equal(31981328u, record.Fields["Key"]);
        Assert.Equal(8, record.Fields["BlocksWide"]);
        Assert.Equal(8, record.Fields["BlocksHigh"]);
        Assert.Equal(424, record.Fields["Doors"]);
        Assert.Equal(4, record.Fields["Buildings"]);
        Assert.Equal("Tavern=2, Palace=1, Temple=1", record.Fields["BuildingTypes"]);
        Assert.Equal("0x10", record.Fields["ExteriorFlags"]);
        Assert.Equal("HumanStronghold", record.Fields["DungeonType"]);
        Assert.Equal(2, record.Fields["DungeonBlocks"]);
        Assert.Equal(6, record.Fields["DungeonDoors"]);
    }

    [Fact]
    public void BuildLocationRecord_OmitsWhatTheLocationDoesNotHave()
    {
        var region = Region(19, DaggerfallMapsFixture.Make("Lonely Hovel", 5000, 6000, locationType: 11, locationId: 3));

        var record = DaggerfallRecordSource.BuildLocationRecord(region, region.Locations[0]);

        Assert.Equal("HomePoor", record.Fields["Type"]);
        Assert.Equal(0, record.Fields["Buildings"]);
        Assert.False(record.Fields.ContainsKey("BuildingTypes"));
        Assert.False(record.Fields.ContainsKey("ExteriorFlags"));
        Assert.False(record.Fields.ContainsKey("DungeonType"));
        Assert.False(record.Fields.ContainsKey("DungeonBlocks"));
    }

    [Fact]
    public void BuildLocationRecord_FormId_IsRegionAndTableIndexOnly()
    {
        var first = Region(5, DaggerfallMapsFixture.Make("A", 1280, 1280), DaggerfallMapsFixture.Make("B", 2560, 2560));
        var second = Region(5, DaggerfallMapsFixture.Make("Renamed", 9000, 9000, width: 4), DaggerfallMapsFixture.Make("B", 2560, 2560));
        var other = Region(6, DaggerfallMapsFixture.Make("A", 1280, 1280));

        var a1 = DaggerfallRecordSource.BuildLocationRecord(first, first.Locations[0]);
        var a2 = DaggerfallRecordSource.BuildLocationRecord(second, second.Locations[0]);
        var b = DaggerfallRecordSource.BuildLocationRecord(first, first.Locations[1]);
        var otherA = DaggerfallRecordSource.BuildLocationRecord(other, other.Locations[0]);

        // Same slot, different content: identity is the slot, so the id must not move.
        Assert.Equal(a1.FormId, a2.FormId);
        Assert.NotEqual(a1.FormId, b.FormId);
        Assert.NotEqual(a1.FormId, otherA.FormId);
        Assert.Equal((5u << 16) | 1u, ClassicFormIdScheme.IndexOf(b.FormId));
    }

    [Fact]
    public void LocationIndex_RejectsSlotsOutsideTheLayout()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DaggerfallRecordSource.LocationIndex(62, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DaggerfallRecordSource.LocationIndex(0, 0x10000));
        Assert.Equal(0x3D0001u, DaggerfallRecordSource.LocationIndex(61, 1));
    }

    [Fact]
    public void BuildRegionRecord_CountsLocationsByTypeAndDungeons()
    {
        var region = Region(16,
            DaggerfallMapsFixture.Make("Farm", 1280, 1280, locationType: 3, locationId: 1),
            DaggerfallMapsFixture.Make("Keep", 2560, 2560, locationType: 7, locationId: 2, dungeonType: 2,
                dungeon: [new DaggerfallMapsFixture.DungeonBlock(0, 0, 1, 0, true)]),
            DaggerfallMapsFixture.Make("Ruin", 3840, 3840, locationType: 10, locationId: 3, dungeonType: 11,
                dungeon: [new DaggerfallMapsFixture.DungeonBlock(0, 0, 1, 0, true)]),
            DaggerfallMapsFixture.Make("Another Farm", 5120, 5120, locationType: 3, locationId: 4));

        var record = DaggerfallRecordSource.BuildRegionRecord(region);

        Assert.Equal("DREG", record.RecordType);
        Assert.Equal("Wrothgarian_Mountains", record.EditorId);
        Assert.Equal("Wrothgarian Mountains", record.FullName);
        Assert.Equal(DaggerfallRecordSource.RegionDomain, ClassicFormIdScheme.DomainOf(record.FormId));
        Assert.Equal(17u, ClassicFormIdScheme.IndexOf(record.FormId));
        Assert.Equal(16, record.Fields["RegionIndex"]);
        Assert.Equal(4, record.Fields["Locations"]);
        Assert.Equal(2, record.Fields["Dungeons"]);
        Assert.Equal(2, record.Fields["HomeFarms"]);
        Assert.Equal(1, record.Fields["DungeonKeep"]);
        Assert.Equal(1, record.Fields["DungeonRuin"]);
    }

    [Fact]
    public async Task Populate_AndTheAnalyzer_EmitARegionPerRegionAndALocationPerName()
    {
        var regions = new Dictionary<int, IReadOnlyList<DaggerfallMapsFixture.Location>>
        {
            [0] = [DaggerfallMapsFixture.Make("Alpha", 1280, 1280, locationId: 1)],
            [17] =
            [
                DaggerfallMapsFixture.Make("Daggerfall", 26504, 36664, locationId: 50026),
                DaggerfallMapsFixture.Make("Betony Farm", 26600, 36000, locationType: 3, locationId: 7)
            ]
        };

        // Lay the install out as the profile expects: DF\DAGGER holds ARENA2 with both markers.
        var root = Path.Combine(Path.GetTempPath(), "bmt-df-" + Guid.NewGuid().ToString("N"));
        var arena2 = Path.Combine(root, "ARENA2");
        Directory.CreateDirectory(arena2);
        try
        {
            File.WriteAllBytes(Path.Combine(arena2, "MAPS.BSA"), DaggerfallMapsFixture.Archive(regions));
            File.WriteAllBytes(Path.Combine(arena2, "ARCH3D.BSA"), []);

            var records = new RecordCollection { Game = BethesdaGame.Daggerfall };
            DaggerfallRecordSource.Populate(arena2, records);

            Assert.Equal(62 + 3, records.GenericRecords.Count);
            Assert.Equal(62, records.GenericRecords.Count(r => r.RecordType == "DREG"));
            Assert.Equal(3, records.GenericRecords.Count(r => r.RecordType == "DLOC"));
            Assert.Equal(65, records.GenericRecords.Select(r => r.FormId).Distinct().Count());

            // The analyzer resolves the install root from the markers and descends into ARENA2.
            var result = await ClassicGameAnalyzer.LoadAsync(root);
            Assert.Equal(BethesdaGame.Daggerfall, result.Records.Game);
            Assert.Equal(65, result.Records.GenericRecords.Count);

            // ...and from a file inside the data directory too.
            var fromFile = await ClassicGameAnalyzer.LoadAsync(Path.Combine(arena2, "MAPS.BSA"));
            Assert.Equal(65, fromFile.Records.GenericRecords.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Populate_WithoutTheArchive_AddsNothing()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmt-df-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var records = new RecordCollection { Game = BethesdaGame.Daggerfall };
            DaggerfallRecordSource.Populate(directory, records);
            Assert.Empty(records.GenericRecords);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
