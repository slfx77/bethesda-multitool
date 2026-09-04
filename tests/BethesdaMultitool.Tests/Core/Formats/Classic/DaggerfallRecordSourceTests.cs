using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BethesdaMultitool.Core.Formats.Classic;
using BethesdaMultitool.Core.Formats.Daggerfall;
using BethesdaMultitool.Core.Formats.Esm.Models;
using BethesdaMultitool.Core.Formats.Xngine.Mesh;
using BethesdaMultitool.Core.Games;
using BethesdaMultitool.Tests.Core.Formats.Audio;
using BethesdaMultitool.Tests.Core.Formats.Daggerfall;
using BethesdaMultitool.Tests.Core.Formats.Xngine.Mesh;
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
        Assert.True((bool)record.Fields["Discovered"]!);
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
            await File.WriteAllBytesAsync(Path.Combine(arena2, "MAPS.BSA"), DaggerfallMapsFixture.Archive(regions), TestContext.Current.CancellationToken);

            // The second install marker: a one-record number-record XnGine BSA (an empty one is not
            // a valid archive — the probe needs a directory to tile) holding the fixture quad.
            var quad = XnGineMeshFixture.Quad();
            byte[] arch3d = [0x01, 0x00, 0x00, 0x02, .. quad, .. BitConverter.GetBytes(5000u), .. BitConverter.GetBytes(quad.Length)];
            await File.WriteAllBytesAsync(Path.Combine(arena2, "ARCH3D.BSA"), arch3d, TestContext.Current.CancellationToken);

            var records = new RecordCollection { Game = BethesdaGame.Daggerfall };
            DaggerfallRecordSource.Populate(arena2, records, TestContext.Current.CancellationToken);

            Assert.Equal(62 + 3 + 1, records.GenericRecords.Count);
            Assert.Equal(62, records.GenericRecords.Count(r => r.RecordType == "DREG"));
            Assert.Equal(3, records.GenericRecords.Count(r => r.RecordType == "DLOC"));
            Assert.Equal(1, records.GenericRecords.Count(r => r.RecordType == "DMSH"));
            Assert.Equal(66, records.GenericRecords.Select(r => r.FormId).Distinct().Count());

            // The analyzer resolves the install root from the markers and descends into ARENA2.
            var result = await ClassicGameAnalyzer.LoadAsync(root, TestContext.Current.CancellationToken);
            Assert.Equal(BethesdaGame.Daggerfall, result.Records.Game);
            Assert.Equal(66, result.Records.GenericRecords.Count);

            // ...and from a file inside the data directory too.
            var fromFile = await ClassicGameAnalyzer.LoadAsync(Path.Combine(arena2, "MAPS.BSA"), TestContext.Current.CancellationToken);
            Assert.Equal(66, fromFile.Records.GenericRecords.Count);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void BuildTextRecord_CarriesIdVariantsAndText()
    {
        var text = DaggerfallTextFile.Parse(DaggerfallTextFixture.TextRsc(
            (5, [.. "PERSONALITY"u8, 0xFD, .. " Personality governs"u8, 0xFC, 0xFF, .. "Alt"u8, 0xF8])));

        var record = DaggerfallRecordSource.BuildTextRecord(text.Records[0]);

        Assert.Equal("DTXT", record.RecordType);
        Assert.Equal("TEXT0005", record.EditorId);
        Assert.Equal("PERSONALITY Personality governs", record.FullName);
        Assert.Equal(DaggerfallRecordSource.TextDomain, ClassicFormIdScheme.DomainOf(record.FormId));
        Assert.Equal(5u, ClassicFormIdScheme.IndexOf(record.FormId));
        Assert.Equal(5, record.Fields["Id"]);
        Assert.Equal(2, record.Fields["Variants"]);
        Assert.Equal("PERSONALITY Personality governs", record.Fields["Text00"]);
        Assert.Equal("Alt", record.Fields["Text01"]);
        Assert.True((bool)record.Fields["InputCursor"]!);
    }

    [Fact]
    public void BuildBookRecord_CarriesHeaderAndPages()
    {
        var book = DaggerfallBookFile.Parse(
            DaggerfallTextFixture.Book("A Tale of Kieran", "Vegepythicus, editor", "naughty", 400, 2,
                DaggerfallTextFixture.Page("Once upon"), [0x00, .. "a time."u8, 0x00, 0xF6]),
            "BOK00001.TXT");

        var record = DaggerfallRecordSource.BuildBookRecord(book);

        Assert.Equal("DBOK", record.RecordType);
        Assert.Equal("BOK00001", record.EditorId);
        Assert.Equal("A Tale of Kieran", record.FullName);
        Assert.Equal(DaggerfallRecordSource.BookDomain, ClassicFormIdScheme.DomainOf(record.FormId));
        Assert.Equal(1u, ClassicFormIdScheme.IndexOf(record.FormId));
        Assert.Equal("Vegepythicus, editor", record.Fields["Author"]);
        Assert.Equal(2, record.Fields["Pages"]);
        Assert.Equal(400u, record.Fields["Price"]);
        Assert.Equal(2, record.Fields["Unknown1"]);
        Assert.True((bool)record.Fields["Naughty"]!);
        Assert.Equal("Once upon", record.Fields["Page00"]);
        Assert.Equal("a time.", record.Fields["Page01"]);

        var tame = DaggerfallRecordSource.BuildBookRecord(DaggerfallBookFile.Parse(
            DaggerfallTextFixture.Book("T", "A", "", 1, 1, DaggerfallTextFixture.Page("x")), "BOK00002.TXT"));
        Assert.False(tame.Fields.ContainsKey("Naughty"));
    }

    [Fact]
    public void Populate_ReadsTextAndBooks_WithoutTheMapsArchive()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "bmt-df-text-" + Guid.NewGuid().ToString("N"));
        var books = Path.Combine(dataRoot, "BOOKS");
        Directory.CreateDirectory(books);
        try
        {
            File.WriteAllBytes(Path.Combine(dataRoot, "TEXT.RSC"), DaggerfallTextFixture.TextRsc(
                (0, DaggerfallTextFixture.Bytes("zero")),
                (1, DaggerfallTextFixture.Bytes("one"))));
            File.WriteAllBytes(Path.Combine(books, "BOK00000.TXT"),
                DaggerfallTextFixture.Book("T", "A", "", 1, 1, DaggerfallTextFixture.Page("x")));
            File.WriteAllBytes(Path.Combine(books, "README.TXT"), DaggerfallTextFixture.Bytes("not a book"));

            var records = new RecordCollection { Game = BethesdaGame.Daggerfall };
            DaggerfallRecordSource.Populate(dataRoot, records, TestContext.Current.CancellationToken);

            Assert.Equal(3, records.GenericRecords.Count);
            Assert.Equal(2, records.GenericRecords.Count(r => r.RecordType == "DTXT"));
            Assert.Equal(1, records.GenericRecords.Count(r => r.RecordType == "DBOK"));
            Assert.Equal(3, records.GenericRecords.Select(r => r.FormId).Distinct().Count());
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildMeshRecord_IdentityIsTheArchiveIndex()
    {
        var mesh = XnGineMesh.Parse(XnGineMeshFixture.Quad(), 44005);

        var record = DaggerfallRecordSource.BuildMeshRecord(17, mesh);

        Assert.Equal("DMSH", record.RecordType);
        Assert.Equal("MESH44005", record.EditorId);
        Assert.Null(record.FullName);
        Assert.Equal(DaggerfallRecordSource.MeshDomain, ClassicFormIdScheme.DomainOf(record.FormId));
        Assert.Equal(17u, ClassicFormIdScheme.IndexOf(record.FormId));
        Assert.Equal(44005, record.Fields["ObjectId"]);
        Assert.Equal("v2.7", record.Fields["Version"]);
        Assert.Equal(4, record.Fields["Points"]);
        Assert.Equal(1, record.Fields["Planes"]);
        Assert.Equal(2, record.Fields["Triangles"]);
        Assert.Equal("024:3", record.Fields["Textures"]);
        Assert.Equal(1f, record.Fields["SizeX"]);
        Assert.Equal(0f, record.Fields["SizeY"]);
    }

    [Fact]
    public void BuildBlockRecord_SummarisesRmbAndRdb()
    {
        var rmb = DaggerfallBlockFixture.Rmb("TVRNAS00", [
            new DaggerfallBlockFixture.SubRecord(0, 0, 0, 0x0F, 5,
                new DaggerfallBlockFixture.BlockData(
                    [new DaggerfallBlockFixture.Model(310, 6, 3, 0, 0, 0, 0)],
                    [new DaggerfallBlockFixture.Flat(0, 0, 0, 0, 0, 0)],
                    Doors: [new DaggerfallBlockFixture.Door(0, 0, 0, 0, 0, 0)]),
                new DaggerfallBlockFixture.BlockData([], [], People: [new DaggerfallBlockFixture.Flat(0, 0, 0, 0, 0, 0)])),
            new DaggerfallBlockFixture.SubRecord(0, 0, 0, 0x0F, 5, new DaggerfallBlockFixture.BlockData([], []), new DaggerfallBlockFixture.BlockData([], []))
        ], misc3d: [new DaggerfallBlockFixture.Model(4, 1, 0, 0, 0, 0, 0)]);
        var rdb = DaggerfallBlockFixture.Rdb(1, 1, [("72100", "DOR")],
            new Dictionary<int, IReadOnlyList<DaggerfallBlockFixture.RdbObject>>
            {
                [0] = [new DaggerfallBlockFixture.RdbObject(1, 0, 0, 0, ActionNextObject: 1), new DaggerfallBlockFixture.RdbObject(2, 0, 0, 0)]
            });

        var directory = Path.Combine(Path.GetTempPath(), "bmt-dblk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "BLOCKS.BSA"),
                DaggerfallBlockFixture.Archive(("TVRNAS00.RMB", rmb), ("B0000000.RDB", rdb), ("B0000000.RDI", new byte[512]), ("FOO", [1, 2, 3])));

            var records = new RecordCollection { Game = BethesdaGame.Daggerfall };
            DaggerfallRecordSource.Populate(directory, records, TestContext.Current.CancellationToken);

            Assert.Equal(4, records.GenericRecords.Count);
            Assert.All(records.GenericRecords, r => Assert.Equal("DBLK", r.RecordType));
            Assert.Equal(4, records.GenericRecords.Select(r => r.FormId).Distinct().Count());

            var rmbRecord = records.GenericRecords[0];
            Assert.Equal("TVRNAS00_RMB", rmbRecord.EditorId);
            Assert.Equal("TVRNAS00", rmbRecord.FullName);
            Assert.Equal(DaggerfallRecordSource.BlockDomain, ClassicFormIdScheme.DomainOf(rmbRecord.FormId));
            Assert.Equal("RMB", rmbRecord.Fields["Kind"]);
            Assert.Equal(2, rmbRecord.Fields["SubBlocks"]);
            Assert.Equal(2, rmbRecord.Fields["Models"]);
            Assert.Equal(1, rmbRecord.Fields["Flats"]);
            Assert.Equal(1, rmbRecord.Fields["People"]);
            Assert.Equal(1, rmbRecord.Fields["Doors"]);
            Assert.Equal("Tavern=2", rmbRecord.Fields["BuildingTypes"]);

            var rdbRecord = records.GenericRecords[1];
            Assert.Equal("RDB", rdbRecord.Fields["Kind"]);
            Assert.Equal("Border", rdbRecord.Fields["DungeonType"]);
            Assert.Equal(2, rdbRecord.Fields["Objects"]);
            Assert.Equal(1, rdbRecord.Fields["Models"]);
            Assert.Equal(1, rdbRecord.Fields["Lights"]);
            Assert.Equal(1, rdbRecord.Fields["Actions"]);
            Assert.Equal(1, rdbRecord.Fields["ModelIds"]);

            Assert.Equal("RDI", records.GenericRecords[2].Fields["Kind"]);
            Assert.Equal("UNKNOWN", records.GenericRecords[3].Fields["Kind"]);
            Assert.Equal(3, records.GenericRecords[3].Fields["Bytes"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void BuildQuestRecord_CarriesMessagesAndTheUndecodedQbn()
    {
        var quest = DaggerfallQuestFile.Create("S0000002",
            DaggerfallTextFixture.TextRsc(
                (1000, DaggerfallTextFixture.Bytes("You must find the vampire.")),
                (1002, DaggerfallTextFixture.Bytes("Well done."))),
            [0, 0, 0, 0, 0, 0, 0, 0, 2, 0, 0, 0, 0x3C, 0x00, .. new byte[100]]);

        var record = DaggerfallRecordSource.BuildQuestRecord(quest);

        Assert.Equal("DQST", record.RecordType);
        Assert.Equal("S0000002", record.EditorId);
        Assert.Equal("You must find the vampire.", record.FullName);
        Assert.Equal(DaggerfallRecordSource.QuestDomain, ClassicFormIdScheme.DomainOf(record.FormId));
        Assert.Equal(2, record.Fields["Messages"]);
        Assert.Equal(114, record.Fields["CompiledBytes"]);
        Assert.Equal("You must find the vampire.", record.Fields["Message1000"]);
        Assert.Equal("Well done.", record.Fields["Message1002"]);
        Assert.StartsWith("0000 0000 0000 0000 0002", (string)record.Fields["CompiledHeader"]!, StringComparison.Ordinal);

        // Identity is the base name, not the content.
        var renamedContent = DaggerfallRecordSource.BuildQuestRecord(
            DaggerfallQuestFile.Create("S0000002", DaggerfallTextFixture.TextRsc((9, DaggerfallTextFixture.Bytes("different"))), null));
        Assert.Equal(record.FormId, renamedContent.FormId);
        Assert.NotEqual(record.FormId,
            DaggerfallRecordSource.BuildQuestRecord(DaggerfallQuestFile.Create("S0000003", null, null)).FormId);
    }

    [Fact]
    public void Populate_ReadsQuestsBesideTheOtherSources()
    {
        var dataRoot = Path.Combine(Path.GetTempPath(), "bmt-df-quests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        try
        {
            File.WriteAllBytes(Path.Combine(dataRoot, "S0000002.QRC"), DaggerfallTextFixture.TextRsc((1000, DaggerfallTextFixture.Bytes("quest text"))));
            File.WriteAllBytes(Path.Combine(dataRoot, "S0000002.QBN"), new byte[64]);
            File.WriteAllBytes(Path.Combine(dataRoot, "$CUREVAM.QBN"), new byte[64]);

            var records = new RecordCollection { Game = BethesdaGame.Daggerfall };
            DaggerfallRecordSource.Populate(dataRoot, records, TestContext.Current.CancellationToken);

            Assert.Equal(2, records.GenericRecords.Count);
            Assert.All(records.GenericRecords, r => Assert.Equal("DQST", r.RecordType));
            Assert.Equal(2, records.GenericRecords.Select(r => r.FormId).Distinct().Count());
            Assert.Equal("_CUREVAM", records.GenericRecords[0].EditorId);
            Assert.Equal(0, records.GenericRecords[0].Fields["Messages"]);
            Assert.Equal(1, records.GenericRecords[1].Fields["Messages"]);
        }
        finally
        {
            Directory.Delete(dataRoot, recursive: true);
        }
    }

    [Fact]
    public void BuildSoundAndMusicRecords_CarryTheirFormatFacts()
    {
        var directory = Path.Combine(Path.GetTempPath(), "bmt-df-audio-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllBytes(Path.Combine(directory, "DAGGER.SND"),
                DaggerfallSoundFileTests.Archive((3, new byte[11025]), (220, [128, 128])));
            File.WriteAllBytes(Path.Combine(directory, "MIDI.BSA"),
                DaggerfallBlockFixture.Archive(("D1.HMI", HmiFileTests.Song("HMI-MIDISONG061595", 64, 16))));

            var records = new RecordCollection { Game = BethesdaGame.Daggerfall };
            DaggerfallRecordSource.Populate(directory, records, TestContext.Current.CancellationToken);

            Assert.Equal(3, records.GenericRecords.Count);
            var sound = records.GenericRecords[0];
            Assert.Equal("DSND", sound.RecordType);
            Assert.Equal("SOUND3", sound.EditorId);
            Assert.Equal(DaggerfallRecordSource.SoundDomain, ClassicFormIdScheme.DomainOf(sound.FormId));
            Assert.Equal(0u, ClassicFormIdScheme.IndexOf(sound.FormId));
            Assert.Equal(11025, sound.Fields["Samples"]);
            Assert.Equal(1d, sound.Fields["Seconds"]);
            Assert.Equal("11025 Hz, 8-bit unsigned, mono", sound.Fields["Format"]);
            Assert.Equal(1u, ClassicFormIdScheme.IndexOf(records.GenericRecords[1].FormId));

            var music = records.GenericRecords[2];
            Assert.Equal("DMUS", music.RecordType);
            Assert.Equal("D1_HMI", music.EditorId);
            Assert.Equal(DaggerfallRecordSource.MusicDomain, ClassicFormIdScheme.DomainOf(music.FormId));
            Assert.Equal("HMI-MIDISONG061595", music.Fields["Tag"]);
            Assert.Equal(2, music.Fields["Tracks"]);
            Assert.Equal(77, music.Fields["LargestTrack"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
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
            DaggerfallRecordSource.Populate(directory, records, TestContext.Current.CancellationToken);
            Assert.Empty(records.GenericRecords);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
