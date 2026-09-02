using System.Collections.Generic;
using System.Linq;
using BethesdaMultitool.Core.Formats.Arena;
using Xunit;

namespace BethesdaMultitool.Tests.Core.Formats.Arena;

/// <summary>
///     Grammar vectors for <see cref="ArenaMifFile" />, built to the shapes the retail maps use
///     (verified 2026-09-01 by walking all 21 loose .MIF files: 81 levels, chunk sizes tiling
///     exactly). The one voxel-layer vector reuses an LZHUF stream hand-derived in
///     <c>LzhufCodecTests</c>, so the layer path is covered without an encoder.
/// </summary>
public class ArenaMifFileTests
{
    /// <summary>
    ///     "AAB" — the hand-derived LZHUF vector from <c>LzhufCodecTests</c>. Its first two bytes
    ///     read back as the little-endian voxel id 0x4141.
    /// </summary>
    private static readonly byte[] LzhufAab = [0xE6, 0xE2, 0xF3, 0x80];

    private const int LzhufAabLength = 3;

    private static List<byte> Chunk(string tag, IEnumerable<byte> payload)
    {
        var body = payload.ToList();
        var bytes = new List<byte>(System.Text.Encoding.ASCII.GetBytes(tag));
        bytes.Add((byte)(body.Count & 0xFF));
        bytes.Add((byte)((body.Count >> 8) & 0xFF));
        bytes.AddRange(body);
        return bytes;
    }

    private static List<byte> Header(int width, int depth, int startingLevel = 0, int levelCount = 1)
    {
        var payload = new byte[ArenaMifFile.HeaderPayloadSize];
        payload[18] = (byte)startingLevel;
        payload[19] = (byte)levelCount;
        payload[21] = (byte)(width & 0xFF);
        payload[22] = (byte)((width >> 8) & 0xFF);
        payload[23] = (byte)(depth & 0xFF);
        payload[24] = (byte)((depth >> 8) & 0xFF);
        return Chunk("MHDR", payload);
    }

    private static byte[] Map(List<byte> header, params List<byte>[] levelChunks)
    {
        var bytes = new List<byte>(header);
        foreach (var chunks in levelChunks)
        {
            bytes.AddRange(Chunk("LEVL", chunks));
        }

        return [.. bytes];
    }

    [Fact]
    public void Parse_ReadsDimensionsAndStartingLevelFromTheHeader()
    {
        var map = ArenaMifFile.Parse(Map(Header(64, 48, startingLevel: 2, levelCount: 1)), "T.MIF");

        Assert.Equal(64, map.Width);
        Assert.Equal(48, map.Depth);
        Assert.Equal(2, map.StartingLevelIndex);
        Assert.Equal("T.MIF", map.Name);
    }

    [Fact]
    public void Parse_ReadsStartPoints_AndMarksUnsetSlots()
    {
        var header = Header(16, 16);

        // Start point 0 = (0x0140, 0x1840); the other three slots stay at the origin.
        header[6 + 2] = 0x40;
        header[6 + 3] = 0x01;
        header[6 + 10] = 0x40;
        header[6 + 11] = 0x18;

        var map = ArenaMifFile.Parse(Map(header), "T.MIF");

        Assert.Equal(ArenaMifFile.StartPointCount, map.StartPoints.Count);
        Assert.Equal(new ArenaMifStartPoint(0x0140, 0x1840), map.StartPoints[0]);
        Assert.False(map.StartPoints[0].IsUnset);
        Assert.True(map.StartPoints[1].IsUnset);
    }

    [Fact]
    public void Parse_NonMhdrInput_Throws()
    {
        var bytes = new byte[80];
        "XXXX"u8.CopyTo(bytes);

        var ex = Assert.Throws<InvalidDataException>(() => ArenaMifFile.Parse(bytes, "BAD.MIF"));
        Assert.Contains("MHDR", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_ZeroDimensions_Throws()
    {
        Assert.Throws<InvalidDataException>(() => ArenaMifFile.Parse(Map(Header(0, 0)), "T.MIF"));
    }

    [Fact]
    public void Parse_LevelMetadataChunks_AreDecoded()
    {
        var level = new List<byte>();
        level.AddRange(Chunk("NAME", "level 1\0"u8.ToArray()));
        level.AddRange(Chunk("INFO", "halls1.inf\0"u8.ToArray()));
        level.AddRange(Chunk("NUMF", [11]));

        var map = ArenaMifFile.Parse(Map(Header(8, 8), level), "T.MIF");

        var parsed = Assert.Single(map.Levels);
        Assert.Equal("level 1", parsed.LevelName);
        Assert.Equal("halls1.inf", parsed.InfoFile);
        Assert.Equal(11, parsed.FloorTextureCount);
    }

    [Fact]
    public void Parse_NameAndInfo_StopAtTheNulRatherThanTheDeclaredSize()
    {
        // Retail chunks pad past the terminator; the padding is not part of the string.
        var level = Chunk("INFO", "abc.inf\0\0\0\0"u8.ToArray());

        var map = ArenaMifFile.Parse(Map(Header(8, 8), level), "T.MIF");

        Assert.Equal("abc.inf", Assert.Single(map.Levels).InfoFile);
    }

    [Fact]
    public void Parse_LockTriggerAndTargetTables_SplitIntoFixedSizeRecords()
    {
        var level = new List<byte>();
        level.AddRange(Chunk("LOCK", [3, 4, 5, 6, 7, 8]));
        level.AddRange(Chunk("TRIG", [10, 11, 2, 3, 20, 21, 0xFF, 0xFF]));
        level.AddRange(Chunk("TARG", [30, 31, 32, 33]));

        var parsed = Assert.Single(ArenaMifFile.Parse(Map(Header(64, 64), level), "T.MIF").Levels);

        Assert.Equal([new ArenaMifLock(3, 4, 5), new ArenaMifLock(6, 7, 8)], parsed.Locks);
        Assert.Equal([new ArenaMifTarget(30, 31), new ArenaMifTarget(32, 33)], parsed.Targets);

        Assert.Equal(2, parsed.Triggers.Count);
        Assert.Equal(new ArenaMifTrigger(10, 11, 2, 3), parsed.Triggers[0]);
        Assert.True(parsed.Triggers[0].HasText);
        Assert.True(parsed.Triggers[0].HasSound);

        // 0xFF is -1: an inactive text/sound reference, not index 255.
        Assert.Equal(-1, parsed.Triggers[1].TextIndex);
        Assert.False(parsed.Triggers[1].HasText);
        Assert.False(parsed.Triggers[1].HasSound);
    }

    [Fact]
    public void Parse_VoxelLayer_DecompressesLzhufIntoLittleEndianVoxelIds()
    {
        // A layer payload is: u16 uncompressed size, then the LZHUF stream. The chunk's own size
        // field counts both, which is why it is the stream length plus two.
        var payload = new List<byte> { LzhufAabLength & 0xFF, (LzhufAabLength >> 8) & 0xFF };
        payload.AddRange(LzhufAab);

        var map = ArenaMifFile.Parse(Map(Header(1, 1), Chunk("FLOR", payload)), "T.MIF");

        var level = Assert.Single(map.Levels);
        Assert.Equal(0x4141, Assert.Single(level.Floor));
        Assert.Empty(level.Map1);
        Assert.Empty(level.Map2);
    }

    [Fact]
    public void Parse_UndecodedChunks_AreKeptAsRawPayloads()
    {
        var level = new List<byte>();
        level.AddRange(Chunk("FLAT", [1, 2, 3]));
        level.AddRange(Chunk("INNS", [9]));

        var parsed = Assert.Single(ArenaMifFile.Parse(Map(Header(8, 8), level), "T.MIF").Levels);

        Assert.Equal([1, 2, 3], parsed.UndecodedChunks["FLAT"]);
        Assert.Equal([9], parsed.UndecodedChunks["INNS"]);
    }

    [Fact]
    public void Parse_MultipleLevels_AreReadInOrder()
    {
        var first = Chunk("NAME", "level 1\0"u8.ToArray());
        var second = Chunk("NAME", "level 2\0"u8.ToArray());

        var map = ArenaMifFile.Parse(Map(Header(8, 8, levelCount: 2), first, second), "T.MIF");

        Assert.Equal(2, map.Levels.Count);
        Assert.Equal("level 1", map.Levels[0].LevelName);
        Assert.Equal("level 2", map.Levels[1].LevelName);
    }

    [Fact]
    public void Parse_HeaderLevelCountDisagreeingWithReality_KeepsBothNumbers()
    {
        // WILD.MIF's own level size is six bytes short of the truth, so the actual blocks are
        // authoritative and the declared count is reported rather than trusted.
        var map = ArenaMifFile.Parse(Map(Header(8, 8, levelCount: 5), Chunk("NUMF", [1])), "T.MIF");

        Assert.Equal(5, map.DeclaredLevelCount);
        Assert.Single(map.Levels);
    }

    [Fact]
    public void Parse_UnknownTag_StopsThatLevelWithoutDiscardingWhatWasRead()
    {
        var level = new List<byte>();
        level.AddRange(Chunk("NUMF", [7]));
        level.AddRange(Chunk("ZZZZ", [1, 2, 3, 4]));

        var map = ArenaMifFile.Parse(Map(Header(8, 8), level), "T.MIF");

        Assert.Equal(7, Assert.Single(map.Levels).FloorTextureCount);
    }

    [Fact]
    public void VoxelAt_IsRowMajor_AndZeroOutsideTheLayer()
    {
        ushort[] layer = [1, 2, 3, 4, 5, 6];

        Assert.Equal(1, ArenaMifLevel.VoxelAt(layer, 3, 0, 0));
        Assert.Equal(3, ArenaMifLevel.VoxelAt(layer, 3, 2, 0));
        Assert.Equal(4, ArenaMifLevel.VoxelAt(layer, 3, 0, 1));
        Assert.Equal(0, ArenaMifLevel.VoxelAt(layer, 3, 0, 5));
        Assert.Equal(0, ArenaMifLevel.VoxelAt([], 3, 0, 0));
    }
}
