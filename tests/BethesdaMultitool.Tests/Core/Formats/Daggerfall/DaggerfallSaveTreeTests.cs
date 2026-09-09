using System.Buffers.Binary;
using BethesdaMultitool.Core.Formats.Daggerfall;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Daggerfall;

/// <summary>
///     Synthetic vectors for <see cref="DaggerfallSaveTree" />: a hand-built file with two records,
///     a mid-stream separator and a one-entry trailer, plus the ways a file is refused.
/// </summary>
public class DaggerfallSaveTreeTests
{
    /// <summary>A 71-byte record root with the fields this suite pins.</summary>
    internal static byte[] Root(
        byte type,
        uint recordId,
        uint parentId = 0,
        byte trailerFlag = 0,
        ushort spriteIndex = 0,
        int x = 0,
        int y = 0,
        int z = 0)
    {
        var root = new byte[DaggerfallSaveTree.RecordRootLength];
        root[0] = type;
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(7), x);
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(11), y);
        BinaryPrimitives.WriteInt32LittleEndian(root.AsSpan(15), z);
        BinaryPrimitives.WriteUInt16LittleEndian(root.AsSpan(27), spriteIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(31), recordId);
        root[35] = trailerFlag;
        BinaryPrimitives.WriteUInt32LittleEndian(root.AsSpan(39), parentId);
        return root;
    }

    /// <summary>A 39-byte trailer entry: the id's low half, a payload, then the whole id.</summary>
    internal static byte[] TrailerEntry(uint ownerId, byte payloadFirst)
    {
        var entry = new byte[DaggerfallSaveTree.TrailerEntryLength];
        BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)ownerId);
        entry[2] = payloadFirst;
        BinaryPrimitives.WriteUInt32LittleEndian(entry.AsSpan(35), ownerId);
        return entry;
    }

    /// <summary>
    ///     Assembles a file: the 19-byte header, an empty building block, the given records and
    ///     separators (a null entry is a zero-length prefix), then the trailer.
    /// </summary>
    internal static byte[] File(IEnumerable<byte[]?> records, IEnumerable<byte[]>? trailerEntries,
        int version = DaggerfallSaveTree.Version, ushort dungeonIndex = 30, byte environment = 3)
    {
        var bytes = new List<byte>();
        var header = new byte[DaggerfallSaveTree.HeaderLength];
        BinaryPrimitives.WriteInt32LittleEndian(header, version);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 3_591_871);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), -256);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(12), 11_188_666);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(16), dungeonIndex);
        header[18] = environment;
        bytes.AddRange(header);
        bytes.AddRange(new byte[4]);

        foreach (var record in records)
        {
            if (record is null)
            {
                bytes.AddRange(new byte[4]);
                continue;
            }

            var length = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(length, record.Length);
            bytes.AddRange(length);
            bytes.AddRange(record);
        }

        if (trailerEntries is not null)
        {
            var entries = trailerEntries.ToList();
            bytes.AddRange(new byte[4]);
            var count = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)entries.Count);
            bytes.AddRange(count);
            foreach (var entry in entries)
            {
                bytes.AddRange(entry);
            }

            bytes.AddRange(new byte[4]);
        }

        return [.. bytes];
    }

    private static byte[] TwoRecordFile()
    {
        var move = Root(0x04, 0x00012712, x: 3_591_871, y: -256, z: 11_188_666);
        var marker = Root(0x22, 0xC38200ED, 0x00012712, DaggerfallSaveTree.TrailerOwnerFlag, (199 << 7) | 15);
        return File(
            [[.. move, 0x00], null, [.. marker, .. new byte[8]]],
            [TrailerEntry(0xC38200ED, 0x04)]);
    }

    [Fact]
    public void Parse_TilesAHandBuiltFileAndKeepsBothSeparators()
    {
        var tree = DaggerfallSaveTree.Parse(TwoRecordFile(), "SAVETREE.DAT");

        Assert.Equal(2, tree.Records.Count);
        // One mid-stream separator plus the one that precedes the trailer.
        Assert.Equal(2, tree.SeparatorCount);
        Assert.True(tree.HasTrailer);
        Assert.Single(tree.TrailerEntries);
        Assert.Empty(tree.BuildingRecords);
        Assert.Equal(0, tree.DuplicateIdCount);
        Assert.Equal(0, tree.UnresolvedTrailerOwnerCount);
        // 19 header + 4 building + (4 + 72) + 4 + (4 + 79) + 4 + 4 + 39 + 4.
        Assert.Equal(237, tree.Length);
    }

    [Fact]
    public void Parse_DecodesTheHeaderAndTheRecordRoots()
    {
        var tree = DaggerfallSaveTree.Parse(TwoRecordFile(), "SAVETREE.DAT");

        Assert.Equal(0x126, tree.Header.Version);
        Assert.Equal(3_591_871, tree.Header.X);
        Assert.Equal(-256, tree.Header.Y);
        Assert.Equal(11_188_666, tree.Header.Z);
        Assert.Equal(30, tree.Header.DungeonIndex);
        Assert.Equal(DaggerfallSaveEnvironment.Dungeon, tree.Header.EnvironmentKind);

        var move = tree.Records[0];
        Assert.Equal(DaggerfallSaveRecordType.Move, move.Type);
        Assert.Equal(0x00012712u, move.RecordId);
        Assert.Equal(1, move.Data.Length);
        Assert.Equal(3_591_871, move.X);
        Assert.False(move.HasTrailerFlag);

        var marker = tree.Records[1];
        Assert.Equal(DaggerfallSaveRecordType.Marker, marker.Type);
        Assert.Equal(0xC382, marker.RecordIdHigh);
        Assert.Equal(0x00ED, marker.RecordIdLow);
        Assert.Equal(199, marker.SpriteArchive);
        Assert.Equal(15, marker.SpriteRecord);
        Assert.True(marker.HasTrailerFlag);
        Assert.Equal(8, marker.Data.Length);
    }

    [Fact]
    public void Parse_LinksParentsChildrenAndTrailerOwners()
    {
        var tree = DaggerfallSaveTree.Parse(TwoRecordFile(), "SAVETREE.DAT");

        var move = tree.Records[0];
        var marker = tree.Records[1];
        Assert.Same(move, marker.Parent);
        Assert.Same(marker, Assert.Single(move.Children));
        Assert.Single(tree.RootRecords, r => ReferenceEquals(r, move));
        Assert.Same(marker, tree.TrailerEntries[0].Owner);
        Assert.Same(tree.TrailerEntries[0], marker.TrailerEntry);
        Assert.Equal(0x04, tree.TrailerEntries[0].Payload.Span[0]);
        Assert.Same(marker, tree.FindById(0xC38200ED));
        Assert.Null(tree.FindById(0xDEADBEEF));
    }

    [Fact]
    public void Parse_CountsDuplicateIdsAndKeepsTheFirst()
    {
        var first = Root(0x34, 0xC382FA01);
        var second = Root(0x28, 0xC382FA01);
        var tree = DaggerfallSaveTree.Parse(
            File([[.. first, 0x00], [.. second, .. new byte[27]]], Array.Empty<byte[]>()),
            "SAVETREE.DAT");

        Assert.Equal(2, tree.Records.Count);
        Assert.Equal(1, tree.DuplicateIdCount);
        Assert.Equal(DaggerfallSaveRecordType.Container, tree.FindById(0xC382FA01)!.Type);
    }

    [Fact]
    public void Parse_BuildsTheCensusByTypeId()
    {
        var tree = DaggerfallSaveTree.Parse(TwoRecordFile(), "SAVETREE.DAT");

        Assert.Collection(
            tree.Census,
            move =>
            {
                Assert.Equal(0x04, move.TypeId);
                Assert.Equal(1, move.Count);
                Assert.Equal(72, move.TotalBytes);
            },
            marker =>
            {
                Assert.Equal(0x22, marker.TypeId);
                Assert.Equal(1, marker.Count);
                Assert.Equal(79, marker.TotalBytes);
            });
    }

    [Fact]
    public void Parse_ReadsTheBuildingBlockWhenOneIsPresent()
    {
        var building = new byte[DaggerfallSaveTree.BuildingRecordLength];
        BinaryPrimitives.WriteUInt16LittleEndian(building, 4_242);
        BinaryPrimitives.WriteUInt16LittleEndian(building.AsSpan(18), 366);
        BinaryPrimitives.WriteUInt16LittleEndian(building.AsSpan(22), 50_049);
        building[24] = 17;
        building[25] = 5;

        var withoutBuilding = File([Root(0x01, 0xC3810001)], null);
        var prefix = DaggerfallSaveTree.HeaderLength + 4;
        var bytes = new byte[withoutBuilding.Length + DaggerfallSaveTree.BuildingRecordLength];
        withoutBuilding.AsSpan(0, prefix).CopyTo(bytes);
        building.CopyTo(bytes.AsSpan(prefix));
        withoutBuilding.AsSpan(prefix).CopyTo(bytes.AsSpan(prefix + DaggerfallSaveTree.BuildingRecordLength));
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(DaggerfallSaveTree.HeaderLength),
            DaggerfallSaveTree.BuildingRecordLength);

        var tree = DaggerfallSaveTree.Parse(bytes, "SAVETREE.DAT");

        var record = Assert.Single(tree.BuildingRecords);
        Assert.Equal(4_242, record.NameSeed);
        Assert.Equal(366, record.FactionId);
        Assert.Equal(50_049, record.LocationId);
        Assert.Equal(17, record.BuildingType);
        Assert.Equal(5, record.Quality);
        Assert.False(tree.HasTrailer);
    }

    [Fact]
    public void IsSaveTree_ProbesTheVersionWord()
    {
        Assert.True(DaggerfallSaveTree.IsSaveTree(TwoRecordFile()));
        Assert.False(DaggerfallSaveTree.IsSaveTree(new byte[23]));
        Assert.False(DaggerfallSaveTree.IsSaveTree(new byte[8]));
    }

    [Fact]
    public void Parse_RejectsAVersionThatIsNotRetail()
    {
        var bytes = TwoRecordFile();
        BinaryPrimitives.WriteInt32LittleEndian(bytes, 0x125);

        var error = Assert.Throws<InvalidDataException>(() => DaggerfallSaveTree.Parse(bytes, "SAVETREE.DAT"));
        Assert.Contains("0x125", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsABuildingBlockThatIsNotWholeRecords()
    {
        var bytes = TwoRecordFile();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(DaggerfallSaveTree.HeaderLength), 25);

        var error = Assert.Throws<InvalidDataException>(() => DaggerfallSaveTree.Parse(bytes, "SAVETREE.DAT"));
        Assert.Contains("26-byte records", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_RejectsARecordThatOverrunsTheFile()
    {
        var bytes = TwoRecordFile();
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(DaggerfallSaveTree.HeaderLength + 4), 10_000);

        Assert.Throws<InvalidDataException>(() => DaggerfallSaveTree.Parse(bytes, "SAVETREE.DAT"));
    }

    [Fact]
    public void Parse_RejectsARecordShorterThanItsRoot()
    {
        var bytes = File([new byte[70]], null);

        var error = Assert.Throws<InvalidDataException>(() => DaggerfallSaveTree.Parse(bytes, "SAVETREE.DAT"));
        Assert.Contains("71-byte root", error.Message, StringComparison.Ordinal);
    }

    /// <summary>
    ///     The trailer is accepted only when every entry's leading u16 is the low half of its
    ///     trailing u32. Corrupting one word must break the whole file rather than let a stray
    ///     count be read as a trailer — the check that would have caught a wrong entry length.
    /// </summary>
    [Fact]
    public void Parse_RefusesATrailerWhoseEntryIdsDisagree()
    {
        var bytes = TwoRecordFile();
        var entryStart = bytes.Length - 4 - DaggerfallSaveTree.TrailerEntryLength;
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(entryStart), 0x1234);

        Assert.Throws<InvalidDataException>(() => DaggerfallSaveTree.Parse(bytes, "SAVETREE.DAT"));
    }

    /// <summary>A trailer count that does not land on EOF is not a trailer.</summary>
    [Fact]
    public void Parse_RefusesATrailerCountThatDoesNotReachEndOfFile()
    {
        var bytes = TwoRecordFile();
        var countStart = bytes.Length - 4 - DaggerfallSaveTree.TrailerEntryLength - 4;
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(countStart), 2);

        Assert.Throws<InvalidDataException>(() => DaggerfallSaveTree.Parse(bytes, "SAVETREE.DAT"));
    }

    /// <summary>A file with no trailer at all still has to tile.</summary>
    [Fact]
    public void Parse_AcceptsAFileThatEndsOnARecord()
    {
        var tree = DaggerfallSaveTree.Parse(File([Root(0x04, 0x00012712)], null), "SAVETREE.DAT");

        Assert.Single(tree.Records);
        Assert.False(tree.HasTrailer);
        Assert.Empty(tree.TrailerEntries);
        Assert.Equal(0, tree.SeparatorCount);
    }
}